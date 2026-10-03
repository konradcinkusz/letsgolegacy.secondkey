using SecondKey.Artifacts;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Contract;

namespace SecondKey.Compare;

/// <summary>What one comparison reads, with the digests that bind the verdict to it.</summary>
public sealed record ComparisonInputs
{
    public required ContractDocument Contract { get; init; }

    public required string ContractSha256 { get; init; }

    /// <summary>The name the contract is reported under; a file name, never a local directory.</summary>
    public string? ContractPath { get; init; }

    public required RunDocument Run { get; init; }

    public required string RunSha256 { get; init; }

    /// <summary>The name the run is reported under; a file name, never a local directory.</summary>
    public string? RunPath { get; init; }
}

/// <summary>
/// C7 (S13): places every replayed exchange in exactly one of four classes and derives the
/// run's outcome, as ADR 0003 decides. Deterministic: the same contract and the same run
/// always give the same verdict, apart from the time it was made, the build of the tool that
/// made it and the names of the input files (design rule 1).
/// </summary>
public static class Comparator
{
    public static VerdictDocument Compare(ComparisonInputs inputs, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var contract = inputs.Contract;
        var run = inputs.Run;
        var engine = ContractEngine.Compile(contract);
        var report = engine.EvaluateRun(run);
        var normalizer = new Normalizer(contract.Normalize);
        var results = run.Results.ToDictionary(r => (r.Exchange, r.Side));

        var exchanges = run.Results
            .Select(r => r.Exchange)
            .Distinct(StringComparer.Ordinal)
            .Select(id => CompareExchange(
                id,
                results.GetValueOrDefault((id, Side.Legacy)),
                results.GetValueOrDefault((id, Side.Candidate)),
                run.Header.Sides,
                engine,
                report,
                normalizer,
                contract.Compare))
            .ToList();

        var accepted = report.Clauses.Where(c => c.Accepted).ToList();
        var summary = new VerdictSummary
        {
            Scenarios = run.Results.Select(r => r.Scenario).Distinct(StringComparer.Ordinal).Count(),
            Exchanges = exchanges.Count,
            Equal = exchanges.Count(e => e.Class == ExchangeClass.Equal),
            EqualUnderContract = exchanges.Count(e => e.Class == ExchangeClass.EqualUnderContract),
            Regression = exchanges.Count(e => e.Class == ExchangeClass.Regression),
            FixCandidate = exchanges.Count(e => e.Class == ExchangeClass.FixCandidate),
            Clauses = new ClauseCounts
            {
                Total = report.Clauses.Count,
                Accepted = accepted.Count,
                Exercised = accepted.Count(c => c.Exercised > 0),
                Unexercised = accepted.Count(c => c.Exercised == 0),
                AbsenceShare = accepted.Count == 0 ? null : Math.Round((double)accepted.Count(c => c.Kind == "never") / accepted.Count, 4),
            },
        };

        return new VerdictDocument
        {
            CreatedAt = createdAt,
            Tool = ToolInfo.Current,
            Inputs = new VerdictInputs
            {
                Contract = new ContractInput
                {
                    Name = contract.Metadata.Name,
                    Revision = contract.Metadata.Revision,
                    Sha256 = inputs.ContractSha256,
                    Path = inputs.ContractPath,
                },
                Run = new RunInput { RunId = run.Header.RunId, Sha256 = inputs.RunSha256, Path = inputs.RunPath },
            },
            Outcome = Outcome(summary),
            Summary = summary,
            Exchanges = exchanges,
            Clauses = report.Clauses,
        };
    }

    /// <summary><c>fail</c> on any regression; otherwise <c>review</c> on any fix candidate; otherwise <c>pass</c>.</summary>
    public static VerdictOutcome Outcome(VerdictSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return summary.Regression > 0 ? VerdictOutcome.Fail
            : summary.FixCandidate > 0 ? VerdictOutcome.Review
            : VerdictOutcome.Pass;
    }

    /// <summary>
    /// The class of one exchange, in the order ADR 0003 fixes: a side without an answer; a
    /// clause kept by legacy and broken by the candidate; a clause broken by legacy and kept
    /// by the candidate; no difference; only differences the contract's rules explain;
    /// anything else. Only accepted clauses decide.
    /// </summary>
    internal static (ExchangeClass Class, IReadOnlyList<string> Reasons) Classify(
        IReadOnlyList<string> missing,
        IReadOnlyList<ClauseResultPair> acceptedClauses,
        IReadOnlyList<Difference> differences,
        bool equalUnderContract)
    {
        if (missing.Count > 0)
        {
            return (ExchangeClass.Regression, missing.Select(m => $"missing-result: {m}").ToList());
        }

        var regressed = acceptedClauses
            .Where(c => c.Legacy == ClauseOutcome.Pass && c.Candidate is ClauseOutcome.Fail or ClauseOutcome.Error)
            .Select(c => $"clause-regression: {c.Id}")
            .ToList();
        if (regressed.Count > 0)
        {
            return (ExchangeClass.Regression, regressed);
        }

        var fixes = acceptedClauses
            .Where(c => c.Legacy is ClauseOutcome.Fail or ClauseOutcome.Error && c.Candidate == ClauseOutcome.Pass)
            .Select(c => $"clause-fixed: {c.Id}")
            .ToList();
        if (fixes.Count > 0)
        {
            return (ExchangeClass.FixCandidate, fixes);
        }

        if (differences.Count == 0)
        {
            return (ExchangeClass.Equal, ["identical"]);
        }

        if (equalUnderContract)
        {
            var rules = differences
                .SelectMany(d => d.NormalizedBy?.Split(", ") ?? [])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(rule => $"normalized: {rule}")
                .ToList();
            return (ExchangeClass.EqualUnderContract, rules);
        }

        return (ExchangeClass.Regression, ["uncovered-difference"]);
    }

    /// <summary>What the two sides reported for one clause, in the words of whichever side broke it.</summary>
    internal static string? Detail(ClauseEvaluation legacy, ClauseEvaluation candidate) => (legacy.Detail, candidate.Detail) switch
    {
        (null, null) => null,
        ({ } l, null) => $"legacy: {l}",
        (null, { } c) => $"candidate: {c}",
        ({ } l, { } c) when string.Equals(l, c, StringComparison.Ordinal) => $"both: {l}",
        ({ } l, { } c) => $"legacy: {l}; candidate: {c}",
    };

    private static ExchangeVerdict CompareExchange(
        string id,
        ExchangeResult? legacy,
        ExchangeResult? candidate,
        RunSides sides,
        ContractEngine engine,
        ContractReport report,
        Normalizer normalizer,
        CompareSettings? settings)
    {
        var legacyDocument = legacy is null ? [] : ComparableDocument.Build(report.Observations[(id, Side.Legacy)], settings, sides.Legacy.BaseUrl);
        var candidateDocument = candidate is null ? [] : ComparableDocument.Build(report.Observations[(id, Side.Candidate)], settings, sides.Candidate.BaseUrl);
        var raw = JsonDiff.Compare(legacyDocument, candidateDocument);
        var normalized = raw.Count == 0
            ? []
            : JsonDiff.Compare(normalizer.Apply(legacyDocument), normalizer.Apply(candidateDocument), normalizer.NumbersEqual);
        var differences = raw.Select(c => c.Difference with { NormalizedBy = normalizer.Explain(c, normalized) }).ToList();

        var clauses = new List<ClauseResultPair>();
        var accepted = new List<ClauseResultPair>();
        for (var i = 0; i < engine.Clauses.Count; i++)
        {
            var l = Evaluation(report, id, Side.Legacy, i);
            var c = Evaluation(report, id, Side.Candidate, i);
            if (l.Outcome == ClauseOutcome.NotApplicable && c.Outcome == ClauseOutcome.NotApplicable)
            {
                continue;
            }

            var pair = new ClauseResultPair { Id = engine.Clauses[i].Id, Legacy = l.Outcome, Candidate = c.Outcome, Detail = Detail(l, c) };
            clauses.Add(pair);
            if (engine.Clauses[i].Definition.IsAccepted)
            {
                accepted.Add(pair);
            }
        }

        var (@class, reasons) = Classify(Missing(legacy, candidate), accepted, differences, normalized.Count == 0);
        var shown = legacy ?? candidate!;
        return new ExchangeVerdict
        {
            Exchange = id,
            Scenario = shown.Scenario,
            Request = new RequestLine
            {
                Method = shown.Request.Method,
                Path = shown.Request.Path,
                Query = string.IsNullOrEmpty(shown.Request.Query) ? null : shown.Request.Query,
            },
            Class = @class,
            Reasons = reasons,
            Diffs = differences,
            Clauses = clauses,
        };
    }

    private static ClauseEvaluation Evaluation(ContractReport report, string exchange, Side side, int clause) =>
        report.Outcomes.TryGetValue((exchange, side), out var list) ? list[clause] : ClauseEvaluation.NotApplicable;

    /// <summary>A side with no answer to compare: never replayed, or replayed without a response.</summary>
    private static List<string> Missing(ExchangeResult? legacy, ExchangeResult? candidate)
    {
        var missing = new List<string>();
        foreach (var (side, result) in new[] { ("legacy", legacy), ("candidate", candidate) })
        {
            if (result is null)
            {
                missing.Add($"{side} (not replayed)");
            }
            else if (result.Response is null)
            {
                missing.Add($"{side} ({result.Error?.Kind ?? "no answer"})");
            }
        }

        return missing;
    }
}
