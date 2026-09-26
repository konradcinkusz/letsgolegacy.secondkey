using SecondKey.Artifacts.Json;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Runs;

/// <summary>A run that passed validation.</summary>
public sealed record RunDocument(
    RunHeader Header,
    IReadOnlyList<StateReset> Resets,
    IReadOnlyList<ExchangeResult> Results,
    RunEnd End)
{
    /// <summary>The result of one exchange on one side, or null when the side has none.</summary>
    public ExchangeResult? Find(string exchange, Side side) =>
        Results.FirstOrDefault(r => r.Side == side && string.Equals(r.Exchange, exchange, StringComparison.Ordinal));
}

/// <summary>
/// Validates and reads <c>*.skrun</c> files. Beyond the schema: a header first, a
/// <c>run.end</c> last (a run without one was interrupted and is refused), at most one
/// result per exchange and side, and a result count that agrees with the end line.
/// </summary>
public static class RunFile
{
    public static ValidationReport Validate(string path)
    {
        using var reader = File.OpenText(path);
        return Parse(reader, path).Report;
    }

    public static ValidationReport Validate(TextReader reader, string name) => Parse(reader, name).Report;

    public static RunDocument Read(string path)
    {
        using var reader = File.OpenText(path);
        return Read(reader, path);
    }

    public static RunDocument Read(TextReader reader, string name)
    {
        var (report, document) = Parse(reader, name);
        return report.IsValid ? document! : throw new ArtifactValidationException(report);
    }

    private static (ValidationReport Report, RunDocument? Document) Parse(TextReader reader, string name)
    {
        var errors = new List<ArtifactError>();
        var events = JsonLines.Parse<RunEvent>(reader, ArtifactKind.Run, errors);

        RunHeader? header = null;
        RunEnd? end = null;
        var endLine = 0;
        var resets = new List<StateReset>();
        var results = new List<ExchangeResult>();
        var seen = new HashSet<(string, Side)>();

        foreach (var (line, runEvent) in events)
        {
            if (end is not null)
            {
                errors.Add(new ArtifactError(line, "/type", "nothing may follow run.end"));
                continue;
            }

            if (line == 1 && runEvent is not RunHeader)
            {
                errors.Add(new ArtifactError(line, "/type", "the first line of a run must be its run.header"));
            }

            switch (runEvent)
            {
                case RunHeader h when line == 1:
                    header = h;
                    break;
                case RunHeader:
                    errors.Add(new ArtifactError(line, "/type", "a run has exactly one run.header, on its first line"));
                    break;
                case StateReset reset:
                    resets.Add(reset);
                    break;
                case ExchangeResult result:
                    if (!seen.Add((result.Exchange, result.Side)))
                    {
                        errors.Add(new ArtifactError(line, "/exchange", $"exchange '{result.Exchange}' already has a {result.Side.ToString().ToLowerInvariant()} result"));
                    }

                    results.Add(result);
                    break;
                case RunEnd e:
                    end = e;
                    endLine = line;
                    break;
            }
        }

        if (events.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ArtifactError(null, "", "empty run: the first line must be a run.header"));
        }

        if (events.Count > 0 && end is null)
        {
            errors.Add(new ArtifactError(null, "", "no run.end line: the run was interrupted, and an incomplete run is never compared"));
        }
        else if (end is not null && end.Results != results.Count)
        {
            errors.Add(new ArtifactError(endLine, "/results", $"run.end says {end.Results} result(s) but the file holds {results.Count}"));
        }

        var report = new ValidationReport(name, errors);
        return (report, report.IsValid ? new RunDocument(header!, resets, results, end!) : null);
    }
}
