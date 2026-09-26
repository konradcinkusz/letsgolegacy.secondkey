using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Tests;

public class ValidationReportTests
{
    [Fact]
    public void An_error_with_a_line_reads_line_location_message()
    {
        Assert.Equal("line 3 /request/method: bad", new ArtifactError(3, "/request/method", "bad").ToString());
    }

    [Fact]
    public void An_error_without_a_location_points_at_the_root()
    {
        Assert.Equal("line 1 /: bad", new ArtifactError(1, "", "bad").ToString());
        Assert.Equal("/: bad", new ArtifactError(null, "", "bad").ToString());
        Assert.Equal("/kind: bad", new ArtifactError(null, "/kind", "bad").ToString());
    }

    [Fact]
    public void A_report_lists_every_error_under_its_artifact()
    {
        var report = new ValidationReport("a.skcap", [new ArtifactError(1, "/a", "x"), new ArtifactError(2, "/b", "y")]);

        Assert.False(report.IsValid);
        Assert.Equal($"a.skcap: 2 error(s){Environment.NewLine}  line 1 /a: x{Environment.NewLine}  line 2 /b: y", report.ToString());
        Assert.Equal("a.skcap: valid", new ValidationReport("a.skcap", []).ToString());
    }

    [Fact]
    public void The_exception_carries_the_report_and_its_text()
    {
        var report = new ValidationReport("c.yaml", [new ArtifactError(null, "/kind", "wrong")]);

        var ex = new ArtifactValidationException(report);

        Assert.Same(report, ex.Report);
        Assert.Equal(report.ToString(), ex.Message);
        Assert.Equal("m", new ArtifactValidationException("m", new InvalidOperationException()).Report.Artifact);
        Assert.True(new ArtifactValidationException().Report.IsValid);
    }

    [Fact]
    public void Digests_are_lower_case_hex_sha256()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Sha256Digest.OfBytes([]));
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Sha256Digest.OfFile(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
