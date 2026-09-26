using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Paths;

namespace SecondKey.Contract.Rendering;

/// <summary>
/// Renders a clause in plain English for the people who sign off a change — risk, change
/// approval, audit — who do not read YAML. Deterministic: the same clause always renders
/// the same sentence, so the sentence can sit in signed evidence.
/// </summary>
public static partial class ClauseText
{
    public static string Render(ClauseDefinition clause)
    {
        ArgumentNullException.ThrowIfNull(clause);
        var scope = Scope(clause.When);
        var predicates = clause.Assert.Select(Predicate).ToList();
        return clause.Kind == ClauseKind.Must
            ? $"{scope}: {string.Join(", and ", predicates)}."
            : $"{scope}: it never happens that {string.Join(" and ", predicates)}.";
    }

    public static string Scope(RequestMatch? when)
    {
        if (when is null)
        {
            return "For every request";
        }

        var methods = when.Method is { Count: > 0 } m ? string.Join(" or ", m) : null;
        var path = when.Path is { } p ? LiteralPath(p) ?? $"paths matching {p}" : null;
        var scope = (methods, path) switch
        {
            (not null, not null) when path.StartsWith('/') => $"For {methods} {path}",
            (not null, not null) => $"For {methods} requests to {path}",
            (not null, null) => $"For every {methods} request",
            (null, not null) when path.StartsWith('/') => $"For requests to {path}",
            (null, not null) => $"For requests to {path}",
            _ => "For every request",
        };

        foreach (var (name, pattern) in when.Query ?? new Dictionary<string, string>())
        {
            scope += $" with query {name} matching {pattern}";
        }

        return scope;
    }

    public static string Predicate(PredicateDefinition predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var multi = SelectorPath.Parse(predicate.Select).IsMultiValued;
        var subject = (predicate.Quantifier ?? Quantifier.All) switch
        {
            Quantifier.Any => $"some {predicate.Select}",
            Quantifier.None => $"no {predicate.Select}",
            _ when multi && predicate.Op is not (PredicateOp.Exists or PredicateOp.Absent or PredicateOp.CountEquals or PredicateOp.CountAtLeast or PredicateOp.CountAtMost) => $"every {predicate.Select}",
            _ => predicate.Select,
        };
        var operand = predicate.Ref is { } reference ? $"the value of {reference}" : Value(predicate.Value);
        var caseNote = predicate.IgnoreCase == true ? " (ignoring case)" : string.Empty;

        return predicate.Op switch
        {
            PredicateOp.Exists => $"{subject} is present",
            PredicateOp.Absent => $"{subject} is absent",
            PredicateOp.EqualsOp => $"{subject} equals {operand}{caseNote}",
            PredicateOp.NotEquals => $"{subject} does not equal {operand}{caseNote}",
            PredicateOp.Lt => $"{subject} is less than {operand}",
            PredicateOp.Lte => $"{subject} is at most {operand}",
            PredicateOp.Gt => $"{subject} is greater than {operand}",
            PredicateOp.Gte => $"{subject} is at least {operand}",
            PredicateOp.Between => string.Create(CultureInfo.InvariantCulture, $"{subject} is between {predicate.Min} and {predicate.Max}"),
            PredicateOp.Approx when predicate.Absolute is { } absolute => string.Create(CultureInfo.InvariantCulture, $"{subject} is within {absolute} of {operand}"),
            PredicateOp.Approx => string.Create(CultureInfo.InvariantCulture, $"{subject} is within {predicate.Relative * 100}% of {operand}"),
            PredicateOp.Matches => $"{subject} matches {operand}{caseNote}",
            PredicateOp.NotMatches => $"{subject} does not match {operand}{caseNote}",
            PredicateOp.Contains => $"{subject} contains {operand}{caseNote}",
            PredicateOp.NotContains => $"{subject} does not contain {operand}{caseNote}",
            PredicateOp.In => $"{subject} is one of {operand}{caseNote}",
            PredicateOp.NotIn => $"{subject} is none of {operand}{caseNote}",
            PredicateOp.CountEquals => $"{subject} has exactly {operand} value(s)",
            PredicateOp.CountAtLeast => $"{subject} has at least {operand} value(s)",
            PredicateOp.CountAtMost => $"{subject} has at most {operand} value(s)",
            _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate.Op, "unknown operator"),
        };
    }

    private static string Value(JsonNode? value) => value switch
    {
        null => "null",
        JsonArray array => string.Join(", ", array.Select(Value)),
        _ => value.ToJsonString(),
    };

    /// <summary>The literal path an anchored regex like <c>^/checkout$</c> denotes, or null when it is a real pattern.</summary>
    private static string? LiteralPath(string pattern)
    {
        var match = AnchoredLiteral().Match(pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^\^(/[A-Za-z0-9/_~-]*)\$$", RegexOptions.CultureInvariant)]
    private static partial Regex AnchoredLiteral();
}
