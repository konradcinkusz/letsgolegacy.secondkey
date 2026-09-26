using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SecondKey.Artifacts.Capture;

/// <summary>
/// One line of a <c>*.skcap</c> file. Serialized with its <c>type</c> discriminator first;
/// the schema in <c>schemas/skcap.schema.json</c> is the authority on the format.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(CaptureHeader), "capture.header")]
[JsonDerivedType(typeof(HttpExchange), "http.exchange")]
public abstract record CaptureEvent
{
    public const int FormatVersion = 1;

    [JsonPropertyOrder(-1)]
    public int V { get; init; } = FormatVersion;
}

/// <summary>The first line of every capture: what recorded it, from where, and how it was sanitized.</summary>
public sealed record CaptureHeader : CaptureEvent
{
    public required string CaptureId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required ToolInfo Tool { get; init; }

    public required CaptureSource Source { get; init; }

    public CaptureSanitization? Sanitization { get; init; }

    public IReadOnlyList<string>? SessionKeys { get; init; }
}

public sealed record CaptureSource
{
    /// <summary><c>http-proxy</c> for a recording proxy; <c>synthetic</c> for hand-written or generated traffic.</summary>
    public required string Kind { get; init; }

    public string? Target { get; init; }

    public string? Listen { get; init; }
}

public sealed record CaptureSanitization
{
    public IReadOnlyList<string>? RedactedHeaders { get; init; }

    public long? MaxBodyBytes { get; init; }
}

/// <summary>One request to the legacy system and the response it gave, as seen at its boundary.</summary>
public sealed record HttpExchange : CaptureEvent
{
    public required string Id { get; init; }

    public required long Seq { get; init; }

    /// <summary>The session the exchange belongs to; exchanges of one session replay together.</summary>
    public required string Session { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required double DurationMs { get; init; }

    public required HttpRequestRecord Request { get; init; }

    public required HttpResponseRecord Response { get; init; }
}

public sealed record HttpRequestRecord
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    /// <summary>The raw query string including its leading <c>?</c>, or null.</summary>
    public string? Query { get; init; }

    /// <summary>Lower-case header names; every value is a list because HTTP headers repeat.</summary>
    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    public BodyRecord? Body { get; init; }
}

public sealed record HttpResponseRecord
{
    public required int Status { get; init; }

    public required IReadOnlyDictionary<string, string[]> Headers { get; init; }

    public BodyRecord? Body { get; init; }
}

/// <summary>How a body is stored: parsed JSON, text, or base64 for anything binary.</summary>
public enum BodyEncoding
{
    [JsonStringEnumMemberName("json")]
    Json,

    [JsonStringEnumMemberName("text")]
    Text,

    [JsonStringEnumMemberName("base64")]
    Base64,
}

public sealed record BodyRecord
{
    [JsonConverter(typeof(JsonStringEnumConverter<BodyEncoding>))]
    public required BodyEncoding Encoding { get; init; }

    public JsonNode? Json { get; init; }

    public string? Text { get; init; }

    public string? Base64 { get; init; }

    public string? ContentType { get; init; }

    public long? Size { get; init; }

    public bool? Truncated { get; init; }

    public static BodyRecord FromJson(JsonNode? json, string? contentType, long? size = null) =>
        new() { Encoding = BodyEncoding.Json, Json = json, ContentType = contentType, Size = size };

    public static BodyRecord FromText(string text, string? contentType, long? size = null, bool truncated = false) =>
        new() { Encoding = BodyEncoding.Text, Text = text, ContentType = contentType, Size = size, Truncated = truncated ? true : null };

    public static BodyRecord FromBytes(ReadOnlySpan<byte> bytes, string? contentType, long? size = null, bool truncated = false) =>
        new() { Encoding = BodyEncoding.Base64, Base64 = Convert.ToBase64String(bytes), ContentType = contentType, Size = size, Truncated = truncated ? true : null };
}
