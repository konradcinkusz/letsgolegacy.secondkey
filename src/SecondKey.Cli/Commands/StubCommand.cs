using System.CommandLine;

namespace SecondKey.Cli.Commands;

/// <summary>A command that exists in the plan but not in this build. It says so and exits 70.</summary>
internal static class StubCommand
{
    public static Command Build(CliContext context, string name, string description, string plannedUnder)
    {
        var command = new Command(name, $"{description} Not in this build — planned under {plannedUnder}.");
        command.SetAction(async (result, cancellationToken) =>
        {
            // Every command reads the configuration first, so a broken one is reported whichever command runs.
            _ = context.LoadConfig(result);
            await context.Error.WriteLineAsync($"sk {name}: not available in this build — planned under {plannedUnder} (docs/WORKPLAN.md).".AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodes.NotImplemented;
        });
        return command;
    }
}
