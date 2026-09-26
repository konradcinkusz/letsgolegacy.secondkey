using System.Text.Json.Nodes;
using SecondKey.Artifacts.Runs;

namespace SecondKey.Artifacts.Tests;

public class SkrunTests
{
    [Fact]
    public void The_sample_run_validates()
    {
        var report = RunFile.Validate(RepoPaths.Sample("sample.skrun"));

        Assert.True(report.IsValid, report.ToString());
    }

    [Fact]
    public void The_sample_run_reads_both_sides_of_every_exchange()
    {
        var run = RunFile.Read(RepoPaths.Sample("sample.skrun"));

        Assert.Equal(10, run.Results.Count);
        Assert.Equal(4, run.Resets.Count);
        Assert.Equal(ScenarioMode.Session, run.Header.ScenarioMode);
        Assert.Equal(ResetMethod.Http, run.Header.Sides.Legacy.Reset);
        Assert.Equal(500, run.Find("ex-000005", Side.Legacy)!.Response!.Status);
        Assert.Equal(400, run.Find("ex-000005", Side.Candidate)!.Response!.Status);
        Assert.Null(run.Find("ex-999999", Side.Legacy));
    }

    public static TheoryData<string, Func<List<string>, List<string>>, string> BrokenRuns => new()
    {
        { "the run.end line is missing", l => l[..^1], "run was interrupted" },
        { "a result has a response and an error", l => Edit(l, 3, n => n["error"] = new JsonObject { ["kind"] = "timeout", ["message"] = "x" }), "line 3" },
        { "a result has neither a response nor an error", l => Edit(l, 3, n => n.AsObject().Remove("response")), "line 3" },
        { "an exchange has two results on one side", l => [.. l[..^1], l[2], l[^1]], "already has a legacy result" },
        { "the result count disagrees with run.end", l => Edit(l, l.Count, n => n["results"] = 3), "run.end says 3 result(s)" },
        { "something follows run.end", l => [.. l, l[1]], "nothing may follow run.end" },
        { "the side is unknown", l => Edit(l, 3, n => n["side"] = "staging"), "/side" },
        { "the header is not first", l => l[1..], "first line of a run must be its run.header" },
    };

    [Theory]
    [MemberData(nameof(BrokenRuns))]
    public void A_broken_run_is_refused(string why, Func<List<string>, List<string>> breakIt, string expected)
    {
        var lines = breakIt([.. RepoPaths.SampleLines("sample.skrun")]);

        var report = RunFile.Validate(new StringReader(string.Join('\n', lines)), why);

        Assert.False(report.IsValid, why);
        Assert.Contains(report.Errors, e => e.ToString().Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_writer_produces_a_run_the_reader_accepts()
    {
        var sample = RunFile.Read(RepoPaths.Sample("sample.skrun"));
        var path = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skrun");
        try
        {
            await using (var writer = await RunWriter.CreateAsync(path, sample.Header))
            {
                await writer.WriteAsync(sample.Resets[0]);
                await writer.WriteAsync(sample.Results[0]);
                await writer.WriteAsync(sample.End with { Results = writer.Results + 0 });
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteAsync(sample.Results[1]));
            }

            var run = RunFile.Read(path);
            Assert.Single(run.Results);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<string> Edit(List<string> lines, int line, Action<JsonNode> edit)
    {
        var copy = new List<string>(lines);
        var node = JsonNode.Parse(copy[line - 1])!;
        edit(node);
        copy[line - 1] = node.ToJsonString();
        return copy;
    }

    [Fact]
    public void A_second_header_and_an_event_before_the_header_are_reported()
    {
        var lines = RepoPaths.SampleLines("sample.skrun");
        var twoHeaders = string.Join('\n', lines.Take(2).Append(lines[0]).Concat(lines.Skip(2)));
        var headless = string.Join('\n', lines.Skip(1));

        Assert.Contains(new SecondKey.Artifacts.Validation.ArtifactError(3, "/type", "a run has exactly one run.header, on its first line"), RunFile.Validate(new StringReader(twoHeaders), "two").Errors);
        Assert.Contains(new SecondKey.Artifacts.Validation.ArtifactError(1, "/type", "the first line of a run must be its run.header"), RunFile.Validate(new StringReader(headless), "headless").Errors);
    }

    [Fact]
    public void An_empty_run_reports_one_error()
    {
        Assert.Equal(
            [new SecondKey.Artifacts.Validation.ArtifactError(null, "", "empty run: the first line must be a run.header")],
            RunFile.Validate(new StringReader(""), "empty").Errors);
    }

    [Fact]
    public void A_count_mismatch_points_at_the_end_line()
    {
        var lines = RepoPaths.SampleLines("sample.skrun");
        lines[^1] = lines[^1].Replace("\"results\":10", "\"results\":9", StringComparison.Ordinal);

        var error = Assert.Single(RunFile.Validate(new StringReader(string.Join('\n', lines)), "count").Errors);

        Assert.Equal(new SecondKey.Artifacts.Validation.ArtifactError(lines.Length, "/results", "run.end says 9 result(s) but the file holds 10"), error);
    }

    [Fact]
    public void Reading_an_invalid_run_throws()
    {
        Assert.Throws<SecondKey.Artifacts.Validation.ArtifactValidationException>(() => RunFile.Read(new StringReader(""), "empty"));
    }
}
