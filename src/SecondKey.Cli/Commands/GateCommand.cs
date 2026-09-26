using System.CommandLine;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk gate</c>. Implemented under ticket S14; until then it reports that it is not in this build.</summary>
internal static class GateCommand
{
    public static Command Build(CliContext context) =>
        StubCommand.Build(context, "gate", "Summarize the gate's SARIF per rule and fail when a finding blocks (C5).", "S14, phase 01");
}
