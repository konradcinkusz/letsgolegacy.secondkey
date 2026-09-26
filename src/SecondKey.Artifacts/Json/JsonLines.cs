using System.Text.Json;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Json;

/// <summary>
/// Reads a JSON Lines artifact: every line one JSON object, validated against the schema
/// branch its <c>type</c> names and then deserialized. A line that fails is reported with
/// its number and skipped; the caller decides whether a partly valid file is usable (it
/// never is for evidence).
/// </summary>
internal static class JsonLines
{
    public static List<(int Line, T Event)> Parse<T>(TextReader reader, ArtifactKind kind, List<ArtifactError> errors)
        where T : class
    {
        var events = new List<(int, T)>();
        var lineNumber = 0;
        int? firstBlank = null;

        while (reader.ReadLine() is { } text)
        {
            lineNumber++;
            if (text.Length == 0)
            {
                // A blank line at the very end is a trailing newline; one followed by more events is an error.
                firstBlank ??= lineNumber;
                continue;
            }

            if (firstBlank is { } blank)
            {
                errors.Add(new ArtifactError(blank, "", "blank line inside the file: every line must be one event"));
                firstBlank = null;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (JsonException ex)
            {
                errors.Add(new ArtifactError(lineNumber, "", $"not valid JSON: {ex.Message}"));
                continue;
            }

            using (document)
            {
                var schemaErrors = ArtifactSchemas.ValidateEvent(kind, document.RootElement, lineNumber);
                if (schemaErrors.Count > 0)
                {
                    errors.AddRange(schemaErrors);
                    continue;
                }

                var parsed = document.RootElement.Deserialize<T>(ArtifactJson.Lines)
                    ?? throw new InvalidOperationException($"line {lineNumber} deserialized to null although it is schema-valid");
                events.Add((lineNumber, parsed));
            }
        }

        return events;
    }
}
