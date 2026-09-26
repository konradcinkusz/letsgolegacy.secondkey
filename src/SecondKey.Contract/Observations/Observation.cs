using System.Text.Json.Nodes;

namespace SecondKey.Contract.Observations;

/// <summary>
/// Everything a clause may look at about one request on one side, as a JSON document with
/// the roots a selector can name (<c>request</c>, <c>response</c>, <c>extract</c>, <c>db</c>,
/// <c>outbound</c> — see SelectorPath), plus <c>error</c> when the side gave no answer,
/// which the comparison reads and no selector does. Extraction failures are kept beside the
/// document: a clause that reads a value that failed to extract is an error, never a
/// silent absence.
/// </summary>
public sealed class Observation
{
    public Observation(JsonObject document, IReadOnlyDictionary<string, string> extractionErrors)
    {
        Document = document;
        ExtractionErrors = extractionErrors;
    }

    public JsonObject Document { get; }

    public IReadOnlyDictionary<string, string> ExtractionErrors { get; }

    public string Method => Document["request"]?["method"]?.GetValue<string>() ?? string.Empty;

    public string Path => Document["request"]?["path"]?.GetValue<string>() ?? string.Empty;

    /// <summary>The raw query values of the request, by name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Query { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}
