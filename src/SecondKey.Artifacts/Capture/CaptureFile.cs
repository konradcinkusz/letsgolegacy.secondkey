using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Capture;

/// <summary>A capture that passed validation: its header and its exchanges in file order.</summary>
public sealed record CaptureDocument(CaptureHeader Header, IReadOnlyList<HttpExchange> Exchanges);

/// <summary>
/// Validates and reads <c>*.skcap</c> files. Beyond the schema, a capture must start with
/// exactly one header, and its exchange ids must be unique with strictly increasing
/// <c>seq</c> — the replay order is the recorded order, so a reordered file is refused.
/// </summary>
public static class CaptureFile
{
    public static ValidationReport Validate(string path)
    {
        using var reader = File.OpenText(path);
        return Parse(reader, path).Report;
    }

    public static ValidationReport Validate(TextReader reader, string name) => Parse(reader, name).Report;

    /// <summary>Reads a capture, or throws <see cref="ArtifactValidationException"/> listing every error.</summary>
    public static CaptureDocument Read(string path)
    {
        using var reader = File.OpenText(path);
        return Read(reader, path);
    }

    public static CaptureDocument Read(TextReader reader, string name)
    {
        var (report, document) = Parse(reader, name);
        return report.IsValid ? document! : throw new ArtifactValidationException(report);
    }

    private static (ValidationReport Report, CaptureDocument? Document) Parse(TextReader reader, string name)
    {
        var errors = new List<ArtifactError>();
        var events = JsonLines.Parse<CaptureEvent>(reader, ArtifactKind.Capture, errors);

        CaptureHeader? header = null;
        var exchanges = new List<HttpExchange>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long lastSeq = 0;

        foreach (var (line, captureEvent) in events)
        {
            switch (captureEvent)
            {
                case CaptureHeader h when line == 1:
                    header = h;
                    break;
                case CaptureHeader:
                    errors.Add(new ArtifactError(line, "/type", "a capture has exactly one capture.header, on its first line"));
                    break;
                case HttpExchange exchange:
                    if (line == 1)
                    {
                        errors.Add(new ArtifactError(line, "/type", "the first line of a capture must be its capture.header"));
                    }

                    if (!ids.Add(exchange.Id))
                    {
                        errors.Add(new ArtifactError(line, "/id", $"exchange id '{exchange.Id}' appears more than once"));
                    }

                    if (exchange.Seq <= lastSeq)
                    {
                        errors.Add(new ArtifactError(line, "/seq", $"seq {exchange.Seq} does not follow {lastSeq}: exchanges must be in recorded order"));
                    }

                    lastSeq = Math.Max(lastSeq, exchange.Seq);
                    exchanges.Add(exchange);
                    break;
            }
        }

        if (events.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ArtifactError(null, "", "empty capture: the first line must be a capture.header"));
        }

        var report = new ValidationReport(name, errors);
        return (report, report.IsValid ? new CaptureDocument(header!, exchanges) : null);
    }
}
