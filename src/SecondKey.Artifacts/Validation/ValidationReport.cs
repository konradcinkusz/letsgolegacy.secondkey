using System.Globalization;

namespace SecondKey.Artifacts.Validation;

/// <summary>One thing wrong with an artifact, located precisely enough to fix it.</summary>
/// <param name="Line">1-based line for JSON Lines artifacts; null for whole documents.</param>
/// <param name="Location">A JSON pointer into the line or document, e.g. <c>/request/method</c>.</param>
/// <param name="Message">What is wrong.</param>
public sealed record ArtifactError(int? Line, string Location, string Message)
{
    public override string ToString()
    {
        var where = string.IsNullOrEmpty(Location) ? "/" : Location;
        return Line is { } line
            ? string.Create(CultureInfo.InvariantCulture, $"line {line} {where}: {Message}")
            : $"{where}: {Message}";
    }
}

/// <summary>The outcome of validating one artifact. Valid means no errors at all.</summary>
public sealed class ValidationReport
{
    public ValidationReport(string artifact, IEnumerable<ArtifactError> errors)
    {
        Artifact = artifact;
        Errors = errors.ToList();
    }

    /// <summary>What was validated — usually a file path.</summary>
    public string Artifact { get; }

    public IReadOnlyList<ArtifactError> Errors { get; }

    public bool IsValid => Errors.Count == 0;

    public override string ToString() => IsValid
        ? $"{Artifact}: valid"
        : $"{Artifact}: {Errors.Count} error(s){Environment.NewLine}  {string.Join(Environment.NewLine + "  ", Errors)}";
}

/// <summary>Thrown when an artifact that must be valid to be used is not.</summary>
public sealed class ArtifactValidationException : Exception
{
    public ArtifactValidationException(ValidationReport report)
        : base(report.ToString())
    {
        Report = report;
    }

    public ArtifactValidationException()
        : this(new ValidationReport("artifact", []))
    {
    }

    public ArtifactValidationException(string message)
        : this(new ValidationReport(message, []))
    {
    }

    public ArtifactValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Report = new ValidationReport(message, []);
    }

    public ValidationReport Report { get; }
}
