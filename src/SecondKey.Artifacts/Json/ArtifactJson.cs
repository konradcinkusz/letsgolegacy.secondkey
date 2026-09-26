using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecondKey.Artifacts.Json;

/// <summary>
/// The one set of serializer options every artifact is read and written with, so that the
/// same value always produces the same bytes (digests of artifacts are part of the evidence).
/// </summary>
public static class ArtifactJson
{
    /// <summary>Compact output, one JSON document per line — for *.skcap and *.skrun.</summary>
    public static JsonSerializerOptions Lines { get; } = Create(indented: false);

    /// <summary>Indented output — for verdict.json and other documents people read.</summary>
    public static JsonSerializerOptions Document { get; } = Create(indented: true);

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = indented,
            // Not Environment.NewLine, the default: the same verdict must be the same bytes on
            // Windows, where the legacy side runs, and on Linux, where CI does.
            NewLine = "\n",
            // Artifacts are files, never embedded in HTML unescaped; keeping "é" readable
            // matters more than escaping it. The evidence report HTML-encodes every value.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            AllowOutOfOrderMetadataProperties = true,
            RespectNullableAnnotations = false,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
