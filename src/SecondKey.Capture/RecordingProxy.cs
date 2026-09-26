using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Capture;
using Yarp.ReverseProxy.Forwarder;

namespace SecondKey.Capture;

/// <summary>
/// C1, phase 01: a recording proxy in front of the legacy system. It forwards every request
/// unchanged (except that it asks for an uncompressed answer, so the recording is readable),
/// hands the client the legacy system's answer unchanged, and appends the exchange to a
/// *.skcap file. The legacy system is untouched: no code change, no recompilation, only a
/// different address for its clients (design rule 4).
/// </summary>
public sealed class RecordingProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly CaptureWriter _writer;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _skipped;
    private int _stopping;
    private int _disposed;

    private RecordingProxy(WebApplication app, CaptureWriter writer, CaptureHeader header)
    {
        _app = app;
        _writer = writer;
        Header = header;
    }

    public CaptureHeader Header { get; }

    /// <summary>Exchanges written so far.</summary>
    public long Recorded => _writer.Count;

    /// <summary>Requests the legacy system did not answer (the proxy's own 502/504s are never recorded as behaviour).</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>The address the proxy actually listens on (useful when the port was 0).</summary>
    public Uri Address { get; private set; } = new("http://127.0.0.1/");

    /// <summary>
    /// Completes when the proxy has stopped — by <see cref="CaptureOptions.ExitAfter"/> or
    /// <see cref="StopAsync"/> — and the recording is flushed and closed.
    /// </summary>
    public Task Completion => _done.Task;

    public static async Task<RecordingProxy> StartAsync(CaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var header = new CaptureHeader
        {
            CaptureId = string.Create(CultureInfo.InvariantCulture, $"cap-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3))}"),
            StartedAt = DateTimeOffset.UtcNow,
            Tool = ToolInfo.Current,
            Source = new CaptureSource { Kind = "http-proxy", Target = options.Target.ToString(), Listen = options.Listen.ToString() },
            Sanitization = new CaptureSanitization
            {
                RedactedHeaders = options.RedactHeaders.Select(h => h.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList(),
                MaxBodyBytes = options.MaxBodyBytes,
            },
            SessionKeys = options.SessionKeys.Count > 0 ? options.SessionKeys : null,
        };
        var writer = await CaptureWriter.CreateAsync(options.Output, header, cancellationToken).ConfigureAwait(false);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(options.Listen.ToString());
        builder.Logging.ClearProviders();
        builder.Services.AddHttpForwarder();
        var app = builder.Build();
        var proxy = new RecordingProxy(app, writer, header);

        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        });
        var sessions = new SessionTracker(options.SessionKeys);
        var redacted = new HashSet<string>(header.Sanitization.RedactedHeaders!, StringComparer.Ordinal);
        var target = options.Target.ToString().TrimEnd('/');

        app.Run(context => proxy.HandleAsync(context, forwarder, invoker, target, sessions, redacted, options));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        proxy.Address = new Uri(app.Urls.First());
        return proxy;
    }

    /// <summary>Stops listening, lets in-flight exchanges finish, and closes the recording. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            try
            {
                await _app.StopAsync().ConfigureAwait(false);
            }
            finally
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
                _done.TrySetResult();
            }
        }

        await _done.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(
        HttpContext context,
        IHttpForwarder forwarder,
        HttpMessageInvoker invoker,
        string target,
        SessionTracker sessions,
        HashSet<string> redacted,
        CaptureOptions options)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();

        context.Request.EnableBuffering();
        var (requestBytes, requestSize, requestTruncated) = await ReadLimitedAsync(context.Request.Body, options.MaxBodyBytes, context.RequestAborted).ConfigureAwait(false);
        context.Request.Body.Position = 0;
        var requestHeaders = Headers(context.Request.Headers);

        var originalBody = context.Response.Body;
        await using var tee = new TeeStream(originalBody, options.MaxBodyBytes);
        context.Response.Body = tee;
        ForwarderError error;
        try
        {
            error = await forwarder.SendAsync(context, target, invoker, ForwarderRequestConfig.Empty, UncompressedTransformer.Instance).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        if (error != ForwarderError.None)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }

        var responseHeaders = Headers(context.Response.Headers);
        var session = sessions.FromRequest(requestHeaders) ?? sessions.FromResponse(responseHeaders);
        var request = new HttpRequestRecord
        {
            Method = context.Request.Method.ToUpperInvariant(),
            Path = string.IsNullOrEmpty(context.Request.Path.Value) ? "/" : context.Request.PathBase.Add(context.Request.Path).Value!,
            Query = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
            Headers = Redact(requestHeaders, redacted),
            Body = BodyCodec.Encode(requestBytes, context.Request.ContentType, requestSize, requestTruncated),
        };
        var response = new HttpResponseRecord
        {
            Status = context.Response.StatusCode,
            Headers = Redact(responseHeaders, redacted),
            Body = BodyCodec.Encode(tee.Captured, context.Response.ContentType, tee.TotalWritten, tee.Truncated),
        };

        await _writer.WriteAsync(
            seq => new HttpExchange
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"ex-{seq:D6}"),
                Seq = seq,
                Session = session ?? string.Create(CultureInfo.InvariantCulture, $"anon-{seq:D6}"),
                StartedAt = startedAt,
                DurationMs = Math.Round(clock.Elapsed.TotalMilliseconds, 3),
                Request = request,
                Response = response,
            },
            CancellationToken.None).ConfigureAwait(false);

        if (options.ExitAfter is { } limit && _writer.Count >= limit)
        {
            // Not awaited: stopping waits for in-flight requests, and this is one of them.
            _ = Task.Run(StopAsync, CancellationToken.None);
        }
    }

    private static async Task<(byte[] Bytes, long Size, bool Truncated)> ReadLimitedAsync(Stream body, long limit, CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        var buffer = new byte[16 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            var room = limit - copy.Length;
            if (room > 0)
            {
                copy.Write(buffer, 0, (int)Math.Min(room, read));
            }

            total += read;
        }

        return (copy.ToArray(), total, total > copy.Length);
    }

    private static SortedDictionary<string, string[]> Headers(IHeaderDictionary headers)
    {
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            if (name.StartsWith(':'))
            {
                continue;
            }

            result[name.ToLowerInvariant()] = values.Select(v => v ?? string.Empty).ToArray();
        }

        return result;
    }

    private static SortedDictionary<string, string[]> Redact(SortedDictionary<string, string[]> headers, HashSet<string> redacted)
    {
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            result[name] = redacted.Contains(name) ? values.Select(_ => "<redacted>").ToArray() : values;
        }

        return result;
    }

    /// <summary>Forwards the request as is, but asks the legacy system for an uncompressed answer.</summary>
    private sealed class UncompressedTransformer : HttpTransformer
    {
        public static readonly UncompressedTransformer Instance = new();

        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest, string destinationPrefix, CancellationToken cancellationToken)
        {
            await Default.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken).ConfigureAwait(false);
            proxyRequest.Headers.Remove("Accept-Encoding");
        }
    }
}
