namespace SecondKey.Artifacts.Tests;

/// <summary>Locates the repository from the test assembly, so tests read the real samples in /schemas.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Sample(string file) => Path.Combine(Root, "schemas", "samples", file);

    public static string[] SampleLines(string file) => File.ReadAllLines(Sample(file));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SecondKey.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SecondKey.slnx not found above " + AppContext.BaseDirectory);
    }
}
