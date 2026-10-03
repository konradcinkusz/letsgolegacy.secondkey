using System.CommandLine;
using System.Globalization;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using SecondKey.Cli.Configuration;
using SecondKey.Replay;
using SecondKey.Replay.Probes;
using SecondKey.Replay.Resets;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk replay</c> (C6, S10–S11): replay a capture against both sides under identical conditions.</summary>
internal static class ReplayCommand
{
    public static Command Build(CliContext context)
    {
        var capture = new Option<string?>("--capture") { Description = "The *.skcap to replay (default: replay.capture)." };
        var output = new Option<string?>("--out") { Description = "The *.skrun to write (default: replay.out, or .secondkey/run.skrun)." };
        var legacy = new Option<string?>("--legacy") { Description = "Base URL of the legacy system (default: replay.legacy.baseUrl)." };
        var candidate = new Option<string?>("--candidate") { Description = "Base URL of the candidate (default: replay.candidate.baseUrl)." };
        var mode = new Option<string?>("--scenario-mode") { Description = "session (default) or exchange." };
        mode.AcceptOnlyFromAmong("session", "exchange");
        var timeout = new Option<int?>("--timeout") { Description = "Seconds to wait for each answer (default 30)." };
        var legacyReset = new Option<string?>("--legacy-reset-http") { Description = "Reset the legacy side before each scenario with POST to this path." };
        var candidateReset = new Option<string?>("--candidate-reset-http") { Description = "Reset the candidate before each scenario with POST to this path." };

        var command = new Command("replay", "Replay a capture against the legacy system and the candidate under identical conditions (C6).")
        {
            capture, output, legacy, candidate, mode, timeout, legacyReset, candidateReset,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var config = context.LoadConfig(result);
            var section = config.Replay;
            var capturePath = result.GetValue(capture) is { } c ? Path.GetFullPath(c) : section?.Capture is { } sc ? config.Resolve(sc) : null;
            var legacyUrl = CliContext.Url(result.GetValue(legacy), "--legacy") ?? CliContext.ConfiguredUrl(section?.Legacy?.BaseUrl, "replay.legacy.baseUrl");
            var candidateUrl = CliContext.Url(result.GetValue(candidate), "--candidate") ?? CliContext.ConfiguredUrl(section?.Candidate?.BaseUrl, "replay.candidate.baseUrl");
            if (capturePath is null || legacyUrl is null || candidateUrl is null)
            {
                await context.Error.WriteLineAsync("sk replay: needs a capture and both base URLs — --capture, --legacy, --candidate, or the replay section of secondkey.yaml".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var options = new ReplayOptions
            {
                Capture = CaptureFile.Read(capturePath),
                CapturePath = capturePath,
                Output = result.GetValue(output) is { } o ? Path.GetFullPath(o) : config.Resolve(section?.Out ?? ".secondkey/run.skrun"),
                Legacy = Side(config, legacyUrl, section?.Legacy, result.GetValue(legacyReset)),
                Candidate = Side(config, candidateUrl, section?.Candidate, result.GetValue(candidateReset)),
                ScenarioMode = (result.GetValue(mode) ?? section?.ScenarioMode) == "exchange" ? ScenarioMode.Exchange : ScenarioMode.Session,
                Timeout = TimeSpan.FromSeconds(result.GetValue(timeout) ?? section?.TimeoutSeconds ?? 30),
                Correlation = (section?.Correlation ?? [])
                    .Select(r => new CorrelationRule(r.Name, r.Compile(), r.FormField, r.Header))
                    .ToList(),
            };

            var summary = await ReplayEngine.RunAsync(options, cancellationToken).ConfigureAwait(false);
            await context.Output.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"sk replay: {summary.Scenarios} scenario(s), {summary.Results} result(s), {summary.Errors} without an answer, {summary.FailedResets} failed reset(s) -> {options.Output}").AsMemory(), cancellationToken).ConfigureAwait(false);
            if (summary.FailedResets > 0)
            {
                await context.Error.WriteLineAsync("sk replay: a side could not be reset; those scenarios were not replayed on it and the run is not a fair comparison.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.RuntimeError;
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static SideTarget Side(SecondKeyConfig file, Uri baseUrl, SideConfig? config, string? resetPath)
    {
        IStateReset reset = NoReset.Instance;
        if (resetPath is not null)
        {
            reset = new HttpReset(new HttpClient(), baseUrl, "POST", resetPath);
        }
        else if (config?.Reset is { } r)
        {
            // SecondKeyConfig has checked that the reset names exactly one kind.
            reset = r switch
            {
                { Http: { } http } => new HttpReset(new HttpClient(), baseUrl, http.Method, http.Path),
                { SqlServerSnapshot: { } sql } => new SqlServerSnapshotReset(Secret(sql.ConnectionStringEnv), sql.Database, sql.Snapshot),
                { Command: { } cmd } => new CommandReset(cmd.Run, cmd.WorkingDirectory is { } directory ? file.Resolve(directory) : null),
                _ => NoReset.Instance,
            };
        }

        var probe = config?.Probe?.SqlServer is { } p ? new SqlServerTableProbe(Secret(p.ConnectionStringEnv), p.Tables) : null;
        return new SideTarget { BaseUrl = baseUrl, Reset = reset, Probe = probe };
    }

    /// <summary>A secret is only ever read from the environment (P5); a missing one stops the command by name.</summary>
    private static string Secret(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new ConfigurationException($"environment variable {variable} is not set (see secrets.env.example)");
}
