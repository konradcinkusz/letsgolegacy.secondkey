namespace SecondKey.Replay.Tests;

/// <summary>
/// A test that needs Docker (a real SQL Server). Without Docker it is reported as skipped,
/// with the reason — never passed. In CI, where SK_REQUIRE_DOCKER=1, it always runs, so a
/// missing Docker fails the build instead of hiding a test (analysis accepted risk A1).
/// SK_SKIP_DOCKER=1 skips it on purpose, even where SK_REQUIRE_DOCKER=1: the mutation job
/// sets it, because a SQL Server container per mutant is far outside its budget and the SQL
/// Server code is excluded from mutation (scripts/mutation.sh). The decision is
/// <see cref="DockerGate"/>'s.
/// </summary>
public sealed class DockerFactAttribute : FactAttribute
{
    public DockerFactAttribute()
    {
        Skip = DockerGate.SkipReason;
    }
}
