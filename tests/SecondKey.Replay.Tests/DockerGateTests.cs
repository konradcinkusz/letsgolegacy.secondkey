namespace SecondKey.Replay.Tests;

public sealed class DockerGateTests
{
    [Theory]
    [InlineData(true, true, true)] // the CI mutation job: the deliberate skip wins over the requirement CI sets
    [InlineData(false, true, true)] // a local run that skips on purpose
    [InlineData(false, false, false)] // a local run without Docker
    public void The_docker_tests_are_skipped_with_a_reason(bool required, bool skipped, bool available)
    {
        Assert.NotNull(DockerGate.Decide(required, skipped, available));
    }

    [Theory]
    [InlineData(true, false, false)] // CI build-and-test without Docker: the tests run and fail (A1)
    [InlineData(true, false, true)] // CI build-and-test
    [InlineData(false, false, true)] // a local run with Docker
    public void The_docker_tests_run(bool required, bool skipped, bool available)
    {
        Assert.Null(DockerGate.Decide(required, skipped, available));
    }
}
