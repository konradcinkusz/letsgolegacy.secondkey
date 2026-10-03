using System.Text.RegularExpressions;
using SecondKey.Cli.Configuration;

namespace SecondKey.Cli.Tests;

public class ConfigurationTests
{
    private const string Full = """
        version: 1
        capture:
          listen: http://127.0.0.1:8080
          target: http://127.0.0.1:5080
          out: .secondkey/traffic.skcap
          sessionKeys: [sk_session]
          redactHeaders: [authorization]
          maxBodyBytes: 1048576
        replay:
          capture: .secondkey/traffic.skcap
          out: .secondkey/run.skrun
          scenarioMode: session
          timeoutSeconds: 30
          legacy:
            baseUrl: http://127.0.0.1:5080
            reset: { http: { path: /__sk/reset } }
            probe: { sqlServer: { connectionStringEnv: SK_LEGACY_DB, tables: [dbo.Orders] } }
          candidate:
            baseUrl: http://127.0.0.1:5081
            reset: { sqlServerSnapshot: { connectionStringEnv: SK_CANDIDATE_DB, database: Shop, snapshot: Shop_sk } }
          correlation:
            - { name: csrf, regex: 'value="([^"]+)"', formField: __RequestVerificationToken }
        compare: { contract: contract.yaml, run: .secondkey/run.skrun, out: .secondkey/verdict.json }
        evidence: { verdict: .secondkey/verdict.json, contract: contract.yaml, sarif: [gate.sarif], out: evidence, pdf: auto }
        gate: { sarif: [gate.sarif] }
        """;

    [Fact]
    public void A_full_configuration_reads_every_section()
    {
        var config = SecondKeyConfig.Parse(Full, "/work/secondkey.yaml");

        Assert.Equal("http://127.0.0.1:5080", config.Capture!.Target);
        Assert.Equal("/__sk/reset", config.Replay!.Legacy!.Reset!.Http!.Path);
        Assert.Equal("POST", config.Replay.Legacy.Reset.Http.Method);
        Assert.Equal("SK_CANDIDATE_DB", config.Replay.Candidate!.Reset!.SqlServerSnapshot!.ConnectionStringEnv);
        Assert.Equal(["dbo.Orders"], config.Replay.Legacy.Probe!.SqlServer!.Tables);
        Assert.Equal("__RequestVerificationToken", config.Replay.Correlation![0].FormField);
        Assert.Equal(["gate.sarif"], config.Gate!.Sarif);
        // "/work" is rooted on every platform; on Windows it is rooted on the current drive.
        Assert.Equal(Path.GetFullPath("/work"), config.BaseDirectory);
        Assert.Equal(Path.GetFullPath("/work/.secondkey/run.skrun"), config.Resolve(config.Compare!.Run!));
    }

    [Theory]
    [InlineData("version: 2\n", "version 2 is not supported")]
    [InlineData("version: 1\nreplay:\n  legacy:\n    baseUrl: x\n    resett: {}\n", "resett")]
    [InlineData("version: 1\nreplay: [\n", "not valid YAML")]
    [InlineData("version: 1\nreplay:\n  legacy:\n    reset: { http: { method: GET } }\n", "path")]
    [InlineData("version: 1\ncapture:\n  target: localhost:5080\n", "capture.target: 'localhost:5080' is not an absolute http(s) URL")]
    [InlineData("version: 1\nreplay:\n  candidate:\n    baseUrl: /shop\n", "replay.candidate.baseUrl: '/shop' is not an absolute http(s) URL")]
    [InlineData("version: 1\nreplay:\n  scenarioMode: exchanges\n", "replay.scenarioMode: 'exchanges' is not one of session, exchange")]
    [InlineData("version: 1\nevidence:\n  pdf: sometimes\n", "evidence.pdf: 'sometimes' is not one of auto, required, off")]
    [InlineData("version: 1\nreplay:\n  legacy:\n    reset: { http: { path: /r }, command: { run: x } }\n", "replay.legacy.reset: give exactly one of http, sqlServerSnapshot, command (found 2)")]
    [InlineData("version: 1\nreplay:\n  candidate:\n    reset: {}\n", "replay.candidate.reset: give exactly one of http, sqlServerSnapshot, command (found 0)")]
    [InlineData("version: 1\nreplay:\n  correlation:\n    - { name: csrf, regex: '(', formField: f }\n", "replay.correlation[0].regex ('csrf'): not a valid regular expression")]
    [InlineData("version: 1\nreplay:\n  correlation:\n    - { name: csrf, regex: 'token=[a-z]+', formField: f }\n", "replay.correlation[0].regex ('csrf'): has no capture group")]
    [InlineData("version: 1\nreplay:\n  correlation:\n    - { name: ok, regex: 'a=(b)', header: h }\n    - { name: plain, regex: '(?:b)', header: h }\n", "replay.correlation[1].regex ('plain'): has no capture group")]
    public void A_wrong_configuration_says_what_is_wrong(string yaml, string expected)
    {
        var ex = Assert.Throws<ConfigurationException>(() => SecondKeyConfig.Parse(yaml, "secondkey.yaml"));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_correlation_pattern_with_a_group_is_accepted_and_runs_as_the_engine_runs_it()
    {
        var config = SecondKeyConfig.Parse(
            "version: 1\nreplay:\n  correlation:\n    - { name: a, regex: 'x=([0-9]+)', header: h }\n    - { name: b, regex: 'y=(?<v>[0-9]+)', formField: f }\n",
            "secondkey.yaml");

        Assert.All(config.Replay!.Correlation!, rule =>
        {
            var pattern = rule.Compile();
            Assert.Equal(CorrelationConfig.MatchTimeout, pattern.MatchTimeout);
            Assert.Equal(RegexOptions.CultureInvariant, pattern.Options);
        });
    }

    [Fact]
    public void An_empty_file_is_an_empty_configuration()
    {
        Assert.Null(SecondKeyConfig.Parse("", "/w/secondkey.yaml").Replay);
    }

    [Fact]
    public void Without_a_file_the_configuration_is_empty_and_an_explicit_missing_file_is_an_error()
    {
        Assert.Null(SecondKeyConfig.Load(null).Capture);
        Assert.Throws<ConfigurationException>(() => SecondKeyConfig.Load(Path.Combine(Path.GetTempPath(), "nothing-here.yaml")));
    }
}
