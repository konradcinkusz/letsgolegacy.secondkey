using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Contract.Extraction;
using SecondKey.Contract.Observations;
using SecondKey.Contract.Predicates;
using SecondKey.Contract.Rendering;

namespace SecondKey.Contract;

/// <summary>The outcome of one clause on one observation, with what was found.</summary>
public sealed record ClauseEvaluation(ClauseOutcome Outcome, string? Detail)
{
    public static ClauseEvaluation NotApplicable { get; } = new(ClauseOutcome.NotApplicable, null);
}

/// <summary>A clause, compiled: its scope, its predicates and its natural-language text.</summary>
public sealed class CompiledClause
{
    internal CompiledClause(ClauseDefinition definition)
    {
        Definition = definition;
        When = new RequestMatcher(definition.When);
        Predicates = definition.Assert.Select(p => new CompiledPredicate(p)).ToList();
        Text = ClauseText.Render(definition);
    }

    public ClauseDefinition Definition { get; }

    public string Id => Definition.Id;

    public RequestMatcher When { get; }

    public IReadOnlyList<CompiledPredicate> Predicates { get; }

    public string Text { get; }

    /// <summary>
    /// <c>must</c>: every predicate holds. <c>never</c>: the predicates never all hold together.
    /// A predicate that could not be evaluated makes the clause an error, which counts against
    /// the side it happened on — an unreadable value is never a pass.
    /// </summary>
    public ClauseEvaluation Evaluate(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!When.Matches(observation.Method, observation.Path, observation.Query))
        {
            return ClauseEvaluation.NotApplicable;
        }

        var results = Predicates.Select(p => p.Evaluate(observation)).ToList();
        if (results.FirstOrDefault(r => r.State == PredicateState.Error) is { } error)
        {
            return new(ClauseOutcome.Error, error.Detail);
        }

        var allHold = results.All(r => r.State == PredicateState.Holds);
        if (Definition.Kind == ClauseKind.Must)
        {
            return allHold
                ? new(ClauseOutcome.Pass, null)
                : new(ClauseOutcome.Fail, results.First(r => r.State != PredicateState.Holds).Detail);
        }

        return allHold
            ? new(ClauseOutcome.Fail, string.Join("; ", results.Select(r => r.Detail)))
            : new(ClauseOutcome.Pass, null);
    }
}

/// <summary>Per-clause outcomes of one run: both sides, every exchange.</summary>
public sealed class ContractReport
{
    internal ContractReport(
        IReadOnlyList<ClauseSummary> clauses,
        IReadOnlyDictionary<(string Exchange, Side Side), IReadOnlyList<ClauseEvaluation>> outcomes,
        IReadOnlyDictionary<(string Exchange, Side Side), Observation> observations)
    {
        Clauses = clauses;
        Outcomes = outcomes;
        Observations = observations;
    }

    /// <summary>One summary per clause, in contract order.</summary>
    public IReadOnlyList<ClauseSummary> Clauses { get; }

    /// <summary>For each exchange and side, one evaluation per clause in contract order.</summary>
    public IReadOnlyDictionary<(string Exchange, Side Side), IReadOnlyList<ClauseEvaluation>> Outcomes { get; }

    /// <summary>
    /// The observation each outcome was evaluated on, so that the comparator compares exactly
    /// what the clauses saw — the same extracted values, not a second extraction.
    /// </summary>
    public IReadOnlyDictionary<(string Exchange, Side Side), Observation> Observations { get; }
}

/// <summary>
/// C3: evaluates a contract against what each side answered. Pure and deterministic — the
/// same contract and run always give the same report (design rule 1).
/// </summary>
public sealed class ContractEngine
{
    private ContractEngine(ContractDocument contract)
    {
        Contract = contract;
        Observations = new ObservationBuilder(new ExtractorSet(contract.Extract));
        Clauses = contract.Clauses.Select(c => new CompiledClause(c)).ToList();
    }

    public ContractDocument Contract { get; }

    public ObservationBuilder Observations { get; }

    public IReadOnlyList<CompiledClause> Clauses { get; }

    public static ContractEngine Compile(ContractDocument contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new ContractEngine(contract);
    }

    public IReadOnlyList<ClauseEvaluation> Evaluate(Observation observation) =>
        Clauses.Select(c => c.Evaluate(observation)).ToList();

    public ContractReport EvaluateRun(RunDocument run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var outcomes = new Dictionary<(string, Side), IReadOnlyList<ClauseEvaluation>>();
        var observations = new Dictionary<(string, Side), Observation>();
        foreach (var result in run.Results)
        {
            var observation = Observations.Build(result);
            observations[(result.Exchange, result.Side)] = observation;
            outcomes[(result.Exchange, result.Side)] = Evaluate(observation);
        }

        var exchanges = run.Results.Select(r => r.Exchange).Distinct(StringComparer.Ordinal).ToList();
        var summaries = Clauses.Select((clause, index) => Summarize(clause, index, exchanges, outcomes)).ToList();
        return new ContractReport(summaries, outcomes, observations);
    }

    private static ClauseSummary Summarize(
        CompiledClause clause,
        int index,
        IReadOnlyList<string> exchanges,
        IReadOnlyDictionary<(string, Side), IReadOnlyList<ClauseEvaluation>> outcomes)
    {
        ClauseOutcome Outcome(string exchange, Side side) =>
            outcomes.TryGetValue((exchange, side), out var list) ? list[index].Outcome : ClauseOutcome.NotApplicable;

        var legacy = new int[3];
        var candidate = new int[3];
        int exercised = 0, regressed = 0;
        foreach (var exchange in exchanges)
        {
            var l = Outcome(exchange, Side.Legacy);
            var c = Outcome(exchange, Side.Candidate);
            if (l == ClauseOutcome.NotApplicable && c == ClauseOutcome.NotApplicable)
            {
                continue;
            }

            exercised++;
            Count(legacy, l);
            Count(candidate, c);
            if (l == ClauseOutcome.Pass && c is ClauseOutcome.Fail or ClauseOutcome.Error)
            {
                regressed++;
            }
        }

        var legacyBroken = legacy[1] + legacy[2] > 0;
        var candidateBroken = candidate[1] + candidate[2] > 0;
        var status = (exercised, regressed, legacyBroken, candidateBroken) switch
        {
            (0, _, _, _) => ClauseStatus.Unexercised,
            (_, > 0, _, _) => ClauseStatus.Regressed,
            (_, _, true, true) => ClauseStatus.ViolatedBoth,
            (_, _, false, true) => ClauseStatus.Regressed,
            (_, _, true, false) => ClauseStatus.Fixed,
            _ => ClauseStatus.Held,
        };

        return new ClauseSummary
        {
            Id = clause.Id,
            Kind = clause.Definition.Kind == ClauseKind.Must ? "must" : "never",
            Title = clause.Definition.Title,
            Text = clause.Text,
            Accepted = clause.Definition.IsAccepted,
            Exercised = exercised,
            Legacy = new Tally { Pass = legacy[0], Fail = legacy[1], Error = legacy[2] },
            Candidate = new Tally { Pass = candidate[0], Fail = candidate[1], Error = candidate[2] },
            Status = status,
        };
    }

    private static void Count(int[] tally, ClauseOutcome outcome)
    {
        switch (outcome)
        {
            case ClauseOutcome.Pass:
                tally[0]++;
                break;
            case ClauseOutcome.Fail:
                tally[1]++;
                break;
            case ClauseOutcome.Error:
                tally[2]++;
                break;
        }
    }
}
