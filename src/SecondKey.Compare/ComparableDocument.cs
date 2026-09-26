using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Contract.Observations;

namespace SecondKey.Compare;

/// <summary>
/// What is compared between the two sides of one exchange, taken from the observation the
/// clauses evaluated: the status; the headers the contract names; the body — an HTML page only
/// through its extracted values unless the contract asks for text, because markup is not
/// behaviour; the extracted values and extraction errors; row changes; outbound calls; and,
/// when a side did not answer, the kind of failure. The request is not compared: it is the
/// same capture on both sides.
/// </summary>
/// <remarks>
/// The document is canonical (see <see cref="CanonicalJson"/>), and two things that differ
/// only because the two sides run at different addresses are made equal here, before any
/// contract rule: the side's own base URL becomes <see cref="BaseUrlPlaceholder"/> wherever it
/// appears, and a <c>content-type</c> is written in one case and spacing.
/// </remarks>
public static class ComparableDocument
{
    public const string BaseUrlPlaceholder = "<base-url>";

    /// <summary>The response headers compared when the contract does not name any.</summary>
    public static IReadOnlyList<string> DefaultHeaders { get; } = ["content-type", "location"];

    public static JsonObject Build(Observation observation, CompareSettings? settings, string? baseUrl)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var source = observation.Document;
        var document = new JsonObject();

        if (source["response"] is JsonObject response)
        {
            var compared = new JsonObject { ["status"] = response["status"]?.DeepClone() };
            var headers = new JsonObject();
            foreach (var name in (settings?.Headers ?? DefaultHeaders).Select(h => h.ToLowerInvariant()))
            {
                if (response["headers"]?[name] is JsonArray values)
                {
                    headers[name] = new JsonArray(values.Select(v => Header(name, v)).ToArray());
                }
            }

            compared["headers"] = headers;
            var contentType = (response["headers"]?["content-type"] as JsonArray)?.FirstOrDefault() is JsonValue first && first.GetValueKind() == JsonValueKind.String ? first.GetValue<string>() : null;
            if (Body(response["body"] as JsonObject, contentType, settings?.Html ?? HtmlCompareMode.Extracts) is { } body)
            {
                compared["body"] = body;
            }

            document["response"] = compared;
        }

        if (source["error"] is JsonObject error)
        {
            document["error"] = new JsonObject { ["kind"] = error["kind"]?.DeepClone() };
        }

        document["extract"] = source["extract"]?.DeepClone() ?? new JsonObject();
        if (observation.ExtractionErrors.Count > 0)
        {
            document["extractErrors"] = new JsonObject(observation.ExtractionErrors.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)JsonValue.Create(e.Value))));
        }

        foreach (var root in new[] { "db", "outbound" })
        {
            if (source[root] is { } value)
            {
                document[root] = value.DeepClone();
            }
        }

        var trimmed = baseUrl?.TrimEnd('/');
        var canonical = (JsonObject)CanonicalJson.Canonicalize(document)!;
        return string.IsNullOrEmpty(trimmed) ? canonical : (JsonObject)Strings.Map(canonical, s => s.Replace(trimmed, BaseUrlPlaceholder, StringComparison.Ordinal))!;
    }

    /// <summary>
    /// <c>Application/JSON;Charset="UTF-8"</c> and <c>application/json; charset=utf-8</c> are
    /// the same content type: media type and parameter names are case-insensitive, and so is
    /// the charset value.
    /// </summary>
    public static string CanonicalContentType(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var parts = value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        var result = new List<string> { parts[0].ToLowerInvariant() };
        foreach (var parameter in parts.Skip(1))
        {
            var equals = parameter.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0)
            {
                result.Add(parameter.ToLowerInvariant());
                continue;
            }

            var name = parameter[..equals].Trim().ToLowerInvariant();
            var argument = parameter[(equals + 1)..].Trim().Trim('"');
            result.Add($"{name}={(name == "charset" ? argument.ToLowerInvariant() : argument)}");
        }

        return string.Join("; ", result);
    }

    private static JsonNode? Header(string name, JsonNode? value) =>
        name == "content-type" && value is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? JsonValue.Create(CanonicalContentType(v.GetValue<string>()))
            : value?.DeepClone();

    private static JsonObject? Body(JsonObject? body, string? contentType, HtmlCompareMode html)
    {
        if (body is null)
        {
            return null;
        }

        var compared = new JsonObject();
        if (body.TryGetPropertyValue("json", out var json))
        {
            compared["json"] = json?.DeepClone();
        }

        if (body["text"] is { } text && !(html == HtmlCompareMode.Extracts && BodyCodec.IsHtml(contentType)))
        {
            compared["text"] = text.DeepClone();
        }

        if (body["base64"] is { } base64)
        {
            compared["base64"] = base64.DeepClone();
        }

        return compared.Count == 0 ? null : compared;
    }
}

/// <summary>Rewrites every string value of a document, leaving keys, numbers and structure alone.</summary>
internal static class Strings
{
    public static JsonNode? Map(JsonNode? node, Func<string, string> map) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Map(p.Value, map)))),
        JsonArray array => new JsonArray(array.Select(item => Map(item, map)).ToArray()),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => JsonValue.Create(map(value.GetValue<string>())),
        _ => node?.DeepClone(),
    };
}
