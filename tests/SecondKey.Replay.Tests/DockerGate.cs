namespace SecondKey.Replay.Tests;

/// <summary>
/// Whether the tests that need Docker (a real SQL Server) run in this process, and if not,
/// why. One decision serves the attribute that skips the tests and the fixture that would
/// start their container, so a skipped test never costs a container.
/// </summary>
internal static class DockerGate
{
    /// <summary>Null when the Docker tests run; otherwise the reason they are skipped.</summary>
    public static string? SkipReason { get; } = Decide(
        required: Environment.GetEnvironmentVariable("SK_REQUIRE_DOCKER") == "1",
        skipped: Environment.GetEnvironmentVariable("SK_SKIP_DOCKER") == "1",
        available: Environment.GetEnvironmentVariable("DOCKER_HOST") is not null || File.Exists("/var/run/docker.sock"));

    /// <summary>
    /// SK_SKIP_DOCKER=1 is a deliberate, per-job decision, so it wins over SK_REQUIRE_DOCKER=1,
    /// which CI sets for every job: the mutation job inherits the requirement and still skips
    /// on purpose. Otherwise a required Docker runs the tests, so a missing one fails them
    /// (analysis accepted risk A1), and an optional one runs them only when it is there.
    /// </summary>
    internal static string? Decide(bool required, bool skipped, bool available) =>
        skipped ? "SK_SKIP_DOCKER=1: the SQL Server tests are left to the build-and-test job."
        : required || available ? null
        : "Docker is not available; this test needs a real SQL Server (CI runs it: SK_REQUIRE_DOCKER=1).";
}
