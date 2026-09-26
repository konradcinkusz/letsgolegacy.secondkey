using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Paths;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;
using SecondKey.Artifacts.Yaml;

namespace SecondKey.Artifacts.Contracts;

/// <summary>A contract that passed validation, with the digest the verdict will cite.</summary>
public sealed record LoadedContract(ContractDocument Document, string Sha256, string? Path);

/// <summary>
/// Loads contract.yaml: YAML → JSON, the JSON Schema, then the rules a schema cannot
/// express (<see cref="ContractRules"/>). A contract that fails any of them is refused whole
/// — a verdict computed against three quarters of a contract is not a verdict.
/// </summary>
public static class ContractFile
{
    public static ValidationReport Validate(string path) => Parse(File.ReadAllText(path), path).Report;

    public static ValidationReport ValidateText(string yaml, string name) => Parse(yaml, name).Report;

    public static LoadedContract Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var (report, document) = Parse(Encoding.UTF8.GetString(bytes), path);
        return report.IsValid
            ? new LoadedContract(document!, Sha256Digest.OfBytes(bytes), path)
            : throw new ArtifactValidationException(report);
    }

    public static LoadedContract LoadText(string yaml, string name)
    {
        var (report, document) = Parse(yaml, name);
        return report.IsValid
            ? new LoadedContract(document!, Sha256Digest.OfBytes(Encoding.UTF8.GetBytes(yaml)), null)
            : throw new ArtifactValidationException(report);
    }

    private static (ValidationReport Report, ContractDocument? Document) Parse(string yaml, string name)
    {
        JsonNode? node;
        try
        {
            node = YamlJson.Convert(yaml);
        }
        catch (YamlConversionException ex)
        {
            var line = ex.Line > 0 ? (int?)ex.Line : null;
            return (new ValidationReport(name, [new ArtifactError(line, "", $"not valid YAML: {ex.Message}")]), null);
        }

        if (node is not JsonObject)
        {
            return (new ValidationReport(name, [new ArtifactError(null, "", "a contract is a YAML mapping")]), null);
        }

        using var json = JsonDocument.Parse(node.ToJsonString());
        var schemaErrors = ArtifactSchemas.ValidateDocument(ArtifactKind.Contract, json.RootElement);
        if (schemaErrors.Count > 0)
        {
            return (new ValidationReport(name, schemaErrors), null);
        }

        var document = json.RootElement.Deserialize<ContractDocument>(ArtifactJson.Document)
            ?? throw new InvalidOperationException("a schema-valid contract deserialized to null");
        var ruleErrors = ContractRules.Check(document);
        return (new ValidationReport(name, ruleErrors), ruleErrors.Count == 0 ? document : null);
    }
}

/// <summary>
/// The contract rules beyond the schema, ported from agent-eval-bench's validator
/// (docs/reference-notes.md §1): references resolve, regexes compile, paths parse, and a
/// contract states at least one absence — an accepted <c>never</c> clause.
/// </summary>
public static class ContractRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    public static IReadOnlyList<ArtifactError> Check(ContractDocument contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var errors = new List<ArtifactError>();
        void Error(string location, string message) => errors.Add(new ArtifactError(null, location, message));

        var extractors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (extractor, i) in (contract.Extract ?? []).Select((e, i) => (e, i)))
        {
            var at = $"/extract/{i}";
            if (!extractors.Add(extractor.Name))
            {
                Error($"{at}/name", $"extractor '{extractor.Name}' is defined more than once");
            }

            CheckMatch(extractor.When, $"{at}/when", Error);
            if (extractor.From == ExtractSource.Text)
            {
                CheckRegex(extractor.Selector, $"{at}/selector", Error, requireGroup: true);
            }

            if (extractor.Regex is { } regex)
            {
                CheckRegex(regex, $"{at}/regex", Error, requireGroup: true);
            }

            if (extractor.Culture is { } culture)
            {
                CheckCulture(culture, $"{at}/culture", Error);
            }
        }

        var normalize = contract.Normalize;
        foreach (var (path, i) in (normalize?.Ignore ?? []).Select((p, i) => (p, i)))
        {
            CheckPath(path, $"/normalize/ignore/{i}", extractors, Error);
        }

        foreach (var (rule, i) in (normalize?.Tolerances ?? []).Select((r, i) => (r, i)))
        {
            CheckPath(rule.Path, $"/normalize/tolerances/{i}/path", extractors, Error);
        }

        foreach (var (rule, i) in (normalize?.Sets ?? []).Select((r, i) => (r, i)))
        {
            CheckPath(rule.Path, $"/normalize/sets/{i}/path", extractors, Error);
            if (rule.Key is { } key)
            {
                try
                {
                    _ = SelectorPath.ParseRelative(key);
                }
                catch (PathSyntaxException ex)
                {
                    Error($"/normalize/sets/{i}/key", ex.Message);
                }
            }
        }

        foreach (var (pattern, i) in (normalize?.Masks?.Patterns ?? []).Select((p, i) => (p, i)))
        {
            CheckRegex(pattern.Regex, $"/normalize/masks/patterns/{i}/regex", Error, requireGroup: false);
        }

        var clauseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (clause, c) in contract.Clauses.Select((cl, c) => (cl, c)))
        {
            var at = $"/clauses/{c}";
            if (!clauseIds.Add(clause.Id))
            {
                Error($"{at}/id", $"clause '{clause.Id}' is defined more than once");
            }

            CheckMatch(clause.When, $"{at}/when", Error);
            foreach (var (predicate, p) in clause.Assert.Select((pr, p) => (pr, p)))
            {
                var pat = $"{at}/assert/{p}";
                CheckPath(predicate.Select, $"{pat}/select", extractors, Error);
                if (predicate.Ref is { } reference)
                {
                    CheckPath(reference, $"{pat}/ref", extractors, Error);
                }

                if (predicate.Op is PredicateOp.Matches or PredicateOp.NotMatches && predicate.Value?.GetValueKind() == JsonValueKind.String)
                {
                    CheckRegex(predicate.Value.GetValue<string>(), $"{pat}/value", Error, requireGroup: false);
                }

                if (predicate.Op == PredicateOp.Between && predicate.Min > predicate.Max)
                {
                    Error(pat, string.Create(CultureInfo.InvariantCulture, $"between: min {predicate.Min} is greater than max {predicate.Max}"));
                }
            }
        }

        if (!contract.Clauses.Any(cl => cl.IsAccepted && cl.Kind == ClauseKind.Never))
        {
            Error("/clauses", "the contract has no accepted 'never' clause: a contract must state at least one thing that must never happen");
        }

        return errors;
    }

    private static void CheckMatch(RequestMatch? match, string at, Action<string, string> error)
    {
        if (match is null)
        {
            return;
        }

        if (match.Path is { } path)
        {
            CheckRegex(path, $"{at}/path", error, requireGroup: false);
        }

        foreach (var (name, pattern) in match.Query ?? new Dictionary<string, string>())
        {
            CheckRegex(pattern, $"{at}/query/{name}", error, requireGroup: false);
        }
    }

    private static void CheckPath(string text, string at, HashSet<string> extractors, Action<string, string> error)
    {
        if (!SelectorPath.TryParse(text, out var path, out var message))
        {
            error(at, message!);
            return;
        }

        if (path!.Root == "extract")
        {
            if (path.Segments.Count < 2 || path.Segments[1] is not PropertySegment named)
            {
                error(at, "an extract path names the extractor, e.g. extract.orderTotal");
            }
            else if (!extractors.Contains(named.Name))
            {
                error(at, $"'{text}' refers to extractor '{named.Name}', which is not defined under extract:");
            }
        }
    }

    private static void CheckRegex(string pattern, string at, Action<string, string> error, bool requireGroup)
    {
        try
        {
            var regex = new Regex(pattern, RegexOptions.CultureInvariant, RegexTimeout);
            if (requireGroup && regex.GetGroupNumbers().Length < 2)
            {
                error(at, $"'{pattern}' needs a capture group: its first group is the extracted value");
            }
        }
        catch (ArgumentException ex)
        {
            error(at, $"not a valid regular expression: {ex.Message}");
        }
    }

    private static void CheckCulture(string name, string at, Action<string, string> error)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
        }
        catch (CultureNotFoundException)
        {
            error(at, $"'{name}' is not a culture this runtime knows");
        }
    }
}
