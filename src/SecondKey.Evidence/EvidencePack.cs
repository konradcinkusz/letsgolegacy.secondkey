using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SecondKey.Artifacts;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Validation;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Evidence.Sarif;

namespace SecondKey.Evidence;

/// <summary>What goes into one evidence pack, and where it is written.</summary>
public sealed record EvidenceOptions
{
    public required string VerdictPath { get; init; }

    public required string ContractPath { get; init; }

    /// <summary>The run the verdict was computed from, to include so the verdict can be recomputed; left out when null.</summary>
    public string? RunPath { get; init; }

    public IReadOnlyList<string> SarifPaths { get; init; } = [];

    public required string OutputDirectory { get; init; }

    public PdfMode Pdf { get; init; } = PdfMode.Auto;

    /// <summary>The browser to print with; found on the machine when null.</summary>
    public string? Browser { get; init; }
}

/// <summary>A file in the pack, with its digest.</summary>
public sealed record PackFile(string Path, string Sha256, long Size);

/// <summary>What was written.</summary>
public sealed record EvidenceResult(string Directory, IReadOnlyList<PackFile> Files, PdfOutcome Pdf, VerdictDocument Verdict, GateSummary? Gate);

/// <summary>The output directory cannot take a pack: it holds files a previous pack did not write.</summary>
public sealed class EvidenceOutputException : IOException
{
    public EvidenceOutputException(string message)
        : base(message)
    {
    }

    public EvidenceOutputException()
    {
    }

    public EvidenceOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// C10, unsigned (S14): the evidence pack — the report as HTML and PDF, the verdict, the
/// contract, the gate's SARIF and optionally the run, a manifest of every file, and an
/// in-toto statement naming each file's SHA-256. The pack refuses a contract or a run that is
/// not the one the verdict names, so it can never pair a verdict with the wrong inputs.
/// Signing the statement (DSSE, the owner's key) is phase 03.
/// </summary>
public static class EvidencePack
{
    public const string ReportName = "index.html";
    public const string PdfName = "report.pdf";
    public const string ManifestName = "manifest.json";
    public const string StatementName = "statement.intoto.json";
    public const string ManifestKind = "secondkey.evidence-manifest";
    public const string StatementType = "https://in-toto.io/Statement/v1";
    public const string PredicateType = "https://github.com/konradcinkusz/letsgolegacy.secondkey/evidence/v1";

    /// <summary>"\n" on every platform: a pack assembled on Windows must carry the digests it would on Linux.</summary>
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<EvidenceResult> WriteAsync(EvidenceOptions options, DateTimeOffset generatedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var verdict = VerdictFile.Read(options.VerdictPath);
        var contract = ContractFile.Load(options.ContractPath);
        Require(contract.Sha256 == verdict.Inputs.Contract.Sha256, options.ContractPath, $"not the contract the verdict was computed from: its SHA-256 is {contract.Sha256}, the verdict names {verdict.Inputs.Contract.Sha256}");
        if (options.RunPath is { } runPath)
        {
            var runSha = Sha256Digest.OfFile(runPath);
            Require(runSha == verdict.Inputs.Run.Sha256, runPath, $"not the run the verdict was computed from: its SHA-256 is {runSha}, the verdict names {verdict.Inputs.Run.Sha256}");
        }

        var logs = options.SarifPaths.Select(SarifLog.Read).ToList();
        var gate = logs.Count == 0 ? null : GateSummary.From(logs);

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.OutputDirectory));
        PrepareDirectory(directory);

        var copied = new List<string> { Copy(options.VerdictPath, directory, "verdict.json"), Copy(options.ContractPath, directory, "contract.yaml") };
        if (options.RunPath is { } run)
        {
            copied.Add(Copy(run, directory, "run.skrun"));
        }

        var sarifNames = new List<string>();
        foreach (var path in options.SarifPaths)
        {
            var name = Unique("sarif/" + Path.GetFileName(path), sarifNames);
            sarifNames.Add(name);
            copied.Add(Copy(path, directory, name));
        }

        var digests = copied
            .Where(name => name == "verdict.json" || name.StartsWith("sarif/", StringComparison.Ordinal))
            .Select(name => new DigestedFile(name, Sha256Digest.OfFile(Path.Combine(directory, name))))
            .ToList();
        var html = HtmlReport.Render(new ReportInput
        {
            Verdict = verdict,
            Contract = contract.Document,
            Gate = gate,
            Digests = digests,
            GeneratedAt = generatedAt,
            Tool = ToolInfo.Current,
        });
        await WriteTextAsync(Path.Combine(directory, ReportName), html, cancellationToken).ConfigureAwait(false);

        var pdf = await PdfRenderer.RenderAsync(Path.Combine(directory, ReportName), Path.Combine(directory, PdfName), options.Pdf, cancellationToken, options.Browser).ConfigureAwait(false);

        var subjects = Files(directory);
        await WriteTextAsync(Path.Combine(directory, StatementName), Statement(subjects, verdict, gate, sarifNames, generatedAt).ToJsonString(Indented) + "\n", cancellationToken).ConfigureAwait(false);
        var files = Files(directory);
        await WriteTextAsync(Path.Combine(directory, ManifestName), Manifest(files, generatedAt).ToJsonString(Indented) + "\n", cancellationToken).ConfigureAwait(false);

        return new EvidenceResult(directory, files, pdf, verdict, gate);
    }

    /// <summary>
    /// Makes <paramref name="directory"/> ready for a pack: created when missing; when it holds
    /// a previous pack, exactly the files that pack's manifest lists are removed; anything
    /// else in it is refused rather than deleted or mixed into the new pack.
    /// </summary>
    internal static void PrepareDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        if (!Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return;
        }

        var manifest = Path.Combine(directory, ManifestName);
        var listed = ReadManifest(manifest)
            ?? throw new EvidenceOutputException($"{directory} is not empty and holds no evidence pack; choose an empty or new directory");
        // Every listed path is checked before any is deleted.
        var paths = listed.Select(relative => (Relative: relative, Full: Path.GetFullPath(Path.Combine(directory, relative)))).ToList();
        if (paths.FirstOrDefault(p => !p.Full.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) is { Relative: not null } outside)
        {
            throw new EvidenceOutputException($"{manifest} lists '{outside.Relative}', which is outside the pack");
        }

        foreach (var (_, full) in paths)
        {
            File.Delete(full);
        }

        File.Delete(manifest);
        foreach (var sub in Directory.EnumerateDirectories(directory).Where(d => !Directory.EnumerateFileSystemEntries(d).Any()).ToList())
        {
            Directory.Delete(sub);
        }

        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new EvidenceOutputException($"{directory} holds files the previous pack did not write; choose an empty or new directory");
        }
    }

    private static List<string>? ReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonNode.Parse(File.ReadAllText(path));
            if (manifest?["kind"]?.GetValueKind() != JsonValueKind.String || manifest["kind"]!.GetValue<string>() != ManifestKind || manifest["files"] is not JsonArray files)
            {
                return null;
            }

            return files.Select(f => f?["path"]).OfType<JsonValue>().Select(v => v.GetValue<string>()).ToList();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonObject Statement(IReadOnlyList<PackFile> subjects, VerdictDocument verdict, GateSummary? gate, IReadOnlyList<string> sarif, DateTimeOffset generatedAt) => new()
    {
        ["_type"] = StatementType,
        ["subject"] = new JsonArray(subjects.Select(f => (JsonNode?)new JsonObject
        {
            ["name"] = f.Path,
            ["digest"] = new JsonObject { ["sha256"] = f.Sha256 },
        }).ToArray()),
        ["predicateType"] = PredicateType,
        ["predicate"] = new JsonObject
        {
            ["signed"] = false,
            ["createdAt"] = generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["tool"] = Tool(),
            ["outcome"] = JsonSerializer.SerializeToNode(verdict.Outcome),
            ["summary"] = JsonSerializer.SerializeToNode(verdict.Summary, Artifacts.Json.ArtifactJson.Document),
            ["contract"] = new JsonObject { ["name"] = verdict.Inputs.Contract.Name, ["revision"] = verdict.Inputs.Contract.Revision, ["sha256"] = verdict.Inputs.Contract.Sha256 },
            ["run"] = new JsonObject { ["runId"] = verdict.Inputs.Run.RunId, ["sha256"] = verdict.Inputs.Run.Sha256 },
            ["gate"] = gate is null ? null : new JsonObject
            {
                ["passes"] = gate.Passes,
                ["blocking"] = gate.Blocking.Count,
                ["findings"] = gate.Findings,
                ["logs"] = new JsonArray(sarif.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            },
        },
    };

    private static JsonObject Manifest(IReadOnlyList<PackFile> files, DateTimeOffset generatedAt) => new()
    {
        ["kind"] = ManifestKind,
        ["v"] = 1,
        ["createdAt"] = generatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        ["tool"] = Tool(),
        ["files"] = new JsonArray(files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha256, ["size"] = f.Size }).ToArray()),
    };

    private static JsonObject Tool() => new()
    {
        ["name"] = ToolInfo.Current.Name,
        ["version"] = ToolInfo.Current.Version,
        ["commit"] = ToolInfo.Current.Commit,
    };

    /// <summary>Every file in the pack but the manifest, by path with forward slashes, in ordinal order.</summary>
    private static List<PackFile> Files(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Relative: Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/')))
            .Where(f => f.Relative != ManifestName)
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .Select(f => new PackFile(f.Relative, Sha256Digest.OfFile(f.Path), new FileInfo(f.Path).Length))
            .ToList();

    private static string Copy(string source, string directory, string name)
    {
        var target = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target);
        return name;
    }

    private static string Unique(string name, IReadOnlyCollection<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var stem = Path.ChangeExtension(name, null);
        var extension = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}-{i}{extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static Task WriteTextAsync(string path, string text, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);

    private static void Require(bool condition, string path, string message)
    {
        if (!condition)
        {
            throw new ArtifactValidationException(new ValidationReport(path, [new ArtifactError(null, "", message)]));
        }
    }
}
