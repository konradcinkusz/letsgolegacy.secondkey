using System.CommandLine;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk replay</c>. Implemented under ticket S10; until then it reports that it is not in this build.</summary>
internal static class ReplayCommand
{
    public static Command Build(CliContext context) =>
        StubCommand.Build(context, "replay", "Replay a capture against the legacy system and the candidate under identical conditions (C6).", "S10, phase 01");
}
