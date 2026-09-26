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
        Assert.Equal("/work", config.BaseDirectory);
        Assert.Equal("/work/.secondkey/run.skrun", config.Resolve(config.Compare!.Run!));
    }

    [Theory]
    [InlineData("version: 2\n", "version 2 is not supported")]
    [InlineData("version: 1\nreplay:\n  legacy:\n    baseUrl: x\n    resett: {}\n", "resett")]
    [InlineData("version: 1\nreplay: [\n", "not valid YAML")]
    [InlineData("version: 1\nreplay:\n  legacy:\n    reset: { http: { method: GET } }\n", "path")]
    public void A_wrong_configuration_says_what_is_wrong(string yaml, string expected)
    {
        var ex = Assert.Throws<ConfigurationException>(() => SecondKeyConfig.Parse(yaml, "secondkey.yaml"));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
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
