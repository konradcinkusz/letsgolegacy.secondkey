namespace SecondKey.Cli.Tests;

/// <summary>Runs <c>sk</c> in-process and captures what it wrote.</summary>
internal static class CliRunner
{
    public static string Root { get; } = FindRoot();

    public static string Sample(string file) => Path.Combine(Root, "schemas", "samples", file);

    public static async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await SecondKeyCli.RunAsync(args, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SecondKey.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SecondKey.slnx not found");
    }
}
