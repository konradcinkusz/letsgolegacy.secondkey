namespace SecondKey.Capture;

/// <summary>How the recording proxy runs and what it keeps.</summary>
public sealed record CaptureOptions
{
    /// <summary>Where the proxy listens, e.g. <c>http://127.0.0.1:8080</c>.</summary>
    public required Uri Listen { get; init; }

    /// <summary>The legacy system behind the proxy.</summary>
    public required Uri Target { get; init; }

    /// <summary>The *.skcap file to write.</summary>
    public required string Output { get; init; }

    /// <summary>Cookie or header names whose value identifies a user session.</summary>
    public IReadOnlyList<string> SessionKeys { get; init; } = [];

    /// <summary>
    /// Headers whose values never reach the recording. Credentials and cookies by default:
    /// sanitization happens here, inside the customer's network, before anything is written.
    /// </summary>
    public IReadOnlyList<string> RedactHeaders { get; init; } = DefaultRedactedHeaders;

    /// <summary>Bodies beyond this size are cut and marked truncated.</summary>
    public long MaxBodyBytes { get; init; } = 1024 * 1024;

    /// <summary>Stop by itself after this many exchanges (for scripted recordings); null runs until stopped.</summary>
    public long? ExitAfter { get; init; }

    public static IReadOnlyList<string> DefaultRedactedHeaders { get; } =
        ["authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key"];
}
