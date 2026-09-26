using System.CommandLine;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk capture</c>. Implemented under ticket S9; until then it reports that it is not in this build.</summary>
internal static class CaptureCommand
{
    public static Command Build(CliContext context) =>
        StubCommand.Build(context, "capture", "Record traffic to and from the legacy system through a proxy, without touching it (C1).", "S9, phase 01");
}
