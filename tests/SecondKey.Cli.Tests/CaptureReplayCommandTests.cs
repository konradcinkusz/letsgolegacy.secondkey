using System.Net;
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

    /// <summary>
    /// A pipeline or a terminal stops a capture with a signal. The exchange in flight at that
    /// moment takes longer than the two seconds System.CommandLine allows by default, and is
    /// still recorded, and the command still ends with its summary and exit code 0.
    /// </summary>
    [UnixFact]
    public async Task A_stop_signal_lets_the_exchange_in_flight_finish_and_keeps_it()
    {
        await using var slow = await TinyServer.StartAsync("slow", TimeSpan.FromSeconds(4));
        var port = TinyServer.FreePort();
        var output = Path.Combine(_directory, "stopped.skcap");
        using var sk = Start("capture", "--listen", $"http://127.0.0.1:{port}", "--target", slow.Address.ToString(), "--out", output);
        await sk.LineAsync("sk capture: recording").WaitAsync(TimeSpan.FromSeconds(60));

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var answer = client.GetAsync("/slow");
        await slow.Received.WaitAsync(TimeSpan.FromSeconds(30));
        sk.Signal("TERM");
        await sk.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(sk.ExitCode == ExitCodes.Success, $"exit {sk.ExitCode}\n{sk.Output}\n{sk.Error}");
        Assert.Contains("recorded 1 exchange(s), skipped 0", sk.Output, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await answer).StatusCode);
        Assert.Equal(["/slow"], CaptureFile.Read(output).Exchanges.Select(e => e.Request.Path));
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
    public async Task A_bad_url_is_a_usage_error_on_the_command_line_and_invalid_configuration_in_the_file()
    {
        var (option, _, optionError) = await RunAsync("replay", "--capture", Sample("sample.skcap"), "--legacy", "not-a-url", "--candidate", _candidate.Address.ToString());

        Assert.Equal(ExitCodes.Usage, option);
        Assert.Contains("--legacy: 'not-a-url' is not an absolute http(s) URL", optionError, StringComparison.Ordinal);

        var (file, _, fileError) = await ReplayWithAsync($$"""
            legacy: { baseUrl: "ftp://legacy.test/" }
            candidate: { baseUrl: "{{_candidate.Address}}" }
            """);

        Assert.Equal(ExitCodes.InvalidInput, file);
        Assert.Contains("replay.legacy.baseUrl: 'ftp://legacy.test/' is not an absolute http(s) URL", fileError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scenario_mode_outside_the_two_is_invalid_configuration()
    {
        var (exit, _, error) = await ReplayWithAsync($$"""
            scenarioMode: exchanges
            legacy: { baseUrl: "{{_legacy.Address}}" }
            candidate: { baseUrl: "{{_candidate.Address}}" }
            """);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains("replay.scenarioMode: 'exchanges' is not one of session, exchange", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ http: { path: /__sk/reset }, command: { run: \"exit 0\" } }", 2)]
    [InlineData("{ }", 0)]
    public async Task A_reset_names_exactly_one_kind(string reset, int kinds)
    {
        var (exit, _, error) = await ReplayWithAsync($$"""
            legacy: { baseUrl: "{{_legacy.Address}}" }
            candidate: { baseUrl: "{{_candidate.Address}}", reset: {{reset}} }
            """);

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains($"replay.candidate.reset: give exactly one of http, sqlServerSnapshot, command (found {kinds})", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_command_resets_working_directory_resolves_against_the_files_directory()
    {
        // The command succeeds only in the directory beside the file, never in the test's own.
        Directory.CreateDirectory(Path.Combine(_directory, "state"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "state", "marker"), "here");
        var run = OperatingSystem.IsWindows() ? "if exist marker (exit 0) else (exit 1)" : "test -f marker";

        var (exit, stdout, error) = await ReplayWithAsync($$"""
            legacy: { baseUrl: "{{_legacy.Address}}" }
            candidate: { baseUrl: "{{_candidate.Address}}", reset: { command: { run: "{{run}}", workingDirectory: state } } }
            """);

        Assert.True(exit == ExitCodes.Success, $"exit {exit}\n{stdout}\n{error}");
        Assert.Contains("0 failed reset(s)", stdout, StringComparison.Ordinal);
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

    /// <summary>Runs <c>sk replay</c> with a secondkey.yaml whose replay section adds these lines.</summary>
    private async Task<(int Exit, string Output, string Error)> ReplayWithAsync(string replayLines)
    {
        var config = Path.Combine(_directory, "secondkey.yaml");
        var indented = string.Join("\n", replayLines.Split('\n').Select(line => "  " + line));
        await File.WriteAllTextAsync(config, $"version: 1\nreplay:\n  capture: '{Sample("sample.skcap")}'\n  out: out/run.skrun\n{indented}\n");
        return await RunAsync("replay", "--config", config);
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
