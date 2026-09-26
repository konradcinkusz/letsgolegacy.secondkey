using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SecondKey.Artifacts.Contracts;

/// <summary>
/// contract.yaml: what the system must do, what it must never do, and which differences
/// between two versions do not matter. <c>schemas/contract.schema.json</c> is the authority.
/// </summary>
public sealed record ContractDocument
{
    public required string ApiVersion { get; init; }

    public required string Kind { get; init; }

    public required ContractMetadata Metadata { get; init; }

    public CompareSettings? Compare { get; init; }

    public IReadOnlyList<ExtractorDefinition>? Extract { get; init; }

    public NormalizeSettings? Normalize { get; init; }

    public required IReadOnlyList<ClauseDefinition> Clauses { get; init; }
}

public sealed record ContractMetadata
{
    public required string Name { get; init; }

    public string? Title { get; init; }

    public string? System { get; init; }

    public required int Revision { get; init; }

    public IReadOnlyList<string>? Owners { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<HtmlCompareMode>))]
public enum HtmlCompareMode
{
    /// <summary>An HTML body is compared only through the extracted values.</summary>
    [JsonStringEnumMemberName("extracts")]
    Extracts,

    [JsonStringEnumMemberName("text")]
    Text,
}

public sealed record CompareSettings
{
    public IReadOnlyList<string>? Headers { get; init; }

    public HtmlCompareMode? Html { get; init; }
}

/// <summary>Which requests a clause or an extractor applies to.</summary>
public sealed record RequestMatch
{
    /// <summary>One method or several (written as a string or a list).</summary>
    [JsonConverter(typeof(StringOrListConverter))]
    public IReadOnlyList<string>? Method { get; init; }

    /// <summary>A regular expression matched against the request path.</summary>
    public string? Path { get; init; }

    /// <summary>Query parameter → regular expression its value must match.</summary>
    public IReadOnlyDictionary<string, string>? Query { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractSource>))]
public enum ExtractSource
{
    [JsonStringEnumMemberName("html")]
    Html,

    [JsonStringEnumMemberName("json")]
    Json,

    [JsonStringEnumMemberName("header")]
    Header,

    [JsonStringEnumMemberName("text")]
    Text,
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractType>))]
public enum ExtractType
{
    [JsonStringEnumMemberName("string")]
    String,

    [JsonStringEnumMemberName("decimal")]
    Decimal,

    [JsonStringEnumMemberName("integer")]
    Integer,

    [JsonStringEnumMemberName("boolean")]
    Boolean,
}

/// <summary>A named value pulled out of a response, so no clause ever reads prose.</summary>
public sealed record ExtractorDefinition
{
    public required string Name { get; init; }

    public RequestMatch? When { get; init; }

    public required ExtractSource From { get; init; }

    public required string Selector { get; init; }

    public string? Attribute { get; init; }

    public string? Regex { get; init; }

    public ExtractType? As { get; init; }

    public string? Culture { get; init; }

    public bool? All { get; init; }
}

public sealed record NormalizeSettings
{
    public IReadOnlyList<string>? Ignore { get; init; }

    public IReadOnlyList<ToleranceRule>? Tolerances { get; init; }

    public IReadOnlyList<SetRule>? Sets { get; init; }

    public MaskSettings? Masks { get; init; }
}

public sealed record ToleranceRule
{
    public required string Path { get; init; }

    public decimal? Absolute { get; init; }

    public decimal? Relative { get; init; }
}

public sealed record SetRule
{
    public required string Path { get; init; }

    public string? Key { get; init; }
}

public sealed record MaskSettings
{
    public bool? Timestamps { get; init; }

    public bool? Guids { get; init; }

    public IReadOnlyList<MaskPattern>? Patterns { get; init; }
}

public sealed record MaskPattern
{
    public required string Name { get; init; }

    public required string Regex { get; init; }

    public required string Replacement { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClauseKind>))]
public enum ClauseKind
{
    /// <summary>Every assertion holds for every request in scope.</summary>
    [JsonStringEnumMemberName("must")]
    Must,

    /// <summary>The assertions never all hold together for any request in scope.</summary>
    [JsonStringEnumMemberName("never")]
    Never,
}

public sealed record ClauseDefinition
{
    public required string Id { get; init; }

    public required ClauseKind Kind { get; init; }

    public required string Title { get; init; }

    public required string Why { get; init; }

    public RequestMatch? When { get; init; }

    public required IReadOnlyList<PredicateDefinition> Assert { get; init; }

    public required ProvenanceRecord Provenance { get; init; }

    [JsonIgnore]
    public bool IsAccepted => Provenance.Status == ClauseLifecycle.Accepted;
}

[JsonConverter(typeof(JsonStringEnumConverter<PredicateOp>))]
public enum PredicateOp
{
    [JsonStringEnumMemberName("exists")]
    Exists,

    [JsonStringEnumMemberName("absent")]
    Absent,

    [JsonStringEnumMemberName("equals")]
    EqualsOp,

    [JsonStringEnumMemberName("notEquals")]
    NotEquals,

    [JsonStringEnumMemberName("lt")]
    Lt,

    [JsonStringEnumMemberName("lte")]
    Lte,

    [JsonStringEnumMemberName("gt")]
    Gt,

    [JsonStringEnumMemberName("gte")]
    Gte,

    [JsonStringEnumMemberName("between")]
    Between,

    [JsonStringEnumMemberName("approx")]
    Approx,

    [JsonStringEnumMemberName("matches")]
    Matches,

    [JsonStringEnumMemberName("notMatches")]
    NotMatches,

    [JsonStringEnumMemberName("contains")]
    Contains,

    [JsonStringEnumMemberName("notContains")]
    NotContains,

    [JsonStringEnumMemberName("in")]
    In,

    [JsonStringEnumMemberName("notIn")]
    NotIn,

    [JsonStringEnumMemberName("countEquals")]
    CountEquals,

    [JsonStringEnumMemberName("countAtLeast")]
    CountAtLeast,

    [JsonStringEnumMemberName("countAtMost")]
    CountAtMost,
}

[JsonConverter(typeof(JsonStringEnumConverter<Quantifier>))]
public enum Quantifier
{
    [JsonStringEnumMemberName("all")]
    All,

    [JsonStringEnumMemberName("any")]
    Any,

    [JsonStringEnumMemberName("none")]
    None,
}

public sealed record PredicateDefinition
{
    public required string Select { get; init; }

    public required PredicateOp Op { get; init; }

    public JsonNode? Value { get; init; }

    public string? Ref { get; init; }

    public decimal? Min { get; init; }

    public decimal? Max { get; init; }

    public decimal? Absolute { get; init; }

    public decimal? Relative { get; init; }

    public Quantifier? Quantifier { get; init; }

    public bool? IgnoreCase { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<ClauseOrigin>))]
public enum ClauseOrigin
{
    [JsonStringEnumMemberName("designed")]
    Designed,

    [JsonStringEnumMemberName("mined")]
    Mined,

    [JsonStringEnumMemberName("llm-draft")]
    LlmDraft,

    [JsonStringEnumMemberName("incident")]
    Incident,

    [JsonStringEnumMemberName("review")]
    Review,

    [JsonStringEnumMemberName("production-trace")]
    ProductionTrace,

    [JsonStringEnumMemberName("manual-session")]
    ManualSession,
}

[JsonConverter(typeof(JsonStringEnumConverter<ClauseLifecycle>))]
public enum ClauseLifecycle
{
    /// <summary>Reported, decides nothing.</summary>
    [JsonStringEnumMemberName("proposed")]
    Proposed,

    /// <summary>Counts towards the verdict.</summary>
    [JsonStringEnumMemberName("accepted")]
    Accepted,
}

public sealed record ProvenanceRecord
{
    public required ClauseOrigin Origin { get; init; }

    public required ClauseLifecycle Status { get; init; }

    public string? Author { get; init; }

    public string? Date { get; init; }

    public SupportRecord? Support { get; init; }
}

public sealed record SupportRecord
{
    public required int Observations { get; init; }

    public required int Violations { get; init; }
}

/// <summary>Reads a value written either as one string or as a list of strings.</summary>
public sealed class StringOrListConverter : JsonConverter<IReadOnlyList<string>>
{
    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString()!];
        }

        return JsonSerializer.Deserialize<List<string>>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
