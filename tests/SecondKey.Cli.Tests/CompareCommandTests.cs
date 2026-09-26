using System.Text.Json;
using SecondKey.Artifacts.Verdicts;
using static SecondKey.Cli.Tests.CliRunner;

namespace SecondKey.Cli.Tests;

public class CompareCommandTests
{
    private static string TempDirectory() => Directory.CreateTempSubdirectory("sk-compare-").FullName;

    [Fact]
    public async Task The_sample_run_fails_and_the_verdict_is_written_and_valid()
    {
        var directory = TempDirectory();
        var verdictPath = Path.Combine(directory, "out", "verdict.json");
        try
        {
            var (exit, output, error) = await RunAsync("compare", "--contract", Sample("contract.yaml"), "--run", Sample("sample.skrun"), "--out", verdictPath);

            Assert.Equal(ExitCodes.Failed, exit);
            Assert.Equal(string.Empty, error);
            Assert.StartsWith("sk compare: fail — 5 exchange(s): 2 equal, 1 equal under contract, 1 regression(s), 1 fix candidate(s) -> ", output, StringComparison.Ordinal);
            Assert.Contains("  regression     ex-000001  GET /products?sort=name  uncovered-difference", output, StringComparison.Ordinal);
            Assert.Contains("  fix-candidate  ex-000005  POST /checkout  clause-fixed: CHECKOUT-NEVER-5XX", output, StringComparison.Ordinal);
            Assert.Contains("  clauses: 6 (5 accepted, 5 exercised, 0 unexercised), absence share 60%", output, StringComparison.Ordinal);
            Assert.Contains("  fixed          CHECKOUT-NEVER-5XX", output, StringComparison.Ordinal);

            var verdict = VerdictFile.Read(verdictPath);
            Assert.Equal(VerdictOutcome.Fail, verdict.Outcome);
            Assert.Equal("contract.yaml", verdict.Inputs.Contract.Path);
            Assert.Equal("sample.skrun", verdict.Inputs.Run.Path);
            Assert.Equal(Artifacts.Hashing.Sha256Digest.OfFile(Sample("sample.skrun")), verdict.Inputs.Run.Sha256);
            Assert.Equal(0, verdict.CreatedAt.Millisecond);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("ex-000001", ExitCodes.Review)]
    [InlineData("ex-000005", ExitCodes.Success)]
    public async Task The_exit_code_follows_the_outcome(string dropped, int expected)
    {
        // Without the regression the sample run is "review"; without both it is "pass".
        var directory = TempDirectory();
        try
        {
            var run = Path.Combine(directory, "run.skrun");
            var lines = File.ReadAllLines(Sample("sample.skrun")).Where(l => !l.Contains("\"ex-000001\"", StringComparison.Ordinal)).ToList();
            if (dropped == "ex-000005")
            {
                lines = lines.Where(l => !l.Contains("\"ex-000005\"", StringComparison.Ordinal)).ToList();
            }

            var results = lines.Count(l => l.Contains("\"exchange.result\"", StringComparison.Ordinal));
            lines[^1] = lines[^1].Replace("\"results\": 10", $"\"results\": {results}", StringComparison.Ordinal).Replace("\"results\":10", $"\"results\":{results}", StringComparison.Ordinal);
            await File.WriteAllLinesAsync(run, lines);

            var (exit, output, error) = await RunAsync("compare", "--contract", Sample("contract.yaml"), "--run", run, "--out", Path.Combine(directory, "verdict.json"));

            Assert.True(expected == exit, output + error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Paths_come_from_the_configuration_file_relative_to_it()
    {
        var directory = TempDirectory();
        try
        {
            File.Copy(Sample("contract.yaml"), Path.Combine(directory, "contract.yaml"));
            Directory.CreateDirectory(Path.Combine(directory, ".secondkey"));
            File.Copy(Sample("sample.skrun"), Path.Combine(directory, ".secondkey", "run.skrun"));
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\ncompare: { contract: contract.yaml }\n");

            var (exit, _, _) = await RunAsync("compare", "--config", config);

            Assert.Equal(ExitCodes.Failed, exit);
            Assert.True(File.Exists(Path.Combine(directory, ".secondkey", "verdict.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_contract_it_is_a_usage_error()
    {
        var directory = TempDirectory();
        try
        {
            var config = Path.Combine(directory, "secondkey.yaml");
            await File.WriteAllTextAsync(config, "version: 1\n");

            var (exit, _, error) = await RunAsync("compare", "--config", config, "--run", Sample("sample.skrun"));

            Assert.Equal(ExitCodes.Usage, exit);
            Assert.Contains("needs a contract", error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_invalid_contract_is_invalid_input_and_nothing_is_written()
    {
        var directory = TempDirectory();
        try
        {
            var contract = Path.Combine(directory, "contract.yaml");
            await File.WriteAllTextAsync(contract, "apiVersion: secondkey/v1\n");
            var verdict = Path.Combine(directory, "verdict.json");

            var (exit, _, error) = await RunAsync("compare", "--contract", contract, "--run", Sample("sample.skrun"), "--out", verdict);

            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains("Required properties", error, StringComparison.Ordinal);
            Assert.False(File.Exists(verdict));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_run_without_results_is_invalid_input_because_nothing_can_pass_vacuously()
    {
        var directory = TempDirectory();
        try
        {
            var run = Path.Combine(directory, "empty.skrun");
            var header = File.ReadLines(Sample("sample.skrun")).First();
            await File.WriteAllLinesAsync(run, [header, """{"type":"run.end","v":1,"finishedAt":"2026-09-26T10:00:10Z","scenarios":0,"results":0}"""]);

            var (exit, _, error) = await RunAsync("compare", "--contract", Sample("contract.yaml"), "--run", run, "--out", Path.Combine(directory, "verdict.json"));

            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains("has no results; nothing was compared, so nothing can pass", error, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(directory, "verdict.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_run_is_reported()
    {
        var (exit, _, error) = await RunAsync("compare", "--contract", Sample("contract.yaml"), "--run", Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.skrun"));

        Assert.NotEqual(ExitCodes.Success, exit);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void The_description_lists_only_what_a_person_must_look_at()
    {
        var verdict = VerdictFile.Read(Sample("verdict.json"));
        var passing = verdict with
        {
            Outcome = VerdictOutcome.Pass,
            Exchanges = verdict.Exchanges.Where(e => e.Class is ExchangeClass.Equal or ExchangeClass.EqualUnderContract).ToList(),
            Clauses = verdict.Clauses.Select(c => c with { Status = ClauseStatus.Held }).ToList(),
            Summary = verdict.Summary with { Clauses = verdict.Summary.Clauses with { AbsenceShare = null } },
        };

        var text = Commands.CompareCommand.Describe(passing, "v.json");

        Assert.StartsWith("sk compare: pass — ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ex-0000", text, StringComparison.Ordinal);
        Assert.DoesNotContain("absence share", text, StringComparison.Ordinal);
        Assert.DoesNotContain("held", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Unexercised_and_regressed_accepted_clauses_are_named_and_proposed_ones_are_not()
    {
        var verdict = VerdictFile.Read(Sample("verdict.json"));
        var clauses = verdict.Clauses.Select(c => c.Id switch
        {
            "TOTAL-NEVER-NEGATIVE" => c with { Status = ClauseStatus.Regressed },
            "ORDER-HAS-LOCATION" => c with { Status = ClauseStatus.Unexercised },
            "PRODUCTS-PRICED" => c with { Status = ClauseStatus.Regressed },
            _ => c,
        }).ToList();

        var text = Commands.CompareCommand.Describe(verdict with { Clauses = clauses }, "v.json");

        Assert.Contains("  regressed      TOTAL-NEVER-NEGATIVE", text, StringComparison.Ordinal);
        Assert.Contains("  unexercised    ORDER-HAS-LOCATION", text, StringComparison.Ordinal);
        Assert.DoesNotContain("PRODUCTS-PRICED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("violated-both", text, StringComparison.Ordinal);
    }
}
