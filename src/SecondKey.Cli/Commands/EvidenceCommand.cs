using System.CommandLine;
using System.Globalization;
using SecondKey.Cli.Configuration;
using SecondKey.Evidence;

namespace SecondKey.Cli.Commands;

/// <summary>
/// <c>sk evidence</c> (C10, S14): assembles the unsigned evidence pack. Exits 0 when the pack
/// is written, whatever the verdict says — the verdict's own exit code is <c>sk compare</c>'s —
/// so a pipeline can always publish the pack that explains a failure.
/// </summary>
internal static class EvidenceCommand
{
    public static Command Build(CliContext context)
    {
        var verdict = new Option<string?>("--verdict") { Description = "The verdict.json (default: evidence.verdict, or .secondkey/verdict.json)." };
        var contract = new Option<string?>("--contract") { Description = "The contract the verdict was computed from (default: evidence.contract, then compare.contract)." };
        var run = new Option<string?>("--run") { Description = "Include the run the verdict was computed from, so anyone can recompute it (default: evidence.run; left out when unset)." };
        var sarif = new Option<string[]>("--sarif") { Description = "The gate's SARIF logs (default: evidence.sarif).", AllowMultipleArgumentsPerToken = true };
        var output = new Option<string?>("--out") { Description = "The directory the pack is written to (default: evidence.out, or .secondkey/evidence)." };
        var pdf = new Option<string?>("--pdf") { Description = "auto (default): a PDF when a Chromium-based browser is available; required: fail without one; off." };
        pdf.AcceptOnlyFromAmong("auto", "required", "off");

        var command = new Command("evidence", "Assemble the evidence pack: report, verdict, contract, gate results, manifest and statement (C10).")
        {
            verdict, contract, run, sarif, output, pdf,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var config = context.LoadConfig(result);
            var section = config.Evidence;
            string? Resolve(string? option, string? configured) =>
                option is not null ? Path.GetFullPath(option) : configured is not null ? config.Resolve(configured) : null;

            var contractPath = Resolve(result.GetValue(contract), section?.Contract ?? config.Compare?.Contract);
            if (contractPath is null)
            {
                await context.Error.WriteLineAsync("sk evidence: needs the contract — --contract, or evidence.contract in secondkey.yaml".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var options = new EvidenceOptions
            {
                VerdictPath = Resolve(result.GetValue(verdict), section?.Verdict) ?? config.Resolve(".secondkey/verdict.json"),
                ContractPath = contractPath,
                RunPath = Resolve(result.GetValue(run), section?.Run),
                SarifPaths = result.GetValue(sarif) is { Length: > 0 } given
                    ? given.Select(Path.GetFullPath).ToList()
                    : (section?.Sarif ?? []).Select(config.Resolve).ToList(),
                OutputDirectory = Resolve(result.GetValue(output), section?.Out) ?? config.Resolve(".secondkey/evidence"),
                Pdf = (result.GetValue(pdf) ?? section?.Pdf ?? "auto") switch
                {
                    "required" => PdfMode.Required,
                    "off" => PdfMode.Off,
                    "auto" => PdfMode.Auto,
                    // --pdf is checked by the parser (64) and evidence.pdf when the file is read (3);
                    // this arm only keeps the switch total.
                    var other => throw new ConfigurationException($"evidence.pdf: '{other}' is not one of auto, required, off"),
                },
            };

            EvidenceResult pack;
            try
            {
                pack = await EvidencePack.WriteAsync(options, WholeSeconds(DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            }
            catch (EvidenceOutputException ex)
            {
                await context.Error.WriteLineAsync($"sk evidence: {ex.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var gate = pack.Gate is null ? "not supplied" : pack.Gate.Passes ? "pass" : $"fail ({pack.Gate.Blocking.Count} blocking)";
            var pdfLine = pack.Pdf.Written ? $"written with {pack.Pdf.Browser}" : $"not written — {pack.Pdf.Reason}";
            await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"""
                sk evidence: pack written to {pack.Directory}
                  {string.Join(", ", pack.Files.Select(f => f.Path))}, {EvidencePack.ManifestName}
                  behaviour: {Word(pack.Verdict.Outcome)} · gate: {gate} · PDF: {pdfLine}
                """).AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static DateTimeOffset WholeSeconds(DateTimeOffset time) =>
        new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), time.Offset);

    private static string Word<T>(T value)
        where T : struct, Enum => System.Text.Json.JsonSerializer.Serialize(value).Trim('"');
}
