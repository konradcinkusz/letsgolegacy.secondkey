using SecondKey.Artifacts;
using SecondKey.Evidence.Sarif;
using static SecondKey.Evidence.Tests.Fixtures;

namespace SecondKey.Evidence.Tests;

/// <summary>
/// The whole report for the sample, byte for byte. A change to the report shows up as a diff
/// of Golden/sample-report.html in review; regenerate it on purpose with SK_UPDATE_GOLDEN=1.
/// </summary>
public class GoldenReportTests
{
    private static string GoldenPath => Path.Combine(Root, "tests", "SecondKey.Evidence.Tests", "Golden", "sample-report.html");

    [Fact]
    public void The_sample_report_is_exactly_the_reviewed_one()
    {
        var html = HtmlReport.Render(new ReportInput
        {
            Verdict = SampleVerdict(),
            Contract = SampleContract().Document,
            Gate = GateSummary.From([SarifLog.Read(SampleSarif)]),
            Digests = [new DigestedFile("verdict.json", new string('c', 64)), new DigestedFile("sarif/portcullis.sarif", new string('d', 64))],
            GeneratedAt = At,
            Tool = new ToolInfo("secondkey", "0.1.0", "abc1234"),
        });

        if (Environment.GetEnvironmentVariable("SK_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(GoldenPath, html);
        }

        Assert.Equal(File.ReadAllText(GoldenPath), html);
    }
}
