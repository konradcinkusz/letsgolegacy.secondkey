using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecondKey.Contract.Predicates;

/// <summary>Value semantics shared by predicates and the comparator: numbers compare numerically.</summary>
public static class JsonValues
{
    public static bool TryGetNumber(JsonNode? node, out decimal number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (value.TryGetValue(out decimal d))
        {
            number = d;
            return true;
        }

        if (value.TryGetValue(out double f) && !double.IsNaN(f) && !double.IsInfinity(f) && Math.Abs(f) < (double)decimal.MaxValue)
        {
            number = (decimal)f;
            return true;
        }

        if (value.TryGetValue(out long l))
        {
            number = l;
            return true;
        }

        return decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>The value as a string when it is a string, or as its invariant text when it is a number or boolean.</summary>
    public static string? AsText(JsonNode? node) => node switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => TryGetNumber(v, out var n) ? n.ToString(CultureInfo.InvariantCulture) : v.ToJsonString(),
        JsonValue v when v.GetValueKind() is JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => null,
    };

    /// <summary>JSON equality where 1, 1.0 and 1.00 are equal; strings compare ordinally (or ignoring case).</summary>
    public static bool Equal(JsonNode? a, JsonNode? b, bool ignoreCase = false)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (TryGetNumber(a, out var x) && TryGetNumber(b, out var y))
        {
            return x == y;
        }

        var kindA = a.GetValueKind();
        if (kindA != b.GetValueKind())
        {
            return false;
        }

        switch (a)
        {
            case JsonObject oa when b is JsonObject ob:
                return oa.Count == ob.Count && oa.All(p => ob.TryGetPropertyValue(p.Key, out var v) && Equal(p.Value, v, ignoreCase));
            case JsonArray aa when b is JsonArray ab:
                return aa.Count == ab.Count && aa.Zip(ab).All(pair => Equal(pair.First, pair.Second, ignoreCase));
            default:
                if (kindA == JsonValueKind.String)
                {
                    return string.Equals(a.GetValue<string>(), b.GetValue<string>(), ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                }

                return JsonNode.DeepEquals(a, b);
        }
    }

    /// <summary>A short rendering of a value for evidence and error details.</summary>
    public static string Render(JsonNode? node)
    {
        var text = node is null ? "null" : node.ToJsonString();
        return text.Length <= 80 ? text : text[..77] + "...";
    }
}
