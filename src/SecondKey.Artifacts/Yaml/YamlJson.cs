using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace SecondKey.Artifacts.Yaml;

/// <summary>Why a YAML text could not be turned into JSON, and where.</summary>
public sealed class YamlConversionException : Exception
{
    public YamlConversionException(string message, long line, long column)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    public YamlConversionException()
    {
    }

    public YamlConversionException(string message)
        : base(message)
    {
    }

    public YamlConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public long Line { get; }

    public long Column { get; }
}

/// <summary>
/// Converts YAML into JSON so a YAML artifact is validated by the same JSON Schema machinery
/// as every other artifact. Typing follows the YAML 1.2 core schema, not YAML 1.1: an
/// unquoted <c>2026-09-26</c> stays a string, <c>no</c> stays a string, and only
/// <c>true</c>/<c>false</c>, <c>null</c>/<c>~</c> and numbers are typed. Quoted scalars are
/// always strings. Duplicate keys are an error.
/// </summary>
public static partial class YamlJson
{
    public static JsonNode? Convert(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new YamlConversionException(ex.Message, ex.Start.Line, ex.Start.Column);
        }
        catch (InvalidOperationException ex)
        {
            // Some malformed flow collections fail inside the scanner without a position.
            throw new YamlConversionException($"malformed YAML ({ex.Message})", 0, 0);
        }

        return stream.Documents.Count switch
        {
            0 => null,
            1 => ToJson(stream.Documents[0].RootNode),
            _ => throw new YamlConversionException("expected one YAML document, found " + stream.Documents.Count.ToString(CultureInfo.InvariantCulture), 1, 1),
        };
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ToObject(mapping),
        YamlSequenceNode sequence => new JsonArray(sequence.Children.Select(ToJson).ToArray()),
        YamlScalarNode scalar => ToScalar(scalar),
        _ => throw new YamlConversionException($"unsupported YAML node {node.NodeType}", node.Start.Line, node.Start.Column),
    };

    private static JsonObject ToObject(YamlMappingNode mapping)
    {
        var obj = new JsonObject();
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key })
            {
                throw new YamlConversionException("mapping keys must be plain strings", keyNode.Start.Line, keyNode.Start.Column);
            }

            if (obj.ContainsKey(key))
            {
                throw new YamlConversionException($"duplicate key '{key}'", keyNode.Start.Line, keyNode.Start.Column);
            }

            obj[key] = ToJson(valueNode);
        }

        return obj;
    }

    private static JsonNode? ToScalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(value);
        }

        if (value.Length == 0 || value is "~" or "null" or "Null" or "NULL")
        {
            return null;
        }

        if (value is "true" or "True" or "TRUE")
        {
            return JsonValue.Create(true);
        }

        if (value is "false" or "False" or "FALSE")
        {
            return JsonValue.Create(false);
        }

        if (IntegerPattern().IsMatch(value) && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
        {
            return JsonValue.Create(integer);
        }

        if (FloatPattern().IsMatch(value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return JsonValue.Create(number);
        }

        return JsonValue.Create(value);
    }

    [GeneratedRegex("^[-+]?[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();
}
