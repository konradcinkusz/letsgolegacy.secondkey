using System.Diagnostics;
using System.Globalization;

namespace SecondKey.Cli.Tests;

/// <summary>Runs <c>sk</c> in-process, or as a process of its own, and captures what it wrote.</summary>
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

    /// <summary>
    /// Starts <c>sk</c> as a process of its own, the way a terminal or a pipeline runs it, so
    /// that a signal reaches it the way theirs would. Standard output and error are read as
    /// they come; the process is the caller's to wait for and dispose of.
    /// </summary>
    public static SkProcess Start(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "SecondKey.Cli.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return new SkProcess(start);
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

/// <summary><c>sk</c> running as a process of its own; see <see cref="CliRunner.Start"/>.</summary>
internal sealed class SkProcess : IDisposable
{
    private readonly Process _process;
    private readonly object _gate = new();
    private readonly List<string> _output = [];
    private readonly List<string> _error = [];
    private readonly List<(string Prefix, TaskCompletionSource Seen)> _waits = [];

    public SkProcess(ProcessStartInfo start)
    {
        _process = new Process { StartInfo = start };
        _process.OutputDataReceived += (_, e) => Add(e.Data, _output);
        _process.ErrorDataReceived += (_, e) => Add(e.Data, _error);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public int ExitCode => _process.ExitCode;

    public string Output => Text(_output);

    public string Error => Text(_error);

    /// <summary>Completes when a line of standard output starts with <paramref name="prefix"/>.</summary>
    public Task LineAsync(string prefix)
    {
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_output.Any(line => line.StartsWith(prefix, StringComparison.Ordinal)))
            {
                seen.SetResult();
            }
            else
            {
                _waits.Add((prefix, seen));
            }
        }

        return seen.Task;
    }

    /// <summary>Sends the process a signal by name (TERM, INT), as <c>kill</c> does. Unix only.</summary>
    public void Signal(string name)
    {
        using var kill = Process.Start("kill", ["-" + name, _process.Id.ToString(CultureInfo.InvariantCulture)]);
        kill.WaitForExit();
        Assert.Equal(0, kill.ExitCode);
    }

    public Task WaitForExitAsync() => _process.WaitForExitAsync();

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    private string Text(List<string> lines)
    {
        lock (_gate)
        {
            return string.Join('\n', lines);
        }
    }

    private void Add(string? line, List<string> lines)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            lines.Add(line);
            foreach (var wait in _waits.Where(w => lines == _output && line.StartsWith(w.Prefix, StringComparison.Ordinal)).ToList())
            {
                wait.Seen.TrySetResult();
                _waits.Remove(wait);
            }
        }
    }
}
