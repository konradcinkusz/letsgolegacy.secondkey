using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Paths;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Contract.Predicates;

namespace SecondKey.Compare;

/// <summary>
/// The contract's normalization rules, compiled (S12): the differences between the two sides
/// that do not matter. They are applied to a copy of each side's canonical document in a
/// fixed order — ignore, then masks, then sets — and tolerances apply when two numbers are
/// compared. Every raw difference a rule accounts for is labelled with that rule, so the
/// evidence says why an exchange counts as equal, not only that it does.
/// </summary>
/// <remarks>
/// Labels name the rule as the contract writes it: <c>ignore: &lt;path&gt;</c>,
/// <c>tolerances: &lt;path&gt;</c>, <c>sets: &lt;path&gt;</c>, <c>masks.timestamps</c>,
/// <c>masks.guids</c> and <c>masks.patterns.&lt;name&gt;</c>.
/// </remarks>
public sealed partial class Normalizer
{
    public const string TimestampMask = "<timestamp>";
    public const string GuidMask = "<guid>";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly IReadOnlyList<SelectorPath> _ignore;
    private readonly IReadOnlyList<Tolerance> _tolerances;
    private readonly IReadOnlyList<SetRuleCompiled> _sets;
    private readonly IReadOnlyList<MaskRule> _masks;

    public Normalizer(NormalizeSettings? settings)
    {
        _ignore = (settings?.Ignore ?? []).Select(SelectorPath.Parse).ToList();
        _tolerances = (settings?.Tolerances ?? []).Select(t => new Tolerance(SelectorPath.Parse(t.Path), t.Absolute, t.Relative)).ToList();

        // Innermost arrays first, so an outer set is ordered by elements whose own sets are already in order.
        _sets = (settings?.Sets ?? [])
            .Select(s => new SetRuleCompiled(SelectorPath.Parse(s.Path), s.Key is null ? null : SelectorPath.ParseRelative(s.Key)))
            .OrderByDescending<SetRuleCompiled, int>(s => s.Path.Segments.Count)
            .ToList();

        var masks = new List<MaskRule>();
        if (settings?.Masks?.Timestamps == true)
        {
            masks.Add(new MaskRule("masks.timestamps", Timestamp(), TimestampMask));
        }

        if (settings?.Masks?.Guids == true)
        {
            masks.Add(new MaskRule("masks.guids", Guid(), GuidMask));
        }

        foreach (var pattern in settings?.Masks?.Patterns ?? [])
        {
            masks.Add(new MaskRule($"masks.patterns.{pattern.Name}", new Regex(pattern.Regex, RegexOptions.CultureInvariant, RegexTimeout), pattern.Replacement));
        }

        _masks = masks;
    }

    /// <summary>No rules: nothing is normalized and nothing is explained.</summary>
    public static Normalizer None { get; } = new(null);

    /// <summary>A normalized copy of <paramref name="document"/>; the argument is not changed.</summary>
    public JsonObject Apply(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var copy = (JsonObject)document.DeepClone();
        RemoveIgnored(copy, []);
        var masked = (JsonObject)Strings.Map(copy, MaskText)!;
        foreach (var set in _sets)
        {
            foreach (var array in set.Path.Select(masked).OfType<JsonArray>())
            {
                Order(array, set.Key);
            }
        }

        return masked;
    }

    /// <summary>Whether two numbers at <paramref name="path"/> count as equal: identical, or within a tolerance that covers the path.</summary>
    public bool NumbersEqual(IReadOnlyList<PathSegment> path, decimal legacy, decimal candidate) =>
        legacy == candidate || _tolerances.Any(t => t.Path.Matches(path) && t.Allows(legacy, candidate));

    /// <summary>The string with every mask applied, in order.</summary>
    public string MaskText(string value) => _masks.Aggregate(value, (text, mask) => mask.Apply(text));

    /// <summary>
    /// The rule that accounts for one raw difference, or null when none does.
    /// <paramref name="normalized"/> is what still differs after normalization: a set rule
    /// accounts for a difference inside its array only when nothing in that array still differs.
    /// </summary>
    public string? Explain(Change change, IReadOnlyList<Change> normalized)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(normalized);
        var path = change.Path;
        var prefixes = Enumerable.Range(1, path.Count).Reverse().Select(length => path.Take(length).ToList()).ToList();

        if (_ignore.FirstOrDefault(rule => prefixes.Any(rule.Matches)) is { } ignored)
        {
            return $"ignore: {ignored.Text}";
        }

        var difference = change.Difference;
        if (difference.Kind == DifferenceKind.Changed)
        {
            if (JsonValues.TryGetNumber(difference.Legacy, out var x) && JsonValues.TryGetNumber(difference.Candidate, out var y)
                && _tolerances.FirstOrDefault(t => t.Path.Matches(path) && t.Allows(x, y)) is { } tolerance)
            {
                return $"tolerances: {tolerance.Path.Text}";
            }

            if (AsString(difference.Legacy) is { } a && AsString(difference.Candidate) is { } b && MasksThatEqualize(a, b) is { } masks)
            {
                return masks;
            }
        }

        foreach (var prefix in prefixes)
        {
            if (_sets.FirstOrDefault(s => s.Path.Matches(prefix)) is { } set && !normalized.Any(n => n.IsAtOrBelow(prefix)))
            {
                return $"sets: {set.Path.Text}";
            }
        }

        return null;
    }

    private string? MasksThatEqualize(string legacy, string candidate)
    {
        var fired = new List<string>();
        foreach (var mask in _masks)
        {
            var l = mask.Apply(legacy);
            var c = mask.Apply(candidate);
            if (!string.Equals(l, legacy, StringComparison.Ordinal) || !string.Equals(c, candidate, StringComparison.Ordinal))
            {
                fired.Add(mask.Name);
            }

            legacy = l;
            candidate = c;
        }

        return fired.Count > 0 && string.Equals(legacy, candidate, StringComparison.Ordinal) ? string.Join(", ", fired) : null;
    }

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private void RemoveIgnored(JsonNode? node, List<PathSegment> path)
    {
        if (_ignore.Count == 0)
        {
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    path.Add(new PropertySegment(name));
                    if (_ignore.Any(rule => rule.Matches(path)))
                    {
                        obj.Remove(name);
                    }
                    else
                    {
                        RemoveIgnored(obj[name], path);
                    }

                    path.RemoveAt(path.Count - 1);
                }

                break;
            case JsonArray array:
                // From the end, so removing an element does not move the ones still to be visited.
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    path.Add(new IndexSegment(i));
                    if (_ignore.Any(rule => rule.Matches(path)))
                    {
                        array.RemoveAt(i);
                    }
                    else
                    {
                        RemoveIgnored(array[i], path);
                    }

                    path.RemoveAt(path.Count - 1);
                }

                break;
        }
    }

    private static void Order(JsonArray array, SelectorPath? key)
    {
        var items = array.ToList();
        array.Clear();
        var ordered = key is null
            ? items.OrderBy(CanonicalJson.ToText, StringComparer.Ordinal)
            : items
                .OrderBy(item => string.Join('\u001f', key.Select(item).Select(CanonicalJson.ToText)), StringComparer.Ordinal)
                .ThenBy(CanonicalJson.ToText, StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            array.Add(item);
        }
    }

    /// <summary>An ISO-8601 date-time: a date, <c>T</c> or a space, hours and minutes, optional seconds, fraction and offset.</summary>
    [GeneratedRegex("[0-9]{4}-[0-9]{2}-[0-9]{2}[Tt ][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:[.,][0-9]+)?)?(?:[Zz]|[+-][0-9]{2}:?[0-9]{2})?", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Timestamp();

    [GeneratedRegex("(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Guid();

    private sealed record Tolerance(SelectorPath Path, decimal? Absolute, decimal? Relative)
    {
        public bool Allows(decimal legacy, decimal candidate)
        {
            decimal difference;
            try
            {
                difference = Math.Abs(legacy - candidate);
            }
            catch (OverflowException)
            {
                return false;
            }

            return Absolute is { } absolute
                ? difference <= absolute
                : Relative is { } relative && difference <= relative * Math.Max(Math.Abs(legacy), Math.Abs(candidate));
        }
    }

    private sealed record SetRuleCompiled(SelectorPath Path, SelectorPath? Key);

    private sealed record MaskRule(string Name, Regex Pattern, string Replacement)
    {
        public string Apply(string text) => Pattern.Replace(text, Replacement);
    }
}
