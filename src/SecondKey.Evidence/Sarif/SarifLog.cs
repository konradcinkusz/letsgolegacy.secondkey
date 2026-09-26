using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Evidence.Sarif;

/// <summary>One result of a static analysis run, reduced to what the gate decides on.</summary>
public sealed record SarifFinding
{
    public required string RuleId { get; init; }

    /// <summary><c>error</c>, <c>warning</c>, <c>note</c> or <c>none</c>.</summary>
    public required string Level { get; init; }

    public required string Message { get; init; }

    /// <summary><c>path:line</c>, the path as the log wrote it (usually relative to the repository root); null for a finding with no location.</summary>
    public string? Location { get; init; }

    /// <summary><c>new</c>, <c>unchanged</c>, <c>updated</c> or <c>absent</c> when the log was compared with a baseline.</summary>
    public string? BaselineState { get; init; }

    /// <summary>True when a suppression was accepted for it (in source, or in a suppressions file).</summary>
    public bool Suppressed { get; init; }

    /// <summary>
    /// The gate's rule: an error that is not suppressed and is not known from the baseline
    /// blocks. A warning never blocks; it is reported.
    /// </summary>
    public bool Blocks => Level == "error" && !Suppressed && BaselineState is not ("unchanged" or "absent");
}

/// <summary>A rule a tool declares — including the ones that found nothing, which is what was checked.</summary>
public sealed record SarifRule(string Id, string? Description, string? HelpUri, string DefaultLevel);

public sealed record SarifRun(string Tool, string? Version, IReadOnlyList<SarifRule> Rules, IReadOnlyList<SarifFinding> Findings);

/// <summary>
/// A SARIF 2.1.0 log, read leniently where the standard is lenient and strictly where the
/// gate's decision depends on it: a file that is not SARIF 2.1.0 is refused, never read as
/// "no findings".
/// </summary>
public sealed record SarifLog(string Name, IReadOnlyList<SarifRun> Runs)
{
    public IEnumerable<SarifFinding> Findings => Runs.SelectMany(r => r.Findings);

    public static SarifLog Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllText(path), Path.GetFileName(path));
    }

    public static SarifLog Parse(string json, string name)
    {
        ArgumentNullException.ThrowIfNull(json);
        var log = ParseJson(json, name) as JsonObject;
        if (log is null || Text(log["version"]) != "2.1.0")
        {
            throw Invalid(name, "/version", "not a SARIF 2.1.0 log");
        }

        var runs = log["runs"] as JsonArray ?? throw Invalid(name, "/runs", "a SARIF log has a runs array");
        return new SarifLog(name, runs.Select((run, i) => ReadRun(name, run as JsonObject, i)).ToList());
    }

    private static JsonNode? ParseJson(string json, string name)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Invalid(name, "", $"not valid JSON: {ex.Message}");
        }
    }

    private static SarifRun ReadRun(string name, JsonObject? run, int index)
    {
        var tool = Text(run?["tool"]?["driver"]?["name"]);
        if (string.IsNullOrEmpty(tool))
        {
            throw Invalid(name, string.Create(CultureInfo.InvariantCulture, $"/runs/{index}/tool/driver/name"), "every run names its tool");
        }

        var declared = new List<JsonNode?>();
        declared.AddRange(run!["tool"]!["driver"]!["rules"] as JsonArray ?? []);
        foreach (var extension in run["tool"]!["extensions"] as JsonArray ?? [])
        {
            declared.AddRange(extension?["rules"] as JsonArray ?? []);
        }

        var driverRules = (run["tool"]!["driver"]!["rules"] as JsonArray ?? []).ToList();
        var rules = declared
            .OfType<JsonObject>()
            .Where(r => Text(r["id"]) is not null)
            .Select(r => new SarifRule(
                Text(r["id"])!,
                Text(r["shortDescription"]?["text"]) ?? Text(r["name"]),
                Text(r["helpUri"]),
                Text(r["defaultConfiguration"]?["level"]) ?? "warning"))
            .GroupBy(r => r.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var byId = rules.ToDictionary(r => r.Id, StringComparer.Ordinal);

        var findings = new List<SarifFinding>();
        foreach (var result in (run["results"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var ruleId = Text(result["ruleId"])
                ?? Text(result["rule"]?["id"])
                ?? (Number(result["ruleIndex"]) is { } i && i >= 0 && i < driverRules.Count ? Text(driverRules[(int)i]?["id"]) : null)
                ?? "(no rule)";
            var kind = Text(result["kind"]) ?? "fail";
            var level = kind != "fail"
                ? "none"
                : Text(result["level"]) ?? (byId.TryGetValue(ruleId, out var rule) ? rule.DefaultLevel : "warning");
            findings.Add(new SarifFinding
            {
                RuleId = ruleId,
                Level = level,
                Message = Text(result["message"]?["text"]) ?? Text(result["message"]?["id"]) ?? "(no message)",
                Location = Location((result["locations"] as JsonArray)?.FirstOrDefault()?["physicalLocation"]),
                BaselineState = Text(result["baselineState"]),
                Suppressed = (result["suppressions"] as JsonArray ?? []).Any(s => Text(s?["status"]) is null or "accepted"),
            });
        }

        return new SarifRun(tool, Text(run["tool"]!["driver"]!["version"]) ?? Text(run["tool"]!["driver"]!["semanticVersion"]), rules, findings);
    }

    private static string? Location(JsonNode? physical)
    {
        if (Text(physical?["artifactLocation"]?["uri"]) is not { } uri)
        {
            return null;
        }

        var path = uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(new Uri(uri).AbsolutePath) : Uri.UnescapeDataString(uri);
        return Number(physical!["region"]?["startLine"]) is { } line ? string.Create(CultureInfo.InvariantCulture, $"{path}:{line}") : path;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static long? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out long n) ? n : null;

    private static ArtifactValidationException Invalid(string name, string location, string message) =>
        new(new ValidationReport(name, [new ArtifactError(null, location, message)]));
}
