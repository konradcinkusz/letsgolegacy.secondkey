using System.CommandLine;
using System.Globalization;
using System.Text;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Compare;

namespace SecondKey.Cli.Commands;

/// <summary>
/// <c>sk compare</c> (C7, S13): what the two sides answered, against the contract, into
/// verdict.json. Exits 0 on pass, 1 on fail, 2 on review — so a pipeline branches on the
/// verdict without reading it.
/// </summary>
internal static class CompareCommand
{
    public static Command Build(CliContext context)
    {
        var contract = new Option<string?>("--contract") { Description = "The contract.yaml to compare against (default: compare.contract)." };
        var run = new Option<string?>("--run") { Description = "The *.skrun to compare (default: compare.run, or .secondkey/run.skrun)." };
        var output = new Option<string?>("--out") { Description = "The verdict.json to write (default: compare.out, or .secondkey/verdict.json)." };

        var command = new Command("compare", "Compare what the legacy system and the candidate answered, against the contract, into verdict.json (C7).")
        {
            contract, run, output,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var config = context.LoadConfig(result);
            var section = config.Compare;
            var contractPath = result.GetValue(contract) is { } c ? Path.GetFullPath(c) : section?.Contract is { } sc ? config.Resolve(sc) : null;
            if (contractPath is null)
            {
                await context.Error.WriteLineAsync("sk compare: needs a contract — --contract, or compare.contract in secondkey.yaml".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var runPath = result.GetValue(run) is { } r ? Path.GetFullPath(r) : config.Resolve(section?.Run ?? ".secondkey/run.skrun");
            var outPath = result.GetValue(output) is { } o ? Path.GetFullPath(o) : config.Resolve(section?.Out ?? ".secondkey/verdict.json");

            var loaded = ContractFile.Load(contractPath);
            var document = RunFile.Read(runPath);
            if (document.Results.Count == 0)
            {
                await context.Error.WriteLineAsync($"sk compare: {runPath} has no results; nothing was compared, so nothing can pass".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.InvalidInput;
            }

            var verdict = Comparator.Compare(
                new ComparisonInputs
                {
                    Contract = loaded.Document,
                    ContractSha256 = loaded.Sha256,
                    ContractPath = Path.GetFileName(contractPath),
                    Run = document,
                    RunSha256 = Sha256Digest.OfFile(runPath),
                    RunPath = Path.GetFileName(runPath),
                },
                WholeSeconds(DateTimeOffset.UtcNow));
            await VerdictFile.WriteAsync(outPath, verdict, cancellationToken).ConfigureAwait(false);
            await context.Output.WriteAsync(Describe(verdict, outPath).AsMemory(), cancellationToken).ConfigureAwait(false);

            return verdict.Outcome switch
            {
                VerdictOutcome.Fail => ExitCodes.Failed,
                VerdictOutcome.Review => ExitCodes.Review,
                _ => ExitCodes.Success,
            };
        });
        return command;
    }

    /// <summary>The verdict in a few lines: the counts, then every exchange a person has to look at.</summary>
    internal static string Describe(VerdictDocument verdict, string path)
    {
        var s = verdict.Summary;
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"sk compare: {Word(verdict.Outcome)} — {s.Exchanges} exchange(s): {s.Equal} equal, {s.EqualUnderContract} equal under contract, {s.Regression} regression(s), {s.FixCandidate} fix candidate(s) -> {path}");
        foreach (var exchange in verdict.Exchanges.Where(e => e.Class is ExchangeClass.Regression or ExchangeClass.FixCandidate))
        {
            var request = $"{exchange.Request.Method} {exchange.Request.Path}{exchange.Request.Query}";
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Word(exchange.Class),-14} {exchange.Exchange}  {request}  {string.Join("; ", exchange.Reasons)}");
        }

        var clauses = s.Clauses;
        var share = clauses.AbsenceShare is { } a ? string.Create(CultureInfo.InvariantCulture, $", absence share {a * 100:0}%") : string.Empty;
        text.AppendLine(CultureInfo.InvariantCulture, $"  clauses: {clauses.Total} ({clauses.Accepted} accepted, {clauses.Exercised} exercised, {clauses.Unexercised} unexercised){share}");
        foreach (var clause in verdict.Clauses.Where(c => c.Accepted && c.Status is ClauseStatus.Regressed or ClauseStatus.Fixed or ClauseStatus.Unexercised))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {Word(clause.Status),-14} {clause.Id}");
        }

        return text.ToString();
    }

    /// <summary>A verdict records when it was made to the second; sub-second noise only makes two verdicts harder to compare.</summary>
    private static DateTimeOffset WholeSeconds(DateTimeOffset time) =>
        new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), time.Offset);

    private static string Word<T>(T value)
        where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value).Trim('"');
}
