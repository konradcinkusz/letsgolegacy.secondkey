using System.CommandLine;
using System.Globalization;
using System.Text;
using SecondKey.Evidence.Sarif;

namespace SecondKey.Cli.Commands;

/// <summary>
/// <c>sk gate</c> (C5, S14): the gate's SARIF summarized per rule. Exits 1 when a finding
/// blocks — an error that is not suppressed and not known from the baseline — and 0 otherwise.
/// </summary>
internal static class GateCommand
{
    public static Command Build(CliContext context)
    {
        var sarif = new Option<string[]>("--sarif")
        {
            Description = "SARIF 2.1.0 logs from the gate's analyzers, e.g. Portcullis (default: gate.sarif).",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("gate", "Summarize the gate's SARIF per rule, and fail when a finding blocks (C5).") { sarif };
        command.SetAction(async (result, cancellationToken) =>
        {
            var config = context.LoadConfig(result);
            var paths = result.GetValue(sarif) is { Length: > 0 } given
                ? given.Select(Path.GetFullPath).ToList()
                : (config.Gate?.Sarif ?? []).Select(config.Resolve).ToList();
            if (paths.Count == 0)
            {
                await context.Error.WriteLineAsync("sk gate: needs at least one SARIF log — --sarif, or gate.sarif in secondkey.yaml".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var summary = GateSummary.From(paths.Select(SarifLog.Read));
            await context.Output.WriteAsync(Describe(summary).AsMemory(), cancellationToken).ConfigureAwait(false);
            return summary.Passes ? ExitCodes.Success : ExitCodes.Failed;
        });
        return command;
    }

    internal static string Describe(GateSummary summary)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"sk gate: {(summary.Passes ? "pass" : "fail")} — {summary.Blocking.Count} blocking of {summary.Findings} finding(s) in {summary.Logs.Count} log(s)");
        foreach (var rule in summary.Rules.Where(r => r.Errors + r.Warnings + r.Notes + r.Suppressed > 0))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {rule.RuleId,-40} errors {rule.Errors,3}  warnings {rule.Warnings,3}  notes {rule.Notes,3}  suppressed {rule.Suppressed,3}  blocking {rule.Blocking,3}");
        }

        foreach (var blocking in summary.Blocking)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  blocks: {blocking.Finding.Location ?? "(no location)"}  {blocking.Finding.RuleId}  {blocking.Finding.Message}");
        }

        return text.ToString();
    }
}
