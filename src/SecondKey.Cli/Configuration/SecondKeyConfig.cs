using System.Text.Json;
using System.Text.Json.Serialization;
using SecondKey.Artifacts.Yaml;

namespace SecondKey.Cli.Configuration;

/// <summary>Why secondkey.yaml could not be used.</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string message)
        : base(message)
    {
    }

    public ConfigurationException()
    {
    }

    public ConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// secondkey.yaml: defaults for every command, so a pipeline step is <c>sk replay</c> rather
/// than a line of options. Command-line options override it. Strict: an unknown key is an
/// error. Secrets never live here — a connection string is named by the environment
/// variable that holds it (P5).
/// </summary>
public sealed record SecondKeyConfig
{
    public const string DefaultFileName = "secondkey.yaml";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public int Version { get; init; } = 1;

    public CaptureConfig? Capture { get; init; }

    public ReplayConfig? Replay { get; init; }

    public CompareConfig? Compare { get; init; }

    public EvidenceConfig? Evidence { get; init; }

    public GateConfig? Gate { get; init; }

    /// <summary>The directory relative paths in the file are resolved against.</summary>
    [JsonIgnore]
    public string BaseDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>An empty configuration rooted at the current directory.</summary>
    public static SecondKeyConfig Empty => new();

    /// <summary>
    /// Loads the file at <paramref name="path"/>; with no path, loads ./secondkey.yaml when it
    /// exists and returns an empty configuration when it does not.
    /// </summary>
    public static SecondKeyConfig Load(string? path)
    {
        var explicitPath = path is not null;
        path ??= Path.Combine(Environment.CurrentDirectory, DefaultFileName);
        if (!File.Exists(path))
        {
            return explicitPath ? throw new ConfigurationException($"{path}: no such configuration file") : Empty;
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static SecondKeyConfig Parse(string yaml, string path)
    {
        try
        {
            var node = YamlJson.Convert(yaml);
            var config = node is null ? new SecondKeyConfig() : node.Deserialize<SecondKeyConfig>(Options) ?? new SecondKeyConfig();
            if (config.Version != 1)
            {
                throw new ConfigurationException($"{path}: version {config.Version} is not supported; this build reads version 1");
            }

            return config with { BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? Environment.CurrentDirectory };
        }
        catch (YamlConversionException ex)
        {
            throw new ConfigurationException($"{path}: not valid YAML: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException($"{path}: {ex.Message}", ex);
        }
    }

    /// <summary>A path from the file, made absolute against the file's directory.</summary>
    public string Resolve(string path) => Path.GetFullPath(Path.Combine(BaseDirectory, path));
}

public sealed record CaptureConfig
{
    public string? Listen { get; init; }

    public string? Target { get; init; }

    public string? Out { get; init; }

    public IReadOnlyList<string>? SessionKeys { get; init; }

    public IReadOnlyList<string>? RedactHeaders { get; init; }

    public long? MaxBodyBytes { get; init; }
}

public sealed record ReplayConfig
{
    public string? Capture { get; init; }

    public string? Out { get; init; }

    /// <summary><c>session</c> (default) or <c>exchange</c>.</summary>
    public string? ScenarioMode { get; init; }

    public int? TimeoutSeconds { get; init; }

    public SideConfig? Legacy { get; init; }

    public SideConfig? Candidate { get; init; }

    public IReadOnlyList<CorrelationConfig>? Correlation { get; init; }
}

public sealed record SideConfig
{
    public string? BaseUrl { get; init; }

    public ResetConfig? Reset { get; init; }

    public ProbeConfig? Probe { get; init; }
}

/// <summary>How a side is put back into its starting state before each scenario. One of the three.</summary>
public sealed record ResetConfig
{
    public HttpResetConfig? Http { get; init; }

    public SqlServerSnapshotConfig? SqlServerSnapshot { get; init; }

    public CommandResetConfig? Command { get; init; }
}

public sealed record HttpResetConfig
{
    public string Method { get; init; } = "POST";

    public required string Path { get; init; }
}

public sealed record SqlServerSnapshotConfig
{
    /// <summary>The environment variable holding the connection string — never the string itself.</summary>
    public required string ConnectionStringEnv { get; init; }

    public required string Database { get; init; }

    public required string Snapshot { get; init; }
}

public sealed record CommandResetConfig
{
    public required string Run { get; init; }

    public string? WorkingDirectory { get; init; }
}

public sealed record ProbeConfig
{
    public SqlServerProbeConfig? SqlServer { get; init; }
}

public sealed record SqlServerProbeConfig
{
    public required string ConnectionStringEnv { get; init; }

    public required IReadOnlyList<string> Tables { get; init; }
}

/// <summary>A value the server generated (a CSRF token) that later requests must carry again.</summary>
public sealed record CorrelationConfig
{
    public required string Name { get; init; }

    /// <summary>A regex over the response body text whose first group is the value.</summary>
    public required string Regex { get; init; }

    /// <summary>The form field the value is written into on later requests.</summary>
    public string? FormField { get; init; }

    /// <summary>The header the value is written into on later requests.</summary>
    public string? Header { get; init; }
}

public sealed record CompareConfig
{
    public string? Contract { get; init; }

    public string? Run { get; init; }

    public string? Out { get; init; }
}

public sealed record EvidenceConfig
{
    public string? Verdict { get; init; }

    public string? Contract { get; init; }

    public IReadOnlyList<string>? Sarif { get; init; }

    public string? Out { get; init; }

    /// <summary><c>auto</c> (default), <c>required</c> or <c>off</c>.</summary>
    public string? Pdf { get; init; }
}

public sealed record GateConfig
{
    public IReadOnlyList<string>? Sarif { get; init; }
}
