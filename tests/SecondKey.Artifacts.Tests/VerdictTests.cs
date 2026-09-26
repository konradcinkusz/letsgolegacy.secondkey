using System.Text.Json.Nodes;
using SecondKey.Artifacts.Validation;
using SecondKey.Artifacts.Verdicts;

namespace SecondKey.Artifacts.Tests;

public class VerdictTests
{
    [Fact]
    public void The_sample_verdict_validates()
    {
        var report = VerdictFile.Validate(File.ReadAllText(RepoPaths.Sample("verdict.json")), "sample");

        Assert.True(report.IsValid, report.ToString());
    }

    [Fact]
    public void The_sample_verdict_uses_all_four_classes()
    {
        var verdict = VerdictFile.Read(RepoPaths.Sample("verdict.json"));

        Assert.Equal(
            [ExchangeClass.Equal, ExchangeClass.EqualUnderContract, ExchangeClass.Regression, ExchangeClass.FixCandidate],
            verdict.Exchanges.Select(e => e.Class).Distinct().Order());
        Assert.Equal(VerdictOutcome.Fail, verdict.Outcome);
    }

    [Theory]
    [InlineData("class", "\"regression\"", "\"worse\"")]
    [InlineData("outcome", "\"outcome\": \"fail\"", "\"outcome\": \"maybe\"")]
    [InlineData("status", "\"status\": \"fixed\"", "\"status\": \"improved\"")]
    [InlineData("reasons", "\"uncovered-difference\"", "\"the model said so\"")]
    public void A_verdict_outside_the_vocabulary_is_refused(string field, string find, string replace)
    {
        var json = File.ReadAllText(RepoPaths.Sample("verdict.json")).Replace(find, replace, StringComparison.Ordinal);

        var report = VerdictFile.Validate(json, field);

        Assert.False(report.IsValid, field);
    }

    [Fact]
    public async Task A_verdict_round_trips_through_the_writer()
    {
        var verdict = VerdictFile.Read(RepoPaths.Sample("verdict.json"));
        var path = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.json");
        try
        {
            await VerdictFile.WriteAsync(path, verdict);
            var again = VerdictFile.Read(path);

            Assert.Equal(verdict.Summary, again.Summary with { Clauses = verdict.Summary.Clauses });
            Assert.Equal(verdict.Exchanges.Count, again.Exchanges.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_writer_refuses_a_document_that_breaks_its_own_schema()
    {
        var verdict = VerdictFile.Read(RepoPaths.Sample("verdict.json"));
        var broken = verdict with
        {
            Inputs = verdict.Inputs with { Contract = verdict.Inputs.Contract with { Sha256 = "not-a-digest" } },
        };

        await Assert.ThrowsAsync<ArtifactValidationException>(() => VerdictFile.WriteAsync(Path.GetTempFileName(), broken));
    }

    [Fact]
    public void Text_that_is_not_json_is_refused()
    {
        var report = VerdictFile.Validate("{ nope", "garbage");

        Assert.StartsWith("not valid JSON", report.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_difference_keeps_arbitrary_json_values()
    {
        var difference = new Difference { Path = "response.status", Kind = DifferenceKind.Changed, Legacy = JsonValue.Create(500), Candidate = JsonValue.Create(400) };

        Assert.Equal(500, difference.Legacy!.GetValue<int>());
    }

    [Fact]
    public async Task The_writer_creates_missing_directories_and_ends_the_file_with_a_newline()
    {
        var verdict = VerdictFile.Read(RepoPaths.Sample("verdict.json"));
        var root = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "out", "verdict.json");
        try
        {
            await VerdictFile.WriteAsync(path, verdict);

            var text = await File.ReadAllTextAsync(path);
            Assert.EndsWith("}\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\uFEFF", text, StringComparison.Ordinal);
            Assert.Contains("\"class\": \"equal-under-contract\"", text, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Reading_an_invalid_verdict_file_throws_with_the_path()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var ex = Assert.Throws<ArtifactValidationException>(() => VerdictFile.Read(path));
            Assert.Equal(path, ex.Report.Artifact);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
