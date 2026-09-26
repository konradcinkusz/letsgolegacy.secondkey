using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SecondKey.Artifacts.Verdicts;

/// <summary>
/// verdict.json: the deterministic outcome of comparing the legacy system and the candidate
/// against a contract. <c>schemas/verdict.schema.json</c> is the authority on the format.
/// </summary>
public sealed record VerdictDocument
{
    public const int FormatVersion = 1;

    public string Kind { get; init; } = "secondkey.verdict";

    public int V { get; init; } = FormatVersion;

    public required DateTimeOffset CreatedAt { get; init; }

    public required ToolInfo Tool { get; init; }

    public required VerdictInputs Inputs { get; init; }

    public required VerdictOutcome Outcome { get; init; }

    public required VerdictSummary Summary { get; init; }

    public required IReadOnlyList<ExchangeVerdict> Exchanges { get; init; }

    public required IReadOnlyList<ClauseSummary> Clauses { get; init; }

    public MutationSummary? Mutation { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<VerdictOutcome>))]
public enum VerdictOutcome
{
    /// <summary>No regression and no fix candidate.</summary>
    [JsonStringEnumMemberName("pass")]
    Pass,

    /// <summary>At least one regression.</summary>
    [JsonStringEnumMemberName("fail")]
    Fail,

    /// <summary>No regression, but at least one fix candidate a person must accept or reject.</summary>
    [JsonStringEnumMemberName("review")]
    Review,
}

/// <summary>The four classes every compared exchange lands in — exactly one each.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ExchangeClass>))]
public enum ExchangeClass
{
    [JsonStringEnumMemberName("equal")]
    Equal,

    [JsonStringEnumMemberName("equal-under-contract")]
    EqualUnderContract,

    [JsonStringEnumMemberName("regression")]
    Regression,

    [JsonStringEnumMemberName("fix-candidate")]
    FixCandidate,
}

[JsonConverter(typeof(JsonStringEnumConverter<ClauseOutcome>))]
public enum ClauseOutcome
{
    [JsonStringEnumMemberName("pass")]
    Pass,

    [JsonStringEnumMemberName("fail")]
    Fail,

    [JsonStringEnumMemberName("error")]
    Error,

    [JsonStringEnumMemberName("not-applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ClauseStatus>))]
public enum ClauseStatus
{
    [JsonStringEnumMemberName("held")]
    Held,

    [JsonStringEnumMemberName("regressed")]
    Regressed,

    [JsonStringEnumMemberName("fixed")]
    Fixed,

    [JsonStringEnumMemberName("violated-both")]
    ViolatedBoth,

    [JsonStringEnumMemberName("unexercised")]
    Unexercised,
}

public sealed record VerdictInputs
{
    public required ContractInput Contract { get; init; }

    public required RunInput Run { get; init; }
}

public sealed record ContractInput
{
    public required string Name { get; init; }

    public required int Revision { get; init; }

    public required string Sha256 { get; init; }

    public string? Path { get; init; }
}

public sealed record RunInput
{
    public required string RunId { get; init; }

    public required string Sha256 { get; init; }

    public string? Path { get; init; }
}

public sealed record VerdictSummary
{
    public int? Scenarios { get; init; }

    public required int Exchanges { get; init; }

    public required int Equal { get; init; }

    public required int EqualUnderContract { get; init; }

    public required int Regression { get; init; }

    public required int FixCandidate { get; init; }

    public required ClauseCounts Clauses { get; init; }
}

public sealed record ClauseCounts
{
    public required int Total { get; init; }

    public required int Accepted { get; init; }

    public required int Exercised { get; init; }

    public required int Unexercised { get; init; }

    /// <summary>The share of accepted clauses that are absence ("never") clauses.</summary>
    public double? AbsenceShare { get; init; }
}

public sealed record ExchangeVerdict
{
    public required string Exchange { get; init; }

    public required string Scenario { get; init; }

    public required RequestLine Request { get; init; }

    public required ExchangeClass Class { get; init; }

    public required IReadOnlyList<string> Reasons { get; init; }

    public required IReadOnlyList<Difference> Diffs { get; init; }

    public required IReadOnlyList<ClauseResultPair> Clauses { get; init; }
}

public sealed record RequestLine
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public string? Query { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DifferenceKind>))]
public enum DifferenceKind
{
    [JsonStringEnumMemberName("added")]
    Added,

    [JsonStringEnumMemberName("removed")]
    Removed,

    [JsonStringEnumMemberName("changed")]
    Changed,
}

/// <summary>One field that differs between the two sides.</summary>
public sealed record Difference
{
    public required string Path { get; init; }

    public required DifferenceKind Kind { get; init; }

    public JsonNode? Legacy { get; init; }

    public JsonNode? Candidate { get; init; }

    /// <summary>The normalization rule that explains this difference, when one does.</summary>
    public string? NormalizedBy { get; init; }
}

public sealed record ClauseResultPair
{
    public required string Id { get; init; }

    public required ClauseOutcome Legacy { get; init; }

    public required ClauseOutcome Candidate { get; init; }

    public string? Detail { get; init; }
}

public sealed record ClauseSummary
{
    public required string Id { get; init; }

    /// <summary><c>must</c> or <c>never</c>.</summary>
    public required string Kind { get; init; }

    public required string Title { get; init; }

    /// <summary>The clause in natural language, for the people who sign off.</summary>
    public required string Text { get; init; }

    public required bool Accepted { get; init; }

    public required int Exercised { get; init; }

    public required Tally Legacy { get; init; }

    public required Tally Candidate { get; init; }

    public required ClauseStatus Status { get; init; }
}

public sealed record Tally
{
    public required int Pass { get; init; }

    public required int Fail { get; init; }

    public required int Error { get; init; }
}

public sealed record MutationSummary
{
    public int? Mutants { get; init; }

    public int? Killed { get; init; }

    public IReadOnlyDictionary<string, int>? PerClause { get; init; }
}
