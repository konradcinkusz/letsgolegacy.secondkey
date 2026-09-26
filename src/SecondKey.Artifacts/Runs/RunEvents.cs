using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SecondKey.Artifacts.Capture;

namespace SecondKey.Artifacts.Runs;

/// <summary>
/// One line of a <c>*.skrun</c> file: what the two sides answered when a capture was replayed.
/// The schema in <c>schemas/skrun.schema.json</c> is the authority on the format.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RunHeader), "run.header")]
[JsonDerivedType(typeof(StateReset), "state.reset")]
[JsonDerivedType(typeof(ExchangeResult), "exchange.result")]
[JsonDerivedType(typeof(RunEnd), "run.end")]
public abstract record RunEvent
{
    public const int FormatVersion = 1;

    [JsonPropertyOrder(-1)]
    public int V { get; init; } = FormatVersion;
}

/// <summary>The two systems under comparison. The names are fixed: the old system is always the oracle.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Side>))]
public enum Side
{
    [JsonStringEnumMemberName("legacy")]
    Legacy,

    [JsonStringEnumMemberName("candidate")]
    Candidate,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResetMethod>))]
public enum ResetMethod
{
    [JsonStringEnumMemberName("none")]
    None,

    [JsonStringEnumMemberName("sqlserver-snapshot")]
    SqlServerSnapshot,

    [JsonStringEnumMemberName("http")]
    Http,

    [JsonStringEnumMemberName("command")]
    Command,
}

[JsonConverter(typeof(JsonStringEnumConverter<ScenarioMode>))]
public enum ScenarioMode
{
    /// <summary>The exchanges of one captured session replay together, after one state reset.</summary>
    [JsonStringEnumMemberName("session")]
    Session,

    /// <summary>Every exchange is its own scenario.</summary>
    [JsonStringEnumMemberName("exchange")]
    Exchange,
}

public sealed record RunHeader : RunEvent
{
    public required string RunId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required ToolInfo Tool { get; init; }

    public required CaptureReference Capture { get; init; }

    public required RunSides Sides { get; init; }

    public required ScenarioMode ScenarioMode { get; init; }
}

public sealed record CaptureReference
{
    public required string CaptureId { get; init; }

    public required string Sha256 { get; init; }

    public string? Path { get; init; }
}

public sealed record RunSides
{
    public required SideInfo Legacy { get; init; }

    public required SideInfo Candidate { get; init; }
}

public sealed record SideInfo
{
    public required string BaseUrl { get; init; }

    public ResetMethod? Reset { get; init; }

    public string? Probe { get; init; }
}

public sealed record StateReset : RunEvent
{
    public required string Scenario { get; init; }

    public required Side Side { get; init; }

    public required ResetMethod Method { get; init; }

    /// <summary><c>ok</c> or <c>failed</c>.</summary>
    public required string Outcome { get; init; }

    public required double DurationMs { get; init; }

    public string? Detail { get; init; }
}

public sealed record ExchangeResult : RunEvent
{
    public required string Exchange { get; init; }

    public required string Scenario { get; init; }

    public required Side Side { get; init; }

    public required long Seq { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double DurationMs { get; init; }

    /// <summary>The request as it was sent to this side, after correlation substitutions.</summary>
    public required HttpRequestRecord Request { get; init; }

    public HttpResponseRecord? Response { get; init; }

    public DbDelta? Db { get; init; }

    public IReadOnlyList<OutboundCall>? Outbound { get; init; }

    public ExchangeError? Error { get; init; }
}

public sealed record ExchangeError
{
    /// <summary><c>connection</c>, <c>timeout</c>, <c>protocol</c> or <c>reset-failed</c>.</summary>
    public required string Kind { get; init; }

    public required string Message { get; init; }
}

/// <summary>Row-level changes a step made, per table, as a state probe saw them.</summary>
public sealed record DbDelta
{
    public string? Probe { get; init; }

    public required IReadOnlyDictionary<string, TableDelta> Tables { get; init; }
}

public sealed record TableDelta
{
    public required IReadOnlyList<JsonObject> Inserted { get; init; }

    public required IReadOnlyList<JsonObject> Deleted { get; init; }

    public required IReadOnlyList<RowUpdate> Updated { get; init; }
}

public sealed record RowUpdate
{
    public required JsonObject Key { get; init; }

    public required JsonObject Before { get; init; }

    public required JsonObject After { get; init; }
}

public sealed record OutboundCall
{
    public required string Method { get; init; }

    public required string Url { get; init; }

    public OutboundRequest? Request { get; init; }

    public HttpResponseRecord? Response { get; init; }
}

public sealed record OutboundRequest
{
    public IReadOnlyDictionary<string, string[]>? Headers { get; init; }

    public BodyRecord? Body { get; init; }
}

public sealed record RunEnd : RunEvent
{
    public required DateTimeOffset FinishedAt { get; init; }

    public required int Scenarios { get; init; }

    public required int Results { get; init; }
}
