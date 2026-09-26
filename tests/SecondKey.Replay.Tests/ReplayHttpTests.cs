using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using SecondKey.Replay.Probes;
using SecondKey.Replay.Resets;

namespace SecondKey.Replay.Tests;

/// <summary>
/// What a replay sends and what it records, against a server that echoes what it received:
/// the recorded request minus what must not be replayed, and the answer exactly as the side
/// gave it — never followed, and masked only where the capture says so.
/// </summary>
public sealed class ReplayHttpTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sk-replay-http-").FullName;
    private EchoServer _server = null!;

    private string RunPath => Path.Combine(_directory, "run.skrun");

    public async Task InitializeAsync() => _server = await EchoServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Recorded_headers_are_replayed_except_those_the_client_sets_or_must_not_send()
    {
        var recorded = new Dictionary<string, string[]>
        {
            ["host"] = ["recorded.example:8080"],
            ["cookie"] = ["sid=recorded"],
            ["accept-encoding"] = ["compress"],
            ["connection"] = ["close"],
            ["keep-alive"] = ["timeout=5"],
            ["upgrade"] = ["websocket"],
            ["te"] = ["trailers"],
            ["trailer"] = ["x-checksum"],
            ["proxy-connection"] = ["keep-alive"],
            ["proxy-authorization"] = ["Basic cHJveHk6cHJveHk="],
            ["expect"] = ["100-continue"],
            ["transfer-encoding"] = ["chunked"],
            ["x-custom"] = ["kept"],
            ["authorization"] = ["<redacted>"],
            ["x-mixed"] = ["<redacted>", "visible"],
        };
        var capture = Capture(["authorization"], Exchange(1, "GET", "/echo", recorded));

        var summary = await ReplayEngine.RunAsync(Options(capture));

        Assert.Equal(0, summary.Errors);
        foreach (var received in BothSides("/echo").Select(r => r.Headers))
        {
            Assert.Equal(_server.Address.Authority, received["host"]);
            Assert.Equal("kept", received["x-custom"]);
            Assert.Equal("visible", received["x-mixed"]);
            Assert.DoesNotContain("compress", received.GetValueOrDefault("accept-encoding") ?? string.Empty, StringComparison.Ordinal);
            foreach (var dropped in new[] { "cookie", "connection", "keep-alive", "upgrade", "te", "trailer", "proxy-connection", "proxy-authorization", "expect", "transfer-encoding", "authorization" })
            {
                Assert.False(received.ContainsKey(dropped), $"'{dropped}' was replayed: {received.GetValueOrDefault(dropped)}");
            }
        }
    }

    [Fact]
    public async Task A_recorded_content_length_is_not_replayed_the_body_decides_it()
    {
        var form = BodyRecord.FromText("a=1", "application/x-www-form-urlencoded");
        var headers = new Dictionary<string, string[]>
        {
            ["content-type"] = ["application/x-www-form-urlencoded"],
            ["content-length"] = ["99"],
        };
        var capture = Capture(null, Exchange(1, "POST", "/echo", headers, form));

        var summary = await ReplayEngine.RunAsync(Options(capture) with { Timeout = TimeSpan.FromSeconds(5) });

        Assert.Equal(0, summary.Errors);
        Assert.All(BothSides("/echo"), received =>
        {
            Assert.Equal("3", received.Headers["content-length"]);
            Assert.Equal("application/x-www-form-urlencoded", received.Headers["content-type"]);
            Assert.Equal("a=1", received.Body);
        });
    }

    [Fact]
    public async Task A_redirect_is_recorded_as_the_side_answered_it_never_followed()
    {
        var capture = Capture(null, Exchange(1, "GET", "/redirect"));

        await ReplayEngine.RunAsync(Options(capture));

        var run = RunFile.Read(RunPath);
        var answer = run.Find("x1", Side.Legacy)!.Response!;
        Assert.Equal(302, answer.Status);
        Assert.Equal(["/landing"], answer.Headers["location"]);
        Assert.DoesNotContain(_server.Requests, r => r.Path == "/landing");
    }

    [Theory]
    [InlineData("app/")]
    [InlineData("app")]
    public async Task A_base_url_with_a_path_prefixes_every_request(string basePath)
    {
        var capture = Capture(null, Exchange(1, "GET", "/echo", query: "?q=1&r=%20"));

        await ReplayEngine.RunAsync(Options(capture, new Uri(_server.Address, basePath)));

        Assert.All(BothSides("/app/echo"), received => Assert.Equal("?q=1&r=%20", received.Query));
    }

    [Fact]
    public async Task The_answer_is_recorded_with_every_header_and_only_the_redacted_ones_masked()
    {
        var request = new Dictionary<string, string[]> { ["authorization"] = ["Bearer recorded"], ["x-custom"] = ["kept"] };
        var capture = Capture(["authorization", "set-cookie"], Exchange(1, "GET", "/headers", request));

        await ReplayEngine.RunAsync(Options(capture));

        var result = RunFile.Read(RunPath).Find("x1", Side.Candidate)!;
        var answer = result.Response!;
        Assert.Equal(["abc"], answer.Headers["x-trace"]);
        Assert.Equal(["text/plain"], answer.Headers["content-type"]);
        Assert.Equal(["<redacted>"], answer.Headers["set-cookie"]);
        Assert.Equal("ok", answer.Body!.Text);
        Assert.Null(answer.Body.Truncated);
        Assert.Equal(["<redacted>"], result.Request.Headers["authorization"]);
        Assert.Equal(["kept"], result.Request.Headers["x-custom"]);
    }

    [Fact]
    public async Task A_side_that_answers_something_other_than_http_is_a_protocol_error()
    {
        using var nonsense = NonsenseServer.Start();
        var capture = Capture(null, Exchange(1, "GET", "/echo"));
        var options = Options(capture) with { Candidate = new SideTarget { BaseUrl = nonsense.Address }, Timeout = TimeSpan.FromSeconds(5) };

        var summary = await ReplayEngine.RunAsync(options);

        Assert.Equal(1, summary.Errors);
        var error = RunFile.Read(RunPath).Find("x1", Side.Candidate)!.Error!;
        Assert.Equal("protocol", error.Kind);
    }

    [Fact]
    public async Task Correlation_writes_a_fresh_header_value_and_leaves_rules_that_learned_nothing_alone()
    {
        var capture = Capture(
            null,
            Exchange(1, "GET", "/token"),
            Exchange(
                2,
                "POST",
                "/echo",
                new Dictionary<string, string[]> { ["x-csrf"] = ["STALE"], ["content-type"] = ["application/x-www-form-urlencoded"] },
                BodyRecord.FromText("t=STALE", "application/x-www-form-urlencoded")));
        CorrelationRule[] rules =
        [
            new("ghost", new Regex("never=\"(\\w+)\""), FormField: "t", Header: null),
            new("csrf", new Regex("token=\"(\\w+)\""), FormField: null, Header: "X-CSRF"),
        ];

        await ReplayEngine.RunAsync(Options(capture) with { Correlation = rules });

        Assert.All(BothSides("/echo"), received =>
        {
            Assert.Equal("FRESH42", received.Headers["x-csrf"]);
            Assert.Equal("t=STALE", received.Body);
        });
    }

    [Fact]
    public async Task Correlation_leaves_alone_what_it_cannot_read_and_patterns_without_a_group()
    {
        var capture = Capture(
            null,
            Exchange(1, "GET", "/token"),
            Exchange(2, "GET", "/binary"),
            Exchange(
                3,
                "POST",
                "/echo",
                new Dictionary<string, string[]> { ["x-bare"] = ["RECORDED"], ["content-type"] = ["application/json"] },
                BodyRecord.FromJson(new System.Text.Json.Nodes.JsonObject { ["t"] = "STALE" }, "application/json")),
            Exchange(
                4,
                "POST",
                "/plain",
                new Dictionary<string, string[]> { ["content-type"] = ["text/plain"] },
                BodyRecord.FromText("t=STALE", "text/plain")));
        CorrelationRule[] rules =
        [
            new("bare", new Regex("token="), FormField: null, Header: "x-bare"),
            new("form", new Regex("token=\"(\\w+)\""), FormField: "t", Header: null),
        ];

        var summary = await ReplayEngine.RunAsync(Options(capture) with { Correlation = rules });

        Assert.Equal(0, summary.Errors);
        Assert.All(BothSides("/echo"), received =>
        {
            Assert.Equal("RECORDED", received.Headers["x-bare"]);
            Assert.Equal("{\"t\":\"STALE\"}", received.Body);
        });
        Assert.All(BothSides("/plain"), received => Assert.Equal("t=STALE", received.Body));
        Assert.Equal(BodyEncoding.Base64, RunFile.Read(RunPath).Find("x2", Side.Legacy)!.Response!.Body!.Encoding);
    }

    [Fact]
    public async Task A_capture_that_redacted_nothing_is_replayed()
    {
        var capture = Capture(null, Exchange(1, "GET", "/echo", new Dictionary<string, string[]> { ["x-custom"] = ["kept"] }));

        var summary = await ReplayEngine.RunAsync(Options(capture));

        Assert.Equal(new ReplaySummary(summary.RunId, 1, 2, 0, 0), summary);
        Assert.Matches("^run-[0-9]{14}-[0-9a-f]{6}$", summary.RunId);
        Assert.Equal(["kept"], RunFile.Read(RunPath).Find("x1", Side.Legacy)!.Request.Headers["x-custom"]);
    }

    [Theory]
    [InlineData("db locked", "db locked")]
    [InlineData(null, "the side could not be reset")]
    public async Task A_failed_reset_is_explained_on_every_result_and_the_probe_is_named(string? detail, string message)
    {
        var capture = Capture(null, Exchange(1, "GET", "/echo"), Exchange(2, "GET", "/echo"));
        var probe = new SqlServerTableProbe("Server=127.0.0.1,1;Database=none;Integrated Security=true", ["dbo.Orders"]);
        var options = Options(capture) with { Candidate = new SideTarget { BaseUrl = _server.Address, Reset = new FailingReset(detail), Probe = probe } };

        var summary = await ReplayEngine.RunAsync(options);

        var run = RunFile.Read(RunPath);
        Assert.Equal(1, summary.FailedResets);
        Assert.Equal("none", run.Header.Sides.Legacy.Probe);
        Assert.Equal("sqlserver-tables", run.Header.Sides.Candidate.Probe);
        Assert.All(run.Results.Where(r => r.Side == Side.Candidate), r =>
        {
            Assert.Equal("reset-failed", r.Error!.Kind);
            Assert.Equal(message, r.Error.Message);
            Assert.Null(r.Response);
        });
        Assert.Equal(2, _server.Requests.Count);
    }

    /// <summary>What the server received at <paramref name="path"/>: once from each side, and nothing else there.</summary>
    private List<Received> BothSides(string path)
    {
        var received = _server.Requests.Where(r => r.Path == path).ToList();
        Assert.Equal(2, received.Count);
        return received;
    }

    private ReplayOptions Options(CaptureDocument capture, Uri? baseUrl = null) => new()
    {
        Capture = capture,
        Output = RunPath,
        Legacy = new SideTarget { BaseUrl = baseUrl ?? _server.Address },
        Candidate = new SideTarget { BaseUrl = baseUrl ?? _server.Address },
    };

    private static CaptureDocument Capture(IReadOnlyList<string>? redacted, params HttpExchange[] exchanges) => new(
        new CaptureHeader
        {
            CaptureId = "cap-replay-http",
            StartedAt = DateTimeOffset.UnixEpoch,
            Tool = ToolInfo.Current,
            Source = new CaptureSource { Kind = "synthetic" },
            Sanitization = redacted is null ? null : new CaptureSanitization { RedactedHeaders = redacted },
        },
        exchanges);

    private static HttpExchange Exchange(int seq, string method, string path, IReadOnlyDictionary<string, string[]>? headers = null, BodyRecord? body = null, string? query = null) => new()
    {
        Id = $"x{seq}",
        Seq = seq,
        Session = "s1",
        StartedAt = DateTimeOffset.UnixEpoch,
        DurationMs = 1,
        Request = new HttpRequestRecord { Method = method, Path = path, Query = query, Headers = headers ?? new Dictionary<string, string[]>(), Body = body },
        Response = new HttpResponseRecord { Status = 200, Headers = new Dictionary<string, string[]>() },
    };

    private sealed class FailingReset(string? detail) : IStateReset
    {
        public ResetMethod Method => ResetMethod.Command;

        public Task<ResetResult> ResetAsync(CancellationToken cancellationToken) => Task.FromResult(new ResetResult(false, 1, detail));
    }

    /// <summary>A request as the server saw it: header names lower-case, repeated values joined.</summary>
    internal sealed record Received(string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body);

    /// <summary>
    /// Echoes every request into <see cref="Requests"/>, and answers three paths specially:
    /// <c>/redirect</c> with a 302, <c>/headers</c> with extra headers, <c>/token</c> with a
    /// server-generated value to correlate.
    /// </summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private EchoServer(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public ConcurrentQueue<Received> Requests { get; } = new();

        public static async Task<EchoServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            EchoServer? server = null;
            app.Run(async context =>
            {
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                var headers = context.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString(), StringComparer.Ordinal);
                server!.Requests.Enqueue(new Received(context.Request.Path.Value!, context.Request.QueryString.Value ?? string.Empty, headers, body));
                switch (context.Request.Path.Value)
                {
                    case "/redirect":
                        context.Response.StatusCode = 302;
                        context.Response.Headers.Location = "/landing";
                        break;
                    case "/headers":
                        context.Response.Headers["x-trace"] = "abc";
                        context.Response.Headers.SetCookie = "sid=secret; path=/";
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("ok", context.RequestAborted);
                        break;
                    case "/binary":
                        context.Response.ContentType = "application/octet-stream";
                        await context.Response.Body.WriteAsync(new byte[] { 0x00, 0xFF, 0x10, 0x80 }, context.RequestAborted);
                        break;
                    case "/token":
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("token=\"FRESH42\"", context.RequestAborted);
                        break;
                    default:
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync("echo", context.RequestAborted);
                        break;
                }
            });
            await app.StartAsync();
            server = new EchoServer(app, new Uri(app.Urls.First()));
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>Accepts connections and answers each with a line that is not HTTP.</summary>
    private sealed class NonsenseServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        private NonsenseServer(TcpListener listener)
        {
            _listener = listener;
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        }

        public Uri Address { get; }

        public static NonsenseServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var server = new NonsenseServer(listener);
            _ = server.AnswerAsync();
            return server;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AnswerAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    _ = await stream.ReadAsync(buffer, _stop.Token);
                    await stream.WriteAsync("THIS IS NOT HTTP\r\n\r\n"u8.ToArray(), _stop.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Stopped.
            }
        }
    }
}
