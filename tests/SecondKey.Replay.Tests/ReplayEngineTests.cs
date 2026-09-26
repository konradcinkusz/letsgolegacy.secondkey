using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using SecondKey.Capture;
using SecondKey.Capture.Tests;
using SecondKey.Replay.Resets;

namespace SecondKey.Replay.Tests;

public sealed class ReplayEngineTests : IAsyncLifetime
{
    private readonly SampleShopHost _legacy = new("legacy");
    private readonly SampleShopHost _candidate = new("candidate");
    private readonly string _directory = Directory.CreateTempSubdirectory("sk-replay-").FullName;
    private CaptureDocument _capture = null!;

    private string CapturePath => Path.Combine(_directory, "traffic.skcap");

    private string RunPath => Path.Combine(_directory, "run.skrun");

    public async Task InitializeAsync()
    {
        await using (var proxy = await RecordingProxy.StartAsync(new CaptureOptions
        {
            Listen = new Uri("http://127.0.0.1:0"),
            Target = _legacy.Address,
            Output = CapturePath,
            SessionKeys = ["sk_session"],
        }))
        {
            using var shopper = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = proxy.Address };
            await shopper.GetAsync("/products?sort=name");
            await shopper.PostAsJsonAsync("/cart/items", new { sku = "ECL-02", quantity = 3 });
            await shopper.GetAsync("/cart");
            await shopper.PostAsJsonAsync("/checkout", new { email = "buyer@example.com" });

            using var browser = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = proxy.Address };
            await browser.PostAsJsonAsync("/checkout", new { email = "empty@example.com" });
        }

        _capture = CaptureFile.Read(CapturePath);
    }

    public async Task DisposeAsync()
    {
        await _legacy.DisposeAsync();
        await _candidate.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private ReplayOptions Options(ScenarioMode mode = ScenarioMode.Session) => new()
    {
        Capture = _capture,
        CapturePath = CapturePath,
        Output = RunPath,
        ScenarioMode = mode,
        Legacy = new SideTarget { BaseUrl = _legacy.Address, Reset = new HttpReset(new HttpClient(), _legacy.Address, "POST", "/__sk/reset") },
        Candidate = new SideTarget { BaseUrl = _candidate.Address, Reset = new HttpReset(new HttpClient(), _candidate.Address, "POST", "/__sk/reset") },
    };

    [Fact]
    public async Task Both_sides_are_recorded_for_every_captured_exchange()
    {
        var summary = await ReplayEngine.RunAsync(Options());

        var report = RunFile.Validate(RunPath);
        Assert.True(report.IsValid, report.ToString());
        var run = RunFile.Read(RunPath);
        Assert.Equal(5, _capture.Exchanges.Count);
        Assert.Equal(10, run.Results.Count);
        Assert.All(_capture.Exchanges, e =>
        {
            Assert.NotNull(run.Find(e.Id, Side.Legacy)?.Response);
            Assert.NotNull(run.Find(e.Id, Side.Candidate)?.Response);
        });
        // Three scenarios: the anonymous catalogue visit, the shopper's session, and the empty checkout.
        Assert.Equal(new ReplaySummary(summary.RunId, 3, 10, 0, 0), summary);
        Assert.Equal(run.Header.RunId, summary.RunId);
    }

    [Fact]
    public async Task Each_side_answers_as_itself()
    {
        await ReplayEngine.RunAsync(Options());
        var run = RunFile.Read(RunPath);
        var emptyCheckout = _capture.Exchanges[4].Id;

        Assert.Equal(500, run.Find(emptyCheckout, Side.Legacy)!.Response!.Status);
        Assert.Equal(400, run.Find(emptyCheckout, Side.Candidate)!.Response!.Status);
    }

    [Fact]
    public async Task A_session_keeps_its_cookies_so_the_cart_survives_between_requests()
    {
        await ReplayEngine.RunAsync(Options());
        var run = RunFile.Read(RunPath);
        var cartPage = _capture.Exchanges[2].Id;

        foreach (var side in new[] { Side.Legacy, Side.Candidate })
        {
            var html = run.Find(cartPage, side)!.Response!.Body!.Text!;
            Assert.Contains("<span class=\"order-total\">59.97</span>", html, StringComparison.Ordinal);
        }

        Assert.Equal(201, run.Find(_capture.Exchanges[3].Id, Side.Candidate)!.Response!.Status);
    }

    [Fact]
    public async Task Every_scenario_starts_with_a_reset_on_each_side_in_order()
    {
        await ReplayEngine.RunAsync(Options());
        var run = RunFile.Read(RunPath);

        Assert.Equal(6, run.Resets.Count);
        Assert.All(run.Resets, r => Assert.Equal("ok", r.Outcome));
        Assert.Equal([Side.Legacy, Side.Candidate, Side.Legacy, Side.Candidate, Side.Legacy, Side.Candidate], run.Resets.Select(r => r.Side));
        Assert.Equal(ResetMethod.Http, run.Header.Sides.Legacy.Reset);
        Assert.Equal(ScenarioMode.Session, run.Header.ScenarioMode);
        Assert.Equal(_capture.Header.CaptureId, run.Header.Capture.CaptureId);
        Assert.Equal(SecondKey.Artifacts.Hashing.Sha256Digest.OfFile(CapturePath), run.Header.Capture.Sha256);
        Assert.Equal("traffic.skcap", run.Header.Capture.Path);
    }

    [Fact]
    public async Task Redacted_headers_stay_redacted_in_the_run()
    {
        await ReplayEngine.RunAsync(Options());
        var run = RunFile.Read(RunPath);

        var sessionStart = run.Find(_capture.Exchanges[1].Id, Side.Candidate)!;
        Assert.Equal(["<redacted>"], sessionStart.Response!.Headers["set-cookie"]);
    }

    [Fact]
    public async Task A_side_that_does_not_answer_is_an_error_on_that_side_only()
    {
        var options = Options() with { Candidate = new SideTarget { BaseUrl = new Uri("http://127.0.0.1:9") } };

        var summary = await ReplayEngine.RunAsync(options);

        var run = RunFile.Read(RunPath);
        Assert.Equal(5, summary.Errors);
        Assert.All(run.Results.Where(r => r.Side == Side.Candidate), r => Assert.Equal("connection", r.Error!.Kind));
        Assert.All(run.Results.Where(r => r.Side == Side.Legacy), r => Assert.Null(r.Error));
    }

    [Fact]
    public async Task A_failed_reset_means_the_scenario_is_not_replayed_on_that_side()
    {
        var options = Options() with { Candidate = new SideTarget { BaseUrl = _candidate.Address, Reset = new HttpReset(new HttpClient(), _candidate.Address, "POST", "/no-such-reset") } };

        var summary = await ReplayEngine.RunAsync(options);

        var run = RunFile.Read(RunPath);
        Assert.Equal(3, summary.FailedResets);
        Assert.All(run.Results.Where(r => r.Side == Side.Candidate), r => Assert.Equal("reset-failed", r.Error!.Kind));
        Assert.Contains(run.Resets, r => r.Outcome == "failed" && r.Detail!.Contains("answered 404", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_timeout_is_recorded_as_a_timeout()
    {
        var silent = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        silent.Start();
        try
        {
            var address = new Uri($"http://127.0.0.1:{((IPEndPoint)silent.LocalEndpoint).Port}");
            var options = Options() with { Candidate = new SideTarget { BaseUrl = address }, Timeout = TimeSpan.FromMilliseconds(300) };

            await ReplayEngine.RunAsync(options);

            var run = RunFile.Read(RunPath);
            Assert.Equal("timeout", run.Results.First(r => r.Side == Side.Candidate).Error!.Kind);
        }
        finally
        {
            silent.Stop();
        }
    }

    [Fact]
    public void Scenarios_follow_sessions_in_recorded_order_or_single_exchanges()
    {
        var bySession = ReplayEngine.Plan(_capture.Exchanges, ScenarioMode.Session);
        var byExchange = ReplayEngine.Plan(_capture.Exchanges.Reverse().ToList(), ScenarioMode.Exchange);

        Assert.Equal(3, bySession.Count);
        Assert.StartsWith("anon-", bySession[0].Scenario, StringComparison.Ordinal);
        Assert.Equal(3, bySession[1].Exchanges.Count);
        Assert.Equal(_capture.Exchanges[1].Session, bySession[1].Scenario);
        Assert.Equal(_capture.Exchanges.Select(e => e.Id), byExchange.Select(s => s.Scenario));
    }

    [Fact]
    public async Task Correlation_writes_the_fresh_server_value_into_later_form_posts()
    {
        var capture = _capture with
        {
            Exchanges =
            [
                _capture.Exchanges[2],
                _capture.Exchanges[3] with
                {
                    Request = _capture.Exchanges[3].Request with
                    {
                        Path = "/health",
                        Method = "GET",
                        Headers = new Dictionary<string, string[]> { ["content-type"] = ["application/x-www-form-urlencoded"] },
                        Body = BodyRecord.FromText("token=STALE&x=1", "application/x-www-form-urlencoded"),
                    },
                },
            ],
        };
        var rule = new CorrelationRule("token", new Regex("name=\"token\" value=\"([0-9A-F]+)\""), FormField: "token", Header: null);
        var options = Options() with { Capture = capture, CapturePath = null, Correlation = [rule] };

        await ReplayEngine.RunAsync(options);

        var run = RunFile.Read(RunPath);
        var legacyCart = run.Find(capture.Exchanges[0].Id, Side.Legacy)!.Response!.Body!.Text!;
        var fresh = rule.Pattern.Match(legacyCart).Groups[1].Value;
        var sent = run.Find(capture.Exchanges[1].Id, Side.Legacy)!.Request.Body!.Text!;
        Assert.Equal($"token={fresh}&x=1", sent);
        Assert.Equal(new string('0', 64), run.Header.Capture.Sha256);
    }

    [Theory]
    [InlineData("a=1&token=old&b=2", "token", "n e w", "a=1&token=n%20e%20w&b=2")]
    [InlineData("token", "token", "v", "token=v")]
    [InlineData("a=1", "token", "v", "a=1")]
    [InlineData("my+field=1", "my field", "v", "my+field=v")]
    [InlineData("=old&token=a", "", "v", "=v&token=a")]
    public void A_form_field_is_replaced_in_place(string form, string field, string value, string expected)
    {
        Assert.Equal(expected, ReplayEngine.ReplaceFormField(form, field, value));
    }

    [Fact]
    public async Task Replaying_without_options_is_refused()
    {
        var options = await Assert.ThrowsAsync<ArgumentNullException>(() => ReplayEngine.RunAsync(null!));
        var exchanges = Assert.Throws<ArgumentNullException>(() => ReplayEngine.Plan(null!, ScenarioMode.Session));
        Assert.Equal("options", options.ParamName);
        Assert.Equal("exchanges", exchanges.ParamName);
    }
}
