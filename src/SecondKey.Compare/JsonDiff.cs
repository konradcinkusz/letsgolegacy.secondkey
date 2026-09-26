using System.Text.Json.Nodes;
using SecondKey.Artifacts.Paths;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Contract.Predicates;

namespace SecondKey.Compare;

/// <summary>One difference, with its location as segments as well as text.</summary>
public sealed record Change(IReadOnlyList<PathSegment> Path, Difference Difference)
{
    /// <summary>True when this change is at <paramref name="prefix"/> or anywhere below it.</summary>
    public bool IsAtOrBelow(IReadOnlyList<PathSegment> prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return Path.Count >= prefix.Count && Path.Take(prefix.Count).SequenceEqual(prefix);
    }
}

/// <summary>
/// Field-by-field differences between two canonical documents, each located by a concrete
/// path in the contract's path language, so a reviewer can turn any difference into an
/// ignore rule or a clause without translating anything.
/// </summary>
public static class JsonDiff
{
    /// <param name="legacy">The legacy side's document.</param>
    /// <param name="candidate">The candidate's document.</param>
    /// <param name="equalNumbers">Decides whether two numbers at a location count as equal (tolerances); plain numeric equality when null.</param>
    public static IReadOnlyList<Change> Compare(JsonNode? legacy, JsonNode? candidate, Func<IReadOnlyList<PathSegment>, decimal, decimal, bool>? equalNumbers = null)
    {
        var changes = new List<Change>();
        Walk(legacy, candidate, [], changes, equalNumbers);
        return changes;
    }

    private static void Walk(JsonNode? legacy, JsonNode? candidate, List<PathSegment> path, List<Change> changes, Func<IReadOnlyList<PathSegment>, decimal, decimal, bool>? equalNumbers)
    {
        switch (legacy, candidate)
        {
            case (JsonObject l, JsonObject c):
                foreach (var name in l.Select(p => p.Key).Union(c.Select(p => p.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    var inLegacy = l.TryGetPropertyValue(name, out var lv);
                    var inCandidate = c.TryGetPropertyValue(name, out var cv);
                    Child(inLegacy, lv, inCandidate, cv, [.. path, new PropertySegment(name)], changes, equalNumbers);
                }

                break;
            case (JsonArray l, JsonArray c):
                for (var i = 0; i < Math.Max(l.Count, c.Count); i++)
                {
                    var inLegacy = i < l.Count;
                    var inCandidate = i < c.Count;
                    Child(inLegacy, inLegacy ? l[i] : null, inCandidate, inCandidate ? c[i] : null, [.. path, new IndexSegment(i)], changes, equalNumbers);
                }

                break;
            default:
                if (equalNumbers is not null && JsonValues.TryGetNumber(legacy, out var x) && JsonValues.TryGetNumber(candidate, out var y) && equalNumbers(path, x, y))
                {
                    return;
                }

                if (!JsonValues.Equal(legacy, candidate))
                {
                    changes.Add(new Change(path, new Difference
                    {
                        Path = SelectorPath.Format(path),
                        Kind = DifferenceKind.Changed,
                        Legacy = legacy?.DeepClone(),
                        Candidate = candidate?.DeepClone(),
                    }));
                }

                break;
        }
    }

    private static void Child(bool inLegacy, JsonNode? legacy, bool inCandidate, JsonNode? candidate, List<PathSegment> path, List<Change> changes, Func<IReadOnlyList<PathSegment>, decimal, decimal, bool>? equalNumbers)
    {
        if (inLegacy && inCandidate)
        {
            Walk(legacy, candidate, path, changes, equalNumbers);
            return;
        }

        changes.Add(new Change(path, inLegacy
            ? new Difference { Path = SelectorPath.Format(path), Kind = DifferenceKind.Removed, Legacy = legacy?.DeepClone() }
            : new Difference { Path = SelectorPath.Format(path), Kind = DifferenceKind.Added, Candidate = candidate?.DeepClone() }));
    }
}
