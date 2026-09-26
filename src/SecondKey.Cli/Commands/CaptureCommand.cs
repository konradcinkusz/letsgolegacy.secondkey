using System.CommandLine;
using System.Globalization;
using SecondKey.Capture;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk capture</c> (C1, S9): record traffic through a proxy in front of the legacy system.</summary>
internal static class CaptureCommand
{
    public static Command Build(CliContext context)
    {
        var listen = new Option<string?>("--listen") { Description = "Where the proxy listens (default: capture.listen, or http://127.0.0.1:8080)." };
        var target = new Option<string?>("--target") { Description = "The legacy system behind the proxy (default: capture.target)." };
        var output = new Option<string?>("--out") { Description = "The *.skcap file to write (default: capture.out, or .secondkey/traffic.skcap)." };
        var sessionKeys = new Option<string[]>("--session-key") { Description = "A cookie or header that identifies a user session. Repeatable.", AllowMultipleArgumentsPerToken = false };
        var redact = new Option<string[]>("--redact-header") { Description = "A header to redact, in addition to credentials and cookies. Repeatable." };
        var maxBody = new Option<long?>("--max-body-bytes") { Description = "Bodies beyond this size are cut and marked truncated (default 1 MiB)." };
        var exitAfter = new Option<long?>("--exit-after") { Description = "Stop by itself after this many exchanges." };

        var command = new Command("capture", "Record traffic to and from the legacy system through a proxy, without touching it (C1).")
        {
            listen, target, output, sessionKeys, redact, maxBody, exitAfter,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            var config = context.LoadConfig(result);
            var section = config.Capture;
            var targetUri = CliContext.Url(result.GetValue(target), "--target") ?? CliContext.ConfiguredUrl(section?.Target, "capture.target");
            if (targetUri is null)
            {
                await context.Error.WriteLineAsync("sk capture: no target — pass --target or set capture.target in secondkey.yaml".AsMemory(), cancellationToken).ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            var options = new CaptureOptions
            {
                Listen = CliContext.Url(result.GetValue(listen), "--listen") ?? CliContext.ConfiguredUrl(section?.Listen ?? "http://127.0.0.1:8080", "capture.listen")!,
                Target = targetUri,
                Output = result.GetValue(output) is { } o ? Path.GetFullPath(o) : config.Resolve(section?.Out ?? ".secondkey/traffic.skcap"),
                SessionKeys = Merge(result.GetValue(sessionKeys), section?.SessionKeys),
                RedactHeaders = Merge(CaptureOptions.DefaultRedactedHeaders, Merge(result.GetValue(redact), section?.RedactHeaders)),
                MaxBodyBytes = result.GetValue(maxBody) ?? section?.MaxBodyBytes ?? 1024 * 1024,
                ExitAfter = result.GetValue(exitAfter),
            };

            await using var proxy = await RecordingProxy.StartAsync(options, cancellationToken).ConfigureAwait(false);
            await context.Output.WriteLineAsync($"sk capture: recording {proxy.Address} -> {options.Target} into {options.Output}. Stop with Ctrl+C.".AsMemory(), cancellationToken).ConfigureAwait(false);
            try
            {
                await proxy.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C or SIGTERM: stop cleanly and keep what was recorded.
            }

            await proxy.StopAsync().ConfigureAwait(false);
            await context.Output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"sk capture: recorded {proxy.Recorded} exchange(s), skipped {proxy.Skipped} the legacy system did not answer.").AsMemory(), CancellationToken.None).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }

    private static List<string> Merge(IReadOnlyList<string>? first, IReadOnlyList<string>? second) =>
        (first ?? []).Concat(second ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
