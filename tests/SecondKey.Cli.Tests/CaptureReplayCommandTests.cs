using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using static SecondKey.Cli.Tests.CliRunner;

namespace SecondKey.Cli.Tests;

public sealed class CaptureReplayCommandTests : IAsyncLifetime
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sk-cli-").FullName;
    private TinyServer _legacy = null!;
    private TinyServer _candidate = null!;

    public async Task InitializeAsync()
    {
        _legacy = await TinyServer.StartAsync("legacy");
        _candidate = await TinyServer.StartAsync("candidate");
    }

    public async Task DisposeAsync()
    {
        await _legacy.DisposeAsync();
        await _candidate.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Capture_records_through_the_proxy_until_it_has_enough()
    {
        var port = TinyServer.FreePort();
        var output = Path.Combine(_directory, "traffic.skcap");
        var run = RunAsync("capture", "--listen", $"http://127.0.0.1:{port}", "--target", _legacy.Address.ToString(), "--out", output, "--exit-after", "2", "--session-key", "sid");

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        await SendWhenListeningAsync(client, "/a");
        await client.GetAsync("/b");
        var (exit, stdout, _) = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("recorded 2 exchange(s)", stdout, StringComparison.Ordinal);
        var capture = CaptureFile.Read(output);
        Assert.Equal(["/a", "/b"], capture.Exchanges.Select(e => e.Request.Path));
        Assert.Equal(["sid"], capture.Header.SessionKeys);
        Assert.Contains("x-api-key", capture.Header.Sanitization!.RedactedHeaders!);
    }

    [Fact]
    public async Task Capture_without_a_target_is_a_usage_error()
    {
        var (exit, _, error) = await RunAsync("capture", "--out", Path.Combine(_directory, "x.skcap"));

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("no target", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_records_both_sides_of_every_exchange()
    {
        var output = Path.Combine(_directory, "run.skrun");

        var (exit, stdout, _) = await RunAsync("replay", "--capture", Sample("sample.skcap"), "--legacy", _legacy.Address.ToString(), "--candidate", _candidate.Address.ToString(), "--out", output);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("2 scenario(s), 10 result(s), 0 without an answer, 0 failed reset(s)", stdout, StringComparison.Ordinal);
        var runDocument = RunFile.Read(output);
        Assert.Equal("candidate", runDocument.Find("ex-000001", Side.Candidate)!.Response!.Body!.Json!["variant"]!.GetValue<string>());
        Assert.Equal("legacy", runDocument.Find("ex-000001", Side.Legacy)!.Response!.Body!.Json!["variant"]!.GetValue<string>());
    }

    [Fact]
    public async Task Replay_reads_its_settings_from_the_configuration_file()
    {
        var config = Path.Combine(_directory, "secondkey.yaml");
        await File.WriteAllTextAsync(config, $$"""
            version: 1
            replay:
              capture: '{{Sample("sample.skcap")}}'
              out: out/run.skrun
              scenarioMode: exchange
              legacy: { baseUrl: "{{_legacy.Address}}" }
              candidate: { baseUrl: "{{_candidate.Address}}", reset: { command: { run: "exit 0" } } }
            """);

        var (exit, stdout, _) = await RunAsync("replay", "--config", config);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("5 scenario(s)", stdout, StringComparison.Ordinal);
        var runDocument = RunFile.Read(Path.Combine(_directory, "out", "run.skrun"));
        Assert.Equal(ScenarioMode.Exchange, runDocument.Header.ScenarioMode);
        Assert.Equal(ResetMethod.Command, runDocument.Header.Sides.Candidate.Reset);
    }

    [Fact]
    public async Task A_reset_that_fails_makes_the_replay_a_runtime_error()
    {
        var (exit, _, error) = await RunAsync("replay", "--capture", Sample("sample.skcap"), "--legacy", _legacy.Address.ToString(), "--candidate", _candidate.Address.ToString(), "--out", Path.Combine(_directory, "r.skrun"), "--candidate-reset-http", "/does-not-matter");

        // The tiny server answers every path with 200, so this reset succeeds; a refused port does not.
        Assert.Equal(ExitCodes.Success, exit);
        var (failedExit, _, failedError) = await RunAsync("replay", "--capture", Sample("sample.skcap"), "--legacy", _legacy.Address.ToString(), "--candidate", "http://127.0.0.1:9", "--out", Path.Combine(_directory, "f.skrun"), "--candidate-reset-http", "/reset");
        Assert.Equal(ExitCodes.RuntimeError, failedExit);
        Assert.Contains("could not be reset", failedError, StringComparison.Ordinal);
        Assert.Empty(error);
    }

    [Fact]
    public async Task A_secret_named_in_the_configuration_must_exist_in_the_environment()
    {
        var config = Path.Combine(_directory, "secondkey.yaml");
        await File.WriteAllTextAsync(config, $$"""
            version: 1
            replay:
              capture: '{{Sample("sample.skcap")}}'
              legacy: { baseUrl: "{{_legacy.Address}}", reset: { sqlServerSnapshot: { connectionStringEnv: SK_TEST_NO_SUCH_VARIABLE, database: Shop, snapshot: Shop_sk } } }
              candidate: { baseUrl: "{{_candidate.Address}}" }
            """);

        var (exit, _, error) = await RunAsync("replay", "--config", config);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains("SK_TEST_NO_SUCH_VARIABLE is not set", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_without_its_inputs_is_a_usage_error()
    {
        var (exit, _, error) = await RunAsync("replay", "--legacy", "http://127.0.0.1:1");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("needs a capture and both base URLs", error, StringComparison.Ordinal);
    }

    private static async Task SendWhenListeningAsync(HttpClient client, string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await client.GetAsync(path);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("the capture proxy never started listening");
    }
}
