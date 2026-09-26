using System.CommandLine;
using System.Text.Json;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Validation;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Cli.Configuration;

namespace SecondKey.Cli.Commands;

/// <summary><c>sk validate &lt;file&gt;...</c>: checks artifacts against their formats. Exit 0 when all are valid, 3 otherwise.</summary>
internal static class ValidateCommand
{
    public static Command Build(CliContext context)
    {
        var files = new Argument<FileInfo[]>("files") { Description = "Artifacts to check: *.skcap, *.skrun, contract *.yaml, verdict *.json, or secondkey.yaml.", Arity = ArgumentArity.OneOrMore };
        var command = new Command("validate", "Check artifacts against their formats and report every error with its location.") { files };
        command.SetAction(async (result, cancellationToken) =>
        {
            var allValid = true;
            foreach (var file in result.GetRequiredValue(files))
            {
                var report = Validate(file);
                allValid &= report.IsValid;
                await context.Output.WriteLineAsync(report.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            return allValid ? ExitCodes.Success : ExitCodes.InvalidInput;
        });
        return command;
    }

    internal static ValidationReport Validate(FileInfo file)
    {
        if (!file.Exists)
        {
            return new ValidationReport(file.FullName, [new ArtifactError(null, "", "no such file")]);
        }

        var name = file.Name;
        var extension = file.Extension.ToUpperInvariant();
        if (string.Equals(name, SecondKeyConfig.DefaultFileName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _ = SecondKeyConfig.Load(file.FullName);
                return new ValidationReport(file.FullName, []);
            }
            catch (ConfigurationException ex)
            {
                return new ValidationReport(file.FullName, [new ArtifactError(null, "", ex.Message)]);
            }
        }

        return extension switch
        {
            ".SKCAP" => CaptureFile.Validate(file.FullName),
            ".SKRUN" => RunFile.Validate(file.FullName),
            ".YAML" or ".YML" => ContractFile.Validate(file.FullName),
            ".JSON" when IsVerdict(file) => VerdictFile.Validate(File.ReadAllText(file.FullName), file.FullName),
            _ => new ValidationReport(file.FullName, [new ArtifactError(null, "", "not a Second Key artifact: expected *.skcap, *.skrun, a contract *.yaml or a verdict *.json")]),
        };
    }

    private static bool IsVerdict(FileInfo file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind)
                && kind.ValueKind == JsonValueKind.String
                && kind.GetString() == "secondkey.verdict";
        }
        catch (JsonException)
        {
            return true; // Reported by the verdict validator as "not valid JSON".
        }
    }
}
