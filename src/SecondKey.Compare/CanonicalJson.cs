using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Contract.Predicates;

namespace SecondKey.Compare;

/// <summary>
/// The one canonical form two answers are compared in: object keys in ordinal order and
/// numbers in their shortest exact decimal form, so <c>1</c>, <c>1.0</c> and <c>1.00</c> are
/// the same value and key order never makes a difference.
/// </summary>
public static class CanonicalJson
{
    public static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        JsonValue value when value.GetValueKind() == JsonValueKind.Number && JsonValues.TryGetNumber(value, out var number) => JsonValue.Create(Normalize(number)),
        _ => node.DeepClone(),
    };

    /// <summary>The canonical text: equal texts mean equal values.</summary>
    public static string ToText(JsonNode? node) => Canonicalize(node)?.ToJsonString() ?? "null";

    private static decimal Normalize(decimal value) =>
        decimal.Parse(value.ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
}
