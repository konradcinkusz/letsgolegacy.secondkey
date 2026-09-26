using System.Text.Json.Nodes;
using SecondKey.Artifacts.Verdicts;
using static SecondKey.Cli.Tests.CliRunner;

namespace SecondKey.Cli.Tests;

public class GateCommandTests
{
    private static string SampleSarif => Path.Combine(Root, "samples", "gate", "portcullis.sarif");

    [Fact]
    public async Task The_sample_passes_and_each_rule_that_found_something_is_listed()
    {
        var (exit, output, error) = await RunAsync("gate", "--sarif", SampleSarif);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(string.Empty, error);
        Assert.StartsWith("sk gate: pass — 0 blocking of 2 finding(s) in 1 log(s)", output, StringComparison.Ordinal);
        Assert.Contains("PORTCULLIS_MIG_SYNC_OVER_ASYNC", output, StringComparison.Ordinal);
        Assert.Contains("suppressed   1", output, StringComparison.Ordinal);
        Assert.DoesNotContain("PORTCULLIS_MIG_CONFIGURATION_MANAGER", output, StringComparison.Ordinal);
        Assert.DoesNotContain("blocks:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocking_finding_fails_the_gate_and_is_printed()
    {
        var directory = Directory.CreateTempSubdirectory("sk-gate-").FullName;
        try
        {
            var blocking = Path.Combine(directory, "blocking.sarif");
            await File.WriteAllTextAsync(blocking, """
                { "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "Portcullis" } }, "results": [
                  { "ruleId": "PORTCULLIS_MIG_HTTPCONTEXT_CURRENT", "level": "error", "message": { "text": "HttpContext.Current in a controller" },
                    "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "src/Cart.cs" }, "region": { "startLine": 12 } } } ] },
                  { "ruleId": "NO_LOCATION", "level": "error", "message": { "text": "repository-wide" } } ] } ] }
                """);

            var (exit, output, _) = await RunAsync("gate", "--sarif", SampleSarif, blocking);

            Assert.Equal(ExitCodes.Failed, exit);
            Assert.StartsWith("sk gate: fail — 2 blocking of 4 finding(s) in 2 log(s)", output, StringComparison.Ordinal);
            Assert.Contains("  blocks: src/Cart.cs:12  PORTCULLIS_MIG_HTTPCONTEXT_CURRENT  HttpContext.Current in a controller", output, StringComparison.Ordinal);
            Assert.Contains("  blocks: (no location)  NO_LOCATION  repository-wide", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Logs_can_come_from_the_configuration()
    {
        var directory = Directory.CreateTempSubdirectory("sk-gate-").FullName;
        try
        {
            File.Copy(SampleSarif, Path.Combine(directory, "gate.sarif"));
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\ngate: { sarif: [gate.sarif] }\n");

            var (exit, output, _) = await RunAsync("gate", "--config", config);

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("in 1 log(s)", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_log_it_is_a_usage_error_and_a_broken_log_is_invalid_input()
    {
        var directory = Directory.CreateTempSubdirectory("sk-gate-").FullName;
        try
        {
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\n");
            var broken = Path.Combine(directory, "broken.sarif");
            await File.WriteAllTextAsync(broken, """{ "version": "2.0.0" }""");

            var (none, _, noneError) = await RunAsync("gate", "--config", config);
            var (invalid, _, invalidError) = await RunAsync("gate", "--sarif", broken);

            Assert.Equal(ExitCodes.Usage, none);
            Assert.Contains("needs at least one SARIF log", noneError, StringComparison.Ordinal);
            Assert.Equal(ExitCodes.InvalidInput, invalid);
            Assert.Contains("not a SARIF 2.1.0 log", invalidError, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class EvidenceCommandTests
{
    private static string SampleSarif => Path.Combine(Root, "samples", "gate", "portcullis.sarif");

    /// <summary>A verdict computed from the sample contract and run, so its digests are real.</summary>
    private static async Task<string> VerdictAsync(string directory)
    {
        var path = Path.Combine(directory, "verdict.json");
        var (exit, _, error) = await RunAsync("compare", "--contract", Sample("contract.yaml"), "--run", Sample("sample.skrun"), "--out", path);
        Assert.True(exit == ExitCodes.Failed, error);
        return path;
    }

    [Fact]
    public async Task The_pack_is_written_and_the_command_succeeds_whatever_the_verdict()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            var verdict = await VerdictAsync(directory);
            var pack = Path.Combine(directory, "pack");

            var (exit, output, error) = await RunAsync("evidence", "--verdict", verdict, "--contract", Sample("contract.yaml"), "--run", Sample("sample.skrun"), "--sarif", SampleSarif, "--out", pack, "--pdf", "off");

            Assert.True(exit == ExitCodes.Success, error);
            Assert.Contains($"sk evidence: pack written to {pack}", output, StringComparison.Ordinal);
            Assert.Contains("contract.yaml, index.html, run.skrun, sarif/portcullis.sarif, statement.intoto.json, verdict.json, manifest.json", output, StringComparison.Ordinal);
            Assert.Contains("behaviour: fail · gate: pass · PDF: not written — switched off (--pdf off)", output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pack, "index.html")));
            Assert.Equal(VerdictOutcome.Fail, VerdictFile.Read(Path.Combine(pack, "verdict.json")).Outcome);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Everything_can_come_from_the_configuration()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".secondkey"));
            File.Copy(Sample("contract.yaml"), Path.Combine(directory, "contract.yaml"));
            File.Copy(SampleSarif, Path.Combine(directory, "gate.sarif"));
            File.Move(await VerdictAsync(directory), Path.Combine(directory, ".secondkey", "verdict.json"));
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\ncompare: { contract: contract.yaml }\nevidence: { sarif: [gate.sarif], pdf: \"off\" }\n");

            var (exit, output, error) = await RunAsync("evidence", "--config", config);

            Assert.True(exit == ExitCodes.Success, error);
            Assert.Contains("gate: pass", output, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory, ".secondkey", "evidence", "manifest.json")));
            Assert.False(File.Exists(Path.Combine(directory, ".secondkey", "evidence", "run.skrun")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_contract_it_is_a_usage_error()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\n");

            var (exit, _, error) = await RunAsync("evidence", "--config", config);

            Assert.Equal(ExitCodes.Usage, exit);
            Assert.Contains("needs the contract", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_contract_the_verdict_does_not_name_is_invalid_input()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            var verdict = await VerdictAsync(directory);
            var edited = Path.Combine(directory, "contract.yaml");
            await File.WriteAllTextAsync(edited, File.ReadAllText(Sample("contract.yaml")) + "# changed\n");

            var (exit, _, error) = await RunAsync("evidence", "--verdict", verdict, "--contract", edited, "--out", Path.Combine(directory, "pack"), "--pdf", "off");

            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains("not the contract the verdict was computed from", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_directory_holding_other_files_is_a_usage_error()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            var verdict = await VerdictAsync(directory);
            var pack = Path.Combine(directory, "pack");
            Directory.CreateDirectory(pack);
            await File.WriteAllTextAsync(Path.Combine(pack, "notes.txt"), "mine");

            var (exit, _, error) = await RunAsync("evidence", "--verdict", verdict, "--contract", Sample("contract.yaml"), "--out", pack, "--pdf", "off");

            Assert.Equal(ExitCodes.Usage, exit);
            Assert.Contains("sk evidence: ", error, StringComparison.Ordinal);
            Assert.Contains("holds no evidence pack", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_pdf_mode_outside_the_three_is_refused()
    {
        var (option, _, _) = await RunAsync("evidence", "--contract", Sample("contract.yaml"), "--pdf", "maybe");

        Assert.Equal(ExitCodes.Usage, option);

        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        try
        {
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\nevidence: { contract: c.yaml, pdf: sometimes }\n");

            var (exit, _, error) = await RunAsync("evidence", "--config", config);

            Assert.Equal(ExitCodes.Usage, exit);
            Assert.Contains("evidence.pdf: 'sometimes' is not one of auto, required, off", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_required_pdf_that_cannot_be_made_is_a_runtime_error()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        var previous = Environment.GetEnvironmentVariable("SK_CHROME_PATH");
        try
        {
            var verdict = await VerdictAsync(directory);
            Environment.SetEnvironmentVariable("SK_CHROME_PATH", Path.Combine(directory, "no-browser-here"));

            var (exit, _, error) = await RunAsync("evidence", "--verdict", verdict, "--contract", Sample("contract.yaml"), "--out", Path.Combine(directory, "pack"), "--pdf", "required");

            Assert.Equal(ExitCodes.RuntimeError, exit);
            Assert.Contains("SK_CHROME_PATH is set to", error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SK_CHROME_PATH", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task In_auto_mode_a_missing_browser_is_reported_and_the_pack_is_still_written()
    {
        var directory = Directory.CreateTempSubdirectory("sk-evidence-").FullName;
        var previous = Environment.GetEnvironmentVariable("SK_CHROME_PATH");
        try
        {
            var verdict = await VerdictAsync(directory);
            Environment.SetEnvironmentVariable("SK_CHROME_PATH", Path.Combine(directory, "no-browser-here"));

            var (exit, output, _) = await RunAsync("evidence", "--verdict", verdict, "--contract", Sample("contract.yaml"), "--out", Path.Combine(directory, "pack"));

            Assert.Equal(ExitCodes.Success, exit);
            Assert.Contains("PDF: not written — SK_CHROME_PATH is set to", output, StringComparison.Ordinal);
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "pack", "manifest.json")))!;
            Assert.DoesNotContain(manifest["files"]!.AsArray(), f => f!["path"]!.GetValue<string>() == "report.pdf");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SK_CHROME_PATH", previous);
            Directory.Delete(directory, recursive: true);
        }
    }
}
