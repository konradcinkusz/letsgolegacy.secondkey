using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Paths;
using SecondKey.Contract.Observations;

namespace SecondKey.Contract.Predicates;

/// <summary>The result of one predicate on one observation.</summary>
public enum PredicateState
{
    Holds,
    DoesNotHold,
    Error,
}

public sealed record PredicateResult(PredicateState State, string Detail);

/// <summary>
/// One assertion of a clause, compiled. Three disciplines from agent-eval-bench
/// (reference-notes §1): no predicate reads prose; none holds vacuously — an <c>all</c>
/// over an empty selection does not hold; and an operator this engine does not know is an
/// error at compile time, never a pass.
/// </summary>
public sealed class CompiledPredicate
{
    private readonly Regex? _regex;

    public CompiledPredicate(PredicateDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Select = SelectorPath.Parse(definition.Select);
        Ref = definition.Ref is { } reference ? SelectorPath.Parse(reference) : null;
        if (definition.Op is PredicateOp.Matches or PredicateOp.NotMatches)
        {
            var options = RegexOptions.CultureInvariant | (definition.IgnoreCase == true ? RegexOptions.IgnoreCase : RegexOptions.None);
            _regex = new Regex(definition.Value!.GetValue<string>(), options, RequestMatcher.RegexTimeout);
        }

        if (!Enum.IsDefined(definition.Op))
        {
            throw new ArgumentException($"unknown operator {definition.Op}", nameof(definition));
        }
    }

    public PredicateDefinition Definition { get; }

    public SelectorPath Select { get; }

    public SelectorPath? Ref { get; }

    private Quantifier Quantifier => Definition.Quantifier ?? Quantifier.All;

    private bool IgnoreCase => Definition.IgnoreCase == true;

    public PredicateResult Evaluate(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (ExtractionError(observation, Select) is { } selectError)
        {
            return new(PredicateState.Error, selectError);
        }

        var selected = Select.Select(observation.Document);
        var present = selected.Where(v => v is not null).ToList();

        switch (Definition.Op)
        {
            case PredicateOp.Exists:
                return Result(present.Count > 0, $"{Select} {(present.Count > 0 ? "is present" : "is absent")}");
            case PredicateOp.Absent:
                return Result(present.Count == 0, $"{Select} {(present.Count == 0 ? "is absent" : "is present: " + JsonValues.Render(present[0]))}");
            case PredicateOp.CountEquals:
            case PredicateOp.CountAtLeast:
            case PredicateOp.CountAtMost:
                var expected = Definition.Value!.GetValue<long>();

                // A single-valued path that lands on an array counts the array's elements:
                // extract.lines (an "all" extractor) counts lines, not "one array".
                var count = !Select.IsMultiValued && present is [JsonArray array] ? array.Count : present.Count;
                var holds = Definition.Op switch
                {
                    PredicateOp.CountEquals => count == expected,
                    PredicateOp.CountAtLeast => count >= expected,
                    _ => count <= expected,
                };
                return Result(holds, string.Create(CultureInfo.InvariantCulture, $"{Select} has {count} value(s)"));
        }

        JsonNode? operand = Definition.Value;
        if (Ref is not null)
        {
            if (ExtractionError(observation, Ref) is { } refError)
            {
                return new(PredicateState.Error, refError);
            }

            var referenced = Ref.Select(observation.Document);
            if (referenced.Count != 1)
            {
                return new(PredicateState.Error, string.Create(CultureInfo.InvariantCulture, $"ref {Ref} selected {referenced.Count} values; a reference must select exactly one"));
            }

            operand = referenced[0];
        }

        var satisfied = selected.Select(v => Test(v, operand)).ToList();
        var anyHolds = satisfied.Any(s => s);
        var result = Quantifier switch
        {
            Quantifier.Any => anyHolds,
            Quantifier.None => !anyHolds,
            _ => satisfied.Count > 0 && satisfied.All(s => s),
        };

        var shown = selected.Count == 0 ? "no value" : string.Join(", ", selected.Take(3).Select(JsonValues.Render)) + (selected.Count > 3 ? ", ..." : string.Empty);
        return Result(result, $"{Select} = {shown}");
    }

    private bool Test(JsonNode? value, JsonNode? operand)
    {
        switch (Definition.Op)
        {
            case PredicateOp.EqualsOp:
                return JsonValues.Equal(value, operand, IgnoreCase);
            case PredicateOp.NotEquals:
                return !JsonValues.Equal(value, operand, IgnoreCase);
            case PredicateOp.Lt:
            case PredicateOp.Lte:
            case PredicateOp.Gt:
            case PredicateOp.Gte:
                if (!JsonValues.TryGetNumber(value, out var x) || !JsonValues.TryGetNumber(operand, out var y))
                {
                    return false;
                }

                return Definition.Op switch
                {
                    PredicateOp.Lt => x < y,
                    PredicateOp.Lte => x <= y,
                    PredicateOp.Gt => x > y,
                    _ => x >= y,
                };
            case PredicateOp.Between:
                return JsonValues.TryGetNumber(value, out var b) && b >= Definition.Min && b <= Definition.Max;
            case PredicateOp.Approx:
                if (!JsonValues.TryGetNumber(value, out var actual) || !JsonValues.TryGetNumber(operand, out var target))
                {
                    return false;
                }

                var tolerance = Definition.Absolute ?? Definition.Relative!.Value * Math.Abs(target);
                return Math.Abs(actual - target) <= tolerance;
            case PredicateOp.Matches:
                return JsonValues.AsText(value) is { } m && _regex!.IsMatch(m);
            case PredicateOp.NotMatches:
                return JsonValues.AsText(value) is not { } n || !_regex!.IsMatch(n);
            case PredicateOp.Contains:
                return Contains(value, operand);
            case PredicateOp.NotContains:
                return !Contains(value, operand);
            case PredicateOp.In:
                return Definition.Value is JsonArray options && options.Any(o => JsonValues.Equal(value, o, IgnoreCase));
            case PredicateOp.NotIn:
                return Definition.Value is JsonArray excluded && !excluded.Any(o => JsonValues.Equal(value, o, IgnoreCase));
            default:
                throw new InvalidOperationException($"operator {Definition.Op} has no value test");
        }
    }

    private bool Contains(JsonNode? value, JsonNode? operand)
    {
        if (value is JsonArray array)
        {
            return array.Any(item => JsonValues.Equal(item, operand, IgnoreCase));
        }

        return JsonValues.AsText(value) is { } text
            && JsonValues.AsText(operand) is { } part
            && text.Contains(part, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static string? ExtractionError(Observation observation, SelectorPath path) =>
        path.Root == "extract"
        && path.Segments.Count > 1
        && path.Segments[1] is PropertySegment named
        && observation.ExtractionErrors.TryGetValue(named.Name, out var error)
            ? error
            : null;

    private static PredicateResult Result(bool holds, string detail) =>
        new(holds ? PredicateState.Holds : PredicateState.DoesNotHold, detail);
}
