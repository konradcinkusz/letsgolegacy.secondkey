using System.CommandLine;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Validation;
using SecondKey.Cli.Commands;
using SecondKey.Cli.Configuration;

namespace SecondKey.Cli;

/// <summary>The command line asked for something that cannot be done as written.</summary>
public sealed class UsageException : Exception
{
    public UsageException(string message)
        : base(message)
    {
    }

    public UsageException()
    {
    }

    public UsageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>What every command needs: where to write, and the shared --config option.</summary>
internal sealed class CliContext
{
    public CliContext(TextWriter output, TextWriter error)
    {
        Output = output;
        Error = error;
    }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public Option<string?> Config { get; } = new("--config")
    {
        Description = $"Path of the configuration file (default: ./{SecondKeyConfig.DefaultFileName} when it exists).",
        Recursive = true,
    };

    public SecondKeyConfig LoadConfig(ParseResult result) => SecondKeyConfig.Load(result.GetValue(Config));

    /// <summary>An absolute http(s) URL from an option or the configuration, or null when neither gives one.</summary>
    public static Uri? Url(string? value, string source)
    {
        if (value is null)
        {
            return null;
        }

        return System.Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new UsageException($"{source}: '{value}' is not an absolute http(s) URL");
    }
}

/// <summary>
/// The <c>sk</c> command line (C0). Every step of the chain runs from here, in a pipeline
/// (design rule 7). Exit codes are in <see cref="ExitCodes"/> and docs/cli.md.
/// </summary>
public static class SecondKeyCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var context = new CliContext(output, error);
        var root = BuildRoot(context);
        var parsed = root.Parse(args);
        if (parsed.Errors.Count > 0)
        {
            foreach (var parseError in parsed.Errors)
            {
                await error.WriteLineAsync(parseError.Message).ConfigureAwait(false);
            }

            await error.WriteLineAsync("Run 'sk --help' for usage.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        try
        {
            return await parsed.InvokeAsync(
                new InvocationConfiguration { Output = output, Error = error, EnableDefaultExceptionHandler = false },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArtifactValidationException ex)
        {
            await error.WriteLineAsync(ex.Report.ToString()).ConfigureAwait(false);
            return ExitCodes.InvalidInput;
        }
        catch (ConfigurationException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ExitCodes.InvalidInput;
        }
        catch (UsageException ex)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            await error.WriteLineAsync("Run 'sk --help' for usage.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("cancelled").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
#pragma warning disable CA1031 // The command line reports every failure as an exit code instead of a stack trace.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await error.WriteLineAsync($"error: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.RuntimeError;
        }
    }

    internal static RootCommand BuildRoot(CliContext context)
    {
        var root = new RootCommand($"Second Key {ToolInfo.Current.Version} — independent, deterministic evidence that a migrated system still keeps its contract.");
        root.Add(context.Config);
        root.Add(CaptureCommand.Build(context));
        root.Add(StubCommand.Build(context, "mine", "Propose contract clauses from recorded traffic (C2).", "C2, phase 02"));
        root.Add(ReplayCommand.Build(context));
        root.Add(CompareCommand.Build(context));
        root.Add(GateCommand.Build(context));
        root.Add(StubCommand.Build(context, "mutate", "Inject defects the contract must catch, and report the kill rate per clause (C9).", "C9, phase 02"));
        root.Add(EvidenceCommand.Build(context));
        root.Add(ValidateCommand.Build(context));
        return root;
    }
}
