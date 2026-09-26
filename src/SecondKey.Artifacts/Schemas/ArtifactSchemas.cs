using System.Collections.Frozen;
using System.Text.Json;
using Json.Schema;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Schemas;

/// <summary>The four artifacts whose formats this repository defines.</summary>
public enum ArtifactKind
{
    Capture,
    Contract,
    Run,
    Verdict,
}

/// <summary>
/// The JSON Schemas in <c>/schemas</c>, embedded and compiled once. Event artifacts
/// (capture, run) are validated line by line against the schema branch their <c>type</c>
/// names, so an error reads "seq is missing" rather than "matched none of four branches".
/// </summary>
public static class ArtifactSchemas
{
    public const string BaseUri = "https://github.com/konradcinkusz/letsgolegacy.secondkey/schemas/";

    private static readonly Lazy<Compiled> Instance = new(Compile);

    private static readonly EvaluationOptions Evaluation = new()
    {
        // Hierarchical, so errors are collected only from branches that actually failed:
        // a oneOf that passed does not report the alternatives it rejected.
        OutputFormat = OutputFormat.Hierarchical,
        RequireFormatValidation = true,
        IncludeApplicatorErrors = true,
    };

    /// <summary>
    /// Keywords whose failure message only says "something below me failed". The failure
    /// below is reported instead; these would only repeat it less precisely.
    /// </summary>
    private static readonly FrozenSet<string> SummaryKeywords = new[]
    {
        "properties", "patternProperties", "items", "prefixItems", "allOf", "$ref", "dependentSchemas",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The event types each JSON Lines artifact knows, and the schema definition for each.</summary>
    internal static FrozenDictionary<string, string> CaptureEventTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["capture.header"] = "header",
        ["http.exchange"] = "httpExchange",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    internal static FrozenDictionary<string, string> RunEventTypes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["run.header"] = "header",
        ["state.reset"] = "stateReset",
        ["exchange.result"] = "exchangeResult",
        ["run.end"] = "end",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The raw text of an embedded schema, e.g. for writing it next to an artifact.</summary>
    public static string Text(ArtifactKind kind) => ReadResource(FileName(kind));

    public static string FileName(ArtifactKind kind) => kind switch
    {
        ArtifactKind.Capture => "skcap.schema.json",
        ArtifactKind.Contract => "contract.schema.json",
        ArtifactKind.Run => "skrun.schema.json",
        ArtifactKind.Verdict => "verdict.schema.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown artifact kind."),
    };

    /// <summary>Validates a whole document (contract or verdict) against its schema.</summary>
    public static IReadOnlyList<ArtifactError> ValidateDocument(ArtifactKind kind, JsonElement instance)
    {
        if (kind is ArtifactKind.Capture or ArtifactKind.Run)
        {
            throw new ArgumentException($"{kind} is a JSON Lines artifact; validate it line by line.", nameof(kind));
        }

        return Collect(Instance.Value.Documents[kind].Evaluate(instance, Evaluation), line: null);
    }

    /// <summary>
    /// Validates one line of a JSON Lines artifact. An unknown or missing <c>type</c> is an
    /// error in its own right — never a line that is silently skipped.
    /// </summary>
    public static IReadOnlyList<ArtifactError> ValidateEvent(ArtifactKind kind, JsonElement instance, int line)
    {
        var (types, branches) = kind switch
        {
            ArtifactKind.Capture => (CaptureEventTypes, Instance.Value.CaptureBranches),
            ArtifactKind.Run => (RunEventTypes, Instance.Value.RunBranches),
            _ => throw new ArgumentException($"{kind} is a document artifact; validate it whole.", nameof(kind)),
        };

        if (instance.ValueKind != JsonValueKind.Object)
        {
            return [new ArtifactError(line, "", "an event must be a JSON object")];
        }

        if (!instance.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
        {
            return [new ArtifactError(line, "/type", "an event must carry a string 'type'")];
        }

        var typeName = type.GetString()!;
        if (!types.TryGetValue(typeName, out var branch))
        {
            return [new ArtifactError(line, "/type", $"unknown event type '{typeName}'; this version knows: {string.Join(", ", types.Keys.Order(StringComparer.Ordinal))}")];
        }

        return Collect(branches[branch].Evaluate(instance, Evaluation), line);
    }

    private static List<ArtifactError> Collect(EvaluationResults results, int? line)
    {
        var errors = new List<ArtifactError>();
        if (results.IsValid)
        {
            return errors;
        }

        Walk(results, errors, line);
        if (errors.Count == 0)
        {
            // A failure with no keyword message anywhere below it; never report "invalid" with nothing to show.
            errors.Add(new ArtifactError(line, results.InstanceLocation.ToString(), "does not match the schema"));
        }

        return errors.Distinct().ToList();
    }

    private static void Walk(EvaluationResults node, List<ArtifactError> errors, int? line)
    {
        if (node.IsValid)
        {
            return;
        }

        var location = node.InstanceLocation.ToString();
        foreach (var (keyword, message) in node.Errors ?? [])
        {
            if (SummaryKeywords.Contains(keyword))
            {
                continue;
            }

            errors.Add(keyword.Length == 0
                ? new ArtifactError(line, location, $"'{LastSegment(location)}' is not allowed here: the format does not define it")
                : new ArtifactError(line, location, $"{message} ({keyword})"));
        }

        foreach (var child in node.Details ?? [])
        {
            Walk(child, errors, line);
        }
    }

    private static string LastSegment(string pointer) =>
        pointer[(pointer.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static Compiled Compile()
    {
        var registry = new SchemaRegistry();
        var build = new BuildOptions { SchemaRegistry = registry };

        // The capture schema first: the run schema reuses its request, response and body definitions.
        var capture = Load(ArtifactKind.Capture, build);
        var run = Load(ArtifactKind.Run, build);
        var contract = Load(ArtifactKind.Contract, build);
        var verdict = Load(ArtifactKind.Verdict, build);

        return new Compiled(
            new Dictionary<ArtifactKind, JsonSchema>
            {
                [ArtifactKind.Capture] = capture,
                [ArtifactKind.Run] = run,
                [ArtifactKind.Contract] = contract,
                [ArtifactKind.Verdict] = verdict,
            }.ToFrozenDictionary(),
            Branches("skcap.schema.json", CaptureEventTypes.Values, build),
            Branches("skrun.schema.json", RunEventTypes.Values, build));
    }

    private static JsonSchema Load(ArtifactKind kind, BuildOptions build) =>
        JsonSchema.FromText(ReadResource(FileName(kind)), build, new Uri(BaseUri + FileName(kind)));

    private static FrozenDictionary<string, JsonSchema> Branches(string file, IEnumerable<string> definitions, BuildOptions build) =>
        definitions.Distinct(StringComparer.Ordinal).ToFrozenDictionary(
            definition => definition,
            definition => JsonSchema.FromText(
                $$"""{"$schema":"https://json-schema.org/draft/2020-12/schema","$ref":"{{BaseUri}}{{file}}#/$defs/{{definition}}"}""",
                build,
                new Uri($"{BaseUri}branches/{file}/{definition}")),
            StringComparer.Ordinal);

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(ArtifactSchemas).Assembly;
        using var stream = assembly.GetManifestResourceStream("schemas/" + fileName)
            ?? throw new InvalidOperationException($"Embedded schema '{fileName}' is missing from {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record Compiled(
        FrozenDictionary<ArtifactKind, JsonSchema> Documents,
        FrozenDictionary<string, JsonSchema> CaptureBranches,
        FrozenDictionary<string, JsonSchema> RunBranches);
}
