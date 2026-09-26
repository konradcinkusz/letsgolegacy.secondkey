namespace SecondKey.Replay.Tests;

/// <summary>
/// A test that needs Docker (a real SQL Server). Without Docker it is reported as skipped,
/// with the reason — never passed. In CI, where SK_REQUIRE_DOCKER=1, it always runs, so a
/// missing Docker fails the build instead of hiding a test (analysis accepted risk A1).
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        var required = Environment.GetEnvironmentVariable("SK_REQUIRE_DOCKER") == "1";
        var available = Environment.GetEnvironmentVariable("DOCKER_HOST") is not null || File.Exists("/var/run/docker.sock");
        if (!required && !available)
        {
            Skip = "Docker is not available; this test needs a real SQL Server (CI runs it: SK_REQUIRE_DOCKER=1).";
        }
    }
}
