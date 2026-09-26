using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Runs;
using SecondKey.Replay.Probes;
using SecondKey.Replay.Resets;

namespace SecondKey.Replay;

/// <summary>One side of the comparison: where it answers, how it is reset, what is watched.</summary>
public sealed record SideTarget
{
    public required Uri BaseUrl { get; init; }

    public IStateReset Reset { get; init; } = NoReset.Instance;

    public SqlServerTableProbe? Probe { get; init; }
}

/// <summary>
/// A value the server generates that later requests must carry back — typically an
/// anti-forgery token in a hidden form field. Recorded values are stale on replay, so the
/// fresh value from the latest response on the same side is written in instead.
/// </summary>
public sealed record CorrelationRule(string Name, Regex Pattern, string? FormField, string? Header);

public sealed record ReplayOptions
{
    public required CaptureDocument Capture { get; init; }

    /// <summary>The capture's file, whose digest the run records; null for an in-memory capture.</summary>
    public string? CapturePath { get; init; }

    public required string Output { get; init; }

    public required SideTarget Legacy { get; init; }

    public required SideTarget Candidate { get; init; }

    public ScenarioMode ScenarioMode { get; init; } = ScenarioMode.Session;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public IReadOnlyList<CorrelationRule> Correlation { get; init; } = [];
}

public sealed record ReplaySummary(string RunId, int Scenarios, int Results, int Errors, int FailedResets);

/// <summary>
/// C6: replays every captured scenario against the legacy system and then the candidate,
/// each side first reset to its starting state, each scenario with a fresh cookie jar —
/// identical conditions, so that what differs is the systems, not the replay.
/// </summary>
public static class ReplayEngine
{
    /// <summary>Request headers never replayed: hop-by-hop, those the client sets itself, and the recorded cookies (the jar sends fresh ones).</summary>
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "transfer-encoding", "connection", "keep-alive", "upgrade", "te", "trailer",
        "proxy-connection", "proxy-authorization", "cookie", "accept-encoding", "expect",
    };

    public static async Task<ReplaySummary> RunAsync(ReplayOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var header = new RunHeader
        {
            RunId = string.Create(CultureInfo.InvariantCulture, $"run-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}"),
            StartedAt = DateTimeOffset.UtcNow,
            Tool = ToolInfo.Current,
            Capture = new CaptureReference
            {
                CaptureId = options.Capture.Header.CaptureId,
                Sha256 = options.CapturePath is null ? new string('0', 64) : Sha256Digest.OfFile(options.CapturePath),
                Path = options.CapturePath is null ? null : Path.GetFileName(options.CapturePath),
            },
            Sides = new RunSides
            {
                Legacy = Describe(options.Legacy),
                Candidate = Describe(options.Candidate),
            },
            ScenarioMode = options.ScenarioMode,
        };

        var redacted = new HashSet<string>(options.Capture.Header.Sanitization?.RedactedHeaders ?? [], StringComparer.OrdinalIgnoreCase);
        var scenarios = Plan(options.Capture.Exchanges, options.ScenarioMode);
        int results = 0, errors = 0, failedResets = 0;

        await using (var writer = await RunWriter.CreateAsync(options.Output, header, cancellationToken).ConfigureAwait(false))
        {
            foreach (var (scenario, exchanges) in scenarios)
            {
                foreach (var (side, target) in new[] { (Side.Legacy, options.Legacy), (Side.Candidate, options.Candidate) })
                {
                    var reset = await target.Reset.ResetAsync(cancellationToken).ConfigureAwait(false);
                    await writer.WriteAsync(
                        new StateReset
                        {
                            Scenario = scenario,
                            Side = side,
                            Method = target.Reset.Method,
                            Outcome = reset.Succeeded ? "ok" : "failed",
                            DurationMs = Math.Round(reset.DurationMs, 3),
                            Detail = reset.Detail,
                        },
                        cancellationToken).ConfigureAwait(false);
                    if (!reset.Succeeded)
                    {
                        failedResets++;
                    }

                    var sideResults = await ReplayScenarioAsync(scenario, side, target, exchanges, reset, options, redacted, cancellationToken).ConfigureAwait(false);
                    foreach (var result in sideResults)
                    {
                        await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                        results++;
                        errors += result.Error is null ? 0 : 1;
                    }
                }
            }

            await writer.WriteAsync(new RunEnd { FinishedAt = DateTimeOffset.UtcNow, Scenarios = scenarios.Count, Results = results }, cancellationToken).ConfigureAwait(false);
        }

        return new ReplaySummary(header.RunId, scenarios.Count, results, errors, failedResets);
    }

    /// <summary>Groups exchanges into scenarios, in recorded order: one per session, or one per exchange.</summary>
    public static IReadOnlyList<(string Scenario, IReadOnlyList<HttpExchange> Exchanges)> Plan(IReadOnlyList<HttpExchange> exchanges, ScenarioMode mode)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        var ordered = exchanges.OrderBy(e => e.Seq).ToList();
        return mode == ScenarioMode.Exchange
            ? ordered.Select(e => (e.Id, (IReadOnlyList<HttpExchange>)[e])).ToList()
            : ordered
                .GroupBy(e => e.Session, StringComparer.Ordinal)
                .OrderBy(g => g.First().Seq)
                .Select(g => (g.Key, (IReadOnlyList<HttpExchange>)g.ToList()))
                .ToList();
    }

    private static SideInfo Describe(SideTarget target) => new()
    {
        BaseUrl = target.BaseUrl.ToString(),
        Reset = target.Reset.Method,
        Probe = target.Probe is null ? "none" : "sqlserver-tables",
    };

    private static async Task<List<ExchangeResult>> ReplayScenarioAsync(
        string scenario,
        Side side,
        SideTarget target,
        IReadOnlyList<HttpExchange> exchanges,
        ResetResult reset,
        ReplayOptions options,
        HashSet<string> redacted,
        CancellationToken cancellationToken)
    {
        var results = new List<ExchangeResult>();
        using var handler = new SocketsHttpHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var correlated = new Dictionary<string, string>(StringComparer.Ordinal);
        var probe = target.Probe;
        var before = reset.Succeeded && probe is not null ? await probe.SnapshotAsync(cancellationToken).ConfigureAwait(false) : null;

        foreach (var exchange in exchanges)
        {
            var startedAt = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            var request = Correlate(exchange.Request, options.Correlation, correlated);
            if (!reset.Succeeded)
            {
                results.Add(Result(exchange, scenario, side, startedAt, 0, Redact(request, redacted), null, new ExchangeError { Kind = "reset-failed", Message = reset.Detail ?? "the side could not be reset" }, null));
                continue;
            }

            HttpResponseRecord? response = null;
            ExchangeError? error = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            try
            {
                using var message = ToMessage(target.BaseUrl, request);
                using var answer = await client.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
                var bytes = await answer.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                response = new HttpResponseRecord
                {
                    Status = (int)answer.StatusCode,
                    Headers = Headers(answer),
                    Body = BodyCodec.Encode(bytes, answer.Content.Headers.ContentType?.ToString(), bytes.LongLength, truncated: false),
                };
                Learn(response, options.Correlation, correlated);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                error = new ExchangeError { Kind = "timeout", Message = string.Create(CultureInfo.InvariantCulture, $"no answer within {options.Timeout.TotalSeconds} s") };
            }
            catch (HttpRequestException ex)
            {
                error = new ExchangeError { Kind = ex.HttpRequestError == HttpRequestError.ConnectionError ? "connection" : "protocol", Message = ex.Message };
            }

            DbDelta? delta = null;
            if (probe is not null && before is not null)
            {
                var after = await probe.SnapshotAsync(cancellationToken).ConfigureAwait(false);
                delta = SqlServerTableProbe.Diff(before, after);
                before = after;
            }

            var redactedResponse = response is null ? null : response with { Headers = RedactHeaders(response.Headers, redacted) };
            results.Add(Result(exchange, scenario, side, startedAt, clock.Elapsed.TotalMilliseconds, Redact(request, redacted), redactedResponse, error, delta));
        }

        return results;
    }

    private static ExchangeResult Result(HttpExchange exchange, string scenario, Side side, DateTimeOffset startedAt, double durationMs, HttpRequestRecord request, HttpResponseRecord? response, ExchangeError? error, DbDelta? db) =>
        new()
        {
            Exchange = exchange.Id,
            Scenario = scenario,
            Side = side,
            Seq = exchange.Seq,
            StartedAt = startedAt,
            DurationMs = Math.Round(durationMs, 3),
            Request = request,
            Response = response,
            Error = error,
            Db = db,
        };

    private static HttpRequestMessage ToMessage(Uri baseUrl, HttpRequestRecord request)
    {
        var uri = new Uri(baseUrl.GetLeftPart(UriPartial.Authority) + baseUrl.AbsolutePath.TrimEnd('/') + request.Path + (request.Query ?? string.Empty));
        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        var bytes = BodyCodec.Decode(request.Body);
        if (request.Body is not null)
        {
            message.Content = new ByteArrayContent(bytes);
        }

        foreach (var (name, values) in request.Headers)
        {
            if (SkippedHeaders.Contains(name))
            {
                continue;
            }

            var usable = values.Where(v => v != "<redacted>").ToArray();
            if (usable.Length == 0)
            {
                continue;
            }

            if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, usable);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(name, usable);
            }
        }

        return message;
    }

    private static HttpRequestRecord Correlate(HttpRequestRecord request, IReadOnlyList<CorrelationRule> rules, Dictionary<string, string> values)
    {
        if (rules.Count == 0 || values.Count == 0)
        {
            return request;
        }

        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.Ordinal);
        var body = request.Body;
        foreach (var rule in rules)
        {
            if (!values.TryGetValue(rule.Name, out var value))
            {
                continue;
            }

            var header = rule.Header?.ToLowerInvariant();
            if (header is not null && headers.ContainsKey(header))
            {
                headers[header] = [value];
            }

            var form = FormText(body);
            if (rule.FormField is not null && form is not null)
            {
                body = body! with { Text = ReplaceFormField(form, rule.FormField, value) };
            }
        }

        return request with { Headers = headers, Body = body };
    }

    private static void Learn(HttpResponseRecord response, IReadOnlyList<CorrelationRule> rules, Dictionary<string, string> values)
    {
        var text = response.Body?.Text;
        if (text is null)
        {
            return;
        }

        foreach (var rule in rules)
        {
            var match = rule.Pattern.Match(text);
            if (match.Success && match.Groups.Count > 1)
            {
                values[rule.Name] = match.Groups[1].Value;
            }
        }
    }

    internal static string ReplaceFormField(string form, string field, string value)
    {
        var parts = form.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var equals = parts[i].IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((equals < 0 ? parts[i] : parts[i][..equals]).Replace('+', ' '));
            if (string.Equals(name, field, StringComparison.Ordinal))
            {
                parts[i] = (equals < 0 ? parts[i] : parts[i][..equals]) + "=" + Uri.EscapeDataString(value);
            }
        }

        return string.Join('&', parts);
    }

    /// <summary>The body's text when it is a URL-encoded form, otherwise null.</summary>
    private static string? FormText(BodyRecord? body) =>
        body is not null && body.Encoding == BodyEncoding.Text && BodyCodec.MediaType(body.ContentType) == "application/x-www-form-urlencoded"
            ? body.Text
            : null;

    private static SortedDictionary<string, string[]> Headers(HttpResponseMessage answer)
    {
        var headers = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, values) in answer.Headers.Concat(answer.Content.Headers))
        {
            headers[name.ToLowerInvariant()] = values.ToArray();
        }

        return headers;
    }

    private static HttpRequestRecord Redact(HttpRequestRecord request, HashSet<string> redacted) =>
        request with { Headers = RedactHeaders(request.Headers, redacted) };

    private static SortedDictionary<string, string[]> RedactHeaders(IReadOnlyDictionary<string, string[]> headers, HashSet<string> redacted)
    {
        var result = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (name, values) in headers)
        {
            result[name] = redacted.Contains(name) ? values.Select(_ => "<redacted>").ToArray() : values;
        }

        return result;
    }
}
