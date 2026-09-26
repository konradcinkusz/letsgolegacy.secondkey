using System.Text;
using System.Text.Json;
using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Verdicts;

/// <summary>Writes and reads verdict.json. What is written is validated before it is written.</summary>
public static class VerdictFile
{
    public static string Serialize(VerdictDocument verdict) =>
        JsonSerializer.Serialize(verdict, ArtifactJson.Document) + "\n";

    /// <summary>Writes the verdict; throws if the document does not satisfy its own schema.</summary>
    public static async Task WriteAsync(string path, VerdictDocument verdict, CancellationToken cancellationToken = default)
    {
        var text = Serialize(verdict);
        var report = Validate(text, path);
        if (!report.IsValid)
        {
            throw new ArtifactValidationException(report);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
    }

    public static ValidationReport Validate(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return new ValidationReport(name, ArtifactSchemas.ValidateDocument(ArtifactKind.Verdict, document.RootElement));
        }
        catch (JsonException ex)
        {
            return new ValidationReport(name, [new ArtifactError(null, "", $"not valid JSON: {ex.Message}")]);
        }
    }

    public static VerdictDocument Read(string path)
    {
        var text = File.ReadAllText(path);
        var report = Validate(text, path);
        if (!report.IsValid)
        {
            throw new ArtifactValidationException(report);
        }

        return JsonSerializer.Deserialize<VerdictDocument>(text, ArtifactJson.Document)
            ?? throw new ArtifactValidationException(new ValidationReport(path, [new ArtifactError(null, "", "empty verdict")]));
    }
}
