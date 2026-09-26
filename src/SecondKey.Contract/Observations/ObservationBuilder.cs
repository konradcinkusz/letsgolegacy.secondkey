using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Runs;
using SecondKey.Contract.Extraction;

namespace SecondKey.Contract.Observations;

/// <summary>Turns a recorded result (or a captured exchange) into an <see cref="Observation"/>.</summary>
public sealed class ObservationBuilder
{
    private readonly ExtractorSet _extractors;

    public ObservationBuilder(ExtractorSet extractors) => _extractors = extractors;

    public Observation Build(ExchangeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Build(result.Request, result.Response, result.Error, result.Db, result.Outbound);
    }

    public Observation Build(HttpExchange exchange)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        return Build(exchange.Request, exchange.Response, error: null, db: null, outbound: null);
    }

    public Observation Build(
        HttpRequestRecord request,
        HttpResponseRecord? response,
        ExchangeError? error,
        DbDelta? db,
        IReadOnlyList<OutboundCall>? outbound)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = QueryString.Parse(request.Query);
        var document = new JsonObject
        {
            ["request"] = new JsonObject
            {
                ["method"] = request.Method,
                ["path"] = request.Path,
                ["query"] = ToNode(query),
                ["headers"] = Headers(request.Headers),
                ["body"] = Body(request.Body, request.Headers),
            },
        };

        if (response is not null)
        {
            document["response"] = new JsonObject
            {
                ["status"] = response.Status,
                ["headers"] = Headers(response.Headers),
                ["body"] = Body(response.Body, response.Headers),
            };
        }

        if (error is not null)
        {
            document["error"] = new JsonObject { ["kind"] = error.Kind, ["message"] = error.Message };
        }

        if (db is not null)
        {
            document["db"] = JsonSerializer(db.Tables);
        }

        if (outbound is not null)
        {
            document["outbound"] = JsonSerializer(outbound);
        }

        var (extracted, errors) = _extractors.Extract(request, response);
        document["extract"] = extracted;
        return new Observation(document, errors) { Query = query };
    }

    private static JsonNode? JsonSerializer<T>(T value) =>
        System.Text.Json.JsonSerializer.SerializeToNode(value, ArtifactJson.Lines);

    private static JsonObject ToNode(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        var obj = new JsonObject();
        foreach (var (name, list) in values)
        {
            obj[name] = new JsonArray(list.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        return obj;
    }

    private static JsonObject Headers(IReadOnlyDictionary<string, string[]> headers)
    {
        var obj = new JsonObject();
        foreach (var (name, values) in headers.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            obj[name.ToLowerInvariant()] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        }

        return obj;
    }

    private static JsonObject? Body(BodyRecord? body, IReadOnlyDictionary<string, string[]> headers)
    {
        if (body is null)
        {
            return null;
        }

        var obj = new JsonObject();
        switch (body.Encoding)
        {
            case BodyEncoding.Json:
                obj["json"] = body.Json?.DeepClone();
                break;
            case BodyEncoding.Text:
                obj["text"] = body.Text;
                if (IsForm(body.ContentType ?? ContentType(headers)))
                {
                    obj["form"] = ToNode(QueryString.Parse(body.Text));
                }

                break;
            case BodyEncoding.Base64:
                obj["base64"] = body.Base64;
                break;
        }

        if (body.Size is { } size)
        {
            obj["size"] = size;
        }

        return obj;
    }

    private static string? ContentType(IReadOnlyDictionary<string, string[]> headers) =>
        headers.FirstOrDefault(h => string.Equals(h.Key, "content-type", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();

    private static bool IsForm(string? contentType) =>
        contentType is not null && contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parses <c>?a=1&amp;b=2&amp;a=3</c> (or a form body) into ordered name → values.</summary>
public static class QueryString
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string? text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }

        foreach (var pair in text.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(equals < 0 ? pair : pair[..equals]);
            var value = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
            if (!result.TryGetValue(name, out var list))
            {
                result[name] = list = [];
            }

            list.Add(value);
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal);
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
