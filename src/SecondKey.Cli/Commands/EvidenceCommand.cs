using System.CommandLine;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk evidence</c>. Implemented under ticket  verdict, contract, gate results, report (C10).:S14; until then it reports that it is not in this build.</summary>
internal static class EvidenceCommand
{
    public static Command Build(CliContext context) =>
        StubCommand.Build(context, "evidence", "Assemble the evidence pack", " verdict, contract, gate results, report (C10).:S14, phase 01");
}
