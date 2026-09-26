using static SecondKey.Cli.Tests.CliRunner;

namespace SecondKey.Cli.Tests;

public class CommandLineTests
{
    private static readonly string[] Steps = ["capture", "mine", "replay", "compare", "gate", "mutate", "evidence"];

    [Fact]
    public async Task Help_lists_every_step_of_the_chain()
    {
        var (exit, output, _) = await RunAsync("--help");

        Assert.Equal(ExitCodes.Success, exit);
        foreach (var step in Steps)
        {
            Assert.Contains($"  {step} ", output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("mine")]
    [InlineData("mutate")]
    public async Task A_stub_says_where_it_is_planned_and_exits_70(string step)
    {
        var (exit, _, error) = await RunAsync(step);

        Assert.Equal(ExitCodes.NotImplemented, exit);
        Assert.Contains($"sk {step}: not available in this build — planned under ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_is_a_usage_error()
    {
        var (exit, _, error) = await RunAsync("deploy");

        Assert.Equal(ExitCodes.Usage, exit);
        Assert.Contains("Run 'sk --help' for usage.", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_configuration_file_is_invalid_input()
    {
        var (exit, _, error) = await RunAsync("mine", "--config", Path.Combine(Path.GetTempPath(), "no-such-secondkey.yaml"));

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains("no such configuration file", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exit_codes_are_distinct()
    {
        int[] codes = [ExitCodes.Success, ExitCodes.Failed, ExitCodes.Review, ExitCodes.InvalidInput, ExitCodes.RuntimeError, ExitCodes.Usage, ExitCodes.NotImplemented];

        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal([0, 1, 2, 3, 4, 64, 70], codes);
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => SecondKeyCli.RunAsync(null!, TextWriter.Null, TextWriter.Null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SecondKeyCli.RunAsync([], null!, TextWriter.Null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => SecondKeyCli.RunAsync([], TextWriter.Null, null!, CancellationToken.None));
    }
}
