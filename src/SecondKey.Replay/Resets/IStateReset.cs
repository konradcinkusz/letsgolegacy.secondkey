using System.Diagnostics;
using System.Globalization;
using SecondKey.Artifacts.Runs;

namespace SecondKey.Replay.Resets;

/// <summary>The result of putting a side back into its starting state.</summary>
public sealed record ResetResult(bool Succeeded, double DurationMs, string? Detail);

/// <summary>
/// Puts one side back into its starting state before a scenario, so the legacy system and
/// the candidate both start every scenario from the same state (S11).
/// </summary>
public interface IStateReset
{
    ResetMethod Method { get; }

    Task<ResetResult> ResetAsync(CancellationToken cancellationToken);
}

/// <summary>No reset: for read-only traffic, or systems whose state does not matter.</summary>
public sealed class NoReset : IStateReset
{
    public static NoReset Instance { get; } = new();

    public ResetMethod Method => ResetMethod.None;

    public Task<ResetResult> ResetAsync(CancellationToken cancellationToken) => Task.FromResult(new ResetResult(true, 0, null));
}

/// <summary>Calls an endpoint the system exposes for tests (e.g. <c>POST /__sk/reset</c>); any 2xx is success.</summary>
public sealed class HttpReset : IStateReset
{
    private readonly HttpClient _client;
    private readonly HttpMethod _method;
    private readonly Uri _uri;

    public HttpReset(HttpClient client, Uri baseUrl, string method, string path)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(baseUrl);
        _client = client;
        _method = new HttpMethod(method);
        _uri = new Uri(baseUrl, path);
    }

    public ResetMethod Method => ResetMethod.Http;

    public async Task<ResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(_method, _uri);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var ok = response.IsSuccessStatusCode;
            return new ResetResult(ok, clock.Elapsed.TotalMilliseconds, ok ? null : string.Create(CultureInfo.InvariantCulture, $"{_method} {_uri} answered {(int)response.StatusCode}"));
        }
        catch (HttpRequestException ex)
        {
            return new ResetResult(false, clock.Elapsed.TotalMilliseconds, $"{_method} {_uri} failed: {ex.Message}");
        }
    }
}

/// <summary>Runs a shell command (e.g. a script that restores a database backup); exit code 0 is success.</summary>
public sealed class CommandReset : IStateReset
{
    private readonly string _command;
    private readonly string? _workingDirectory;

    public CommandReset(string command, string? workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        _command = command;
        _workingDirectory = workingDirectory;
    }

    public ResetMethod Method => ResetMethod.Command;

    public async Task<ResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var windows = OperatingSystem.IsWindows();
        var start = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _workingDirectory ?? Environment.CurrentDirectory,
        };
        start.ArgumentList.Add(windows ? "/c" : "-c");
        start.ArgumentList.Add(_command);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start: {_command}");
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = (await stderr.ConfigureAwait(false)).Trim();
        return process.ExitCode == 0
            ? new ResetResult(true, clock.Elapsed.TotalMilliseconds, null)
            : new ResetResult(false, clock.Elapsed.TotalMilliseconds, string.Create(CultureInfo.InvariantCulture, $"'{_command}' exited {process.ExitCode}: {error}"));
    }
}
