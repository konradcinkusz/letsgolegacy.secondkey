using System.Reflection;

namespace SecondKey.Artifacts;

/// <summary>
/// The name, version and commit of the tool that produced an artifact. Every artifact
/// records it, so a verdict can always be traced back to the exact build that computed it.
/// </summary>
/// <param name="Name">Always <see cref="ProductName"/>.</param>
/// <param name="Version">The semantic version, without build metadata.</param>
/// <param name="Commit">The source commit from the build metadata, when the build recorded one.</param>
public sealed record ToolInfo(string Name, string Version, string? Commit)
{
    public const string ProductName = "secondkey";

    public static ToolInfo Current { get; } = FromAssembly(typeof(ToolInfo).Assembly);

    public static ToolInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        return Parse(informational);
    }

    /// <summary>
    /// Splits an informational version such as <c>0.1.0+3f9a2c1</c> into the version and
    /// the commit. The SDK appends the commit as build metadata after a <c>+</c>.
    /// </summary>
    public static ToolInfo Parse(string informationalVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(informationalVersion);
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return new ToolInfo(ProductName, informationalVersion, null);
        }

        var commit = informationalVersion[(plus + 1)..];
        return new ToolInfo(ProductName, informationalVersion[..plus], commit.Length == 0 ? null : commit);
    }

    public override string ToString() => Commit is null ? $"{Name} {Version}" : $"{Name} {Version} ({Commit})";
}
