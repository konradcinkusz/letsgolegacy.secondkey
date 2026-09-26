using System.Text.Json.Nodes;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Validation;
using static SecondKey.Evidence.Tests.Fixtures;

namespace SecondKey.Evidence.Tests;

/// <summary>S14: "the pack renders from fixtures".</summary>
public class EvidencePackTests
{
    private static async Task<EvidenceResult> WriteAsync(TempDirectory directory, Func<EvidenceOptions, EvidenceOptions>? change = null)
    {
        var options = new EvidenceOptions
        {
            VerdictPath = await WriteVerdictAsync(directory),
            ContractPath = Sample("contract.yaml"),
            RunPath = Sample("sample.skrun"),
            SarifPaths = [SampleSarif],
            OutputDirectory = directory.File("pack"),
            Pdf = PdfMode.Off,
        };
        return await EvidencePack.WriteAsync(change?.Invoke(options) ?? options, At, CancellationToken.None);
    }

    private static JsonNode Json(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    [Fact]
    public async Task The_pack_renders_from_the_sample_fixtures()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory);

        Assert.Equal(
            ["contract.yaml", "index.html", "run.skrun", "sarif/portcullis.sarif", "statement.intoto.json", "verdict.json"],
            pack.Files.Select(f => f.Path));
        Assert.All(pack.Files, f => Assert.Equal(Sha256Digest.OfFile(Path.Combine(pack.Directory, f.Path)), f.Sha256));
        Assert.All(pack.Files, f => Assert.Equal(new FileInfo(Path.Combine(pack.Directory, f.Path)).Length, f.Size));
        Assert.Equal(File.ReadAllBytes(Sample("contract.yaml")), File.ReadAllBytes(Path.Combine(pack.Directory, "contract.yaml")));
        Assert.Equal(File.ReadAllBytes(SampleSarif), File.ReadAllBytes(Path.Combine(pack.Directory, "sarif", "portcullis.sarif")));
        Assert.Contains("Behaviour: FAIL", File.ReadAllText(Path.Combine(pack.Directory, "index.html")), StringComparison.Ordinal);
        Assert.Equal(Artifacts.Verdicts.VerdictOutcome.Fail, pack.Verdict.Outcome);
        Assert.True(pack.Gate!.Passes);
        Assert.False(pack.Pdf.Written);
    }

    [Fact]
    public async Task Text_files_are_utf8_without_a_byte_order_mark_and_json_is_indented_and_ends_with_a_newline()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory);

        foreach (var name in new[] { "index.html", EvidencePack.ManifestName, EvidencePack.StatementName })
        {
            var bytes = File.ReadAllBytes(Path.Combine(pack.Directory, name));
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, $"{name} starts with a byte order mark");
        }

        Assert.StartsWith("{\n  \"kind\": ", File.ReadAllText(Path.Combine(pack.Directory, EvidencePack.ManifestName)), StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"_type\": ", File.ReadAllText(Path.Combine(pack.Directory, EvidencePack.StatementName)), StringComparison.Ordinal);
        Assert.EndsWith("}\n", File.ReadAllText(Path.Combine(pack.Directory, EvidencePack.StatementName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_report_lists_the_digests_of_the_verdict_and_the_gate_logs()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory);
        var html = File.ReadAllText(Path.Combine(pack.Directory, "index.html"));

        Assert.Contains($"<tr><td><code>verdict.json</code></td><td><code>{pack.Files.Single(f => f.Path == "verdict.json").Sha256}</code></td></tr>", html, StringComparison.Ordinal);
        Assert.Contains($"<tr><td><code>sarif/portcullis.sarif</code></td><td><code>{pack.Files.Single(f => f.Path == "sarif/portcullis.sarif").Sha256}</code></td></tr>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<tr><td><code>run.skrun</code></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<tr><td><code>contract.yaml</code></td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_manifest_lists_every_file_but_itself()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory);
        var manifest = Json(Path.Combine(pack.Directory, EvidencePack.ManifestName));

        Assert.Equal("secondkey.evidence-manifest", manifest["kind"]!.GetValue<string>());
        Assert.Equal(1, manifest["v"]!.GetValue<int>());
        Assert.Equal("2026-09-26T10:05:00Z", manifest["createdAt"]!.GetValue<string>());
        Assert.Equal("secondkey", manifest["tool"]!["name"]!.GetValue<string>());
        Assert.Equal(Artifacts.ToolInfo.Current.Version, manifest["tool"]!["version"]!.GetValue<string>());
        Assert.Equal(Artifacts.ToolInfo.Current.Commit, manifest["tool"]!["commit"]?.GetValue<string>());
        Assert.Equal(
            pack.Files.Select(f => (f.Path, f.Sha256, f.Size)),
            manifest["files"]!.AsArray().Select(f => (f!["path"]!.GetValue<string>(), f["sha256"]!.GetValue<string>(), f["size"]!.GetValue<long>())));
        Assert.EndsWith("}\n", File.ReadAllText(Path.Combine(pack.Directory, EvidencePack.ManifestName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_statement_names_every_other_file_and_what_was_decided()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory);
        var statement = Json(Path.Combine(pack.Directory, EvidencePack.StatementName));

        Assert.Equal("https://in-toto.io/Statement/v1", statement["_type"]!.GetValue<string>());
        Assert.Equal(EvidencePack.PredicateType, statement["predicateType"]!.GetValue<string>());
        Assert.Equal(
            pack.Files.Where(f => f.Path != EvidencePack.StatementName).Select(f => (f.Path, f.Sha256)),
            statement["subject"]!.AsArray().Select(s => (s!["name"]!.GetValue<string>(), s["digest"]!["sha256"]!.GetValue<string>())));

        var predicate = statement["predicate"]!;
        Assert.False(predicate["signed"]!.GetValue<bool>());
        Assert.Equal("2026-09-26T10:05:00Z", predicate["createdAt"]!.GetValue<string>());
        Assert.Equal("fail", predicate["outcome"]!.GetValue<string>());
        Assert.Equal(1, predicate["summary"]!["regression"]!.GetValue<int>());
        Assert.Equal(Sha256Digest.OfFile(Sample("contract.yaml")), predicate["contract"]!["sha256"]!.GetValue<string>());
        Assert.Equal("sample-shop", predicate["contract"]!["name"]!.GetValue<string>());
        Assert.Equal(1, predicate["contract"]!["revision"]!.GetValue<int>());
        Assert.Equal("run-20260926-sample", predicate["run"]!["runId"]!.GetValue<string>());
        Assert.Equal(Sha256Digest.OfFile(Sample("sample.skrun")), predicate["run"]!["sha256"]!.GetValue<string>());
        Assert.True(predicate["gate"]!["passes"]!.GetValue<bool>());
        Assert.Equal(0, predicate["gate"]!["blocking"]!.GetValue<int>());
        Assert.Equal(2, predicate["gate"]!["findings"]!.GetValue<int>());
        Assert.Equal(["sarif/portcullis.sarif"], predicate["gate"]!["logs"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Equal("secondkey", predicate["tool"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_gate_results_or_the_run_the_pack_says_so()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory, o => o with { SarifPaths = [], RunPath = null });
        var statement = Json(Path.Combine(pack.Directory, EvidencePack.StatementName));

        Assert.Equal(["contract.yaml", "index.html", "statement.intoto.json", "verdict.json"], pack.Files.Select(f => f.Path));
        Assert.Null(pack.Gate);
        Assert.Null(statement["predicate"]!["gate"]);
        Assert.Contains("Gate: not supplied", File.ReadAllText(Path.Combine(pack.Directory, "index.html")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_contract_that_is_not_the_one_the_verdict_names_is_refused()
    {
        using var directory = new TempDirectory();
        var changed = directory.File("contract.yaml");
        await File.WriteAllTextAsync(changed, File.ReadAllText(Sample("contract.yaml")) + "\n# edited after the verdict\n");

        var ex = await Assert.ThrowsAsync<ArtifactValidationException>(() => WriteAsync(directory, o => o with { ContractPath = changed }));

        Assert.StartsWith("not the contract the verdict was computed from: its SHA-256 is ", ex.Report.Errors.Single().Message, StringComparison.Ordinal);
        Assert.Equal("", ex.Report.Errors.Single().Location);
        Assert.Null(ex.Report.Errors.Single().Line);
        Assert.Equal(changed, ex.Report.Artifact);
        Assert.False(Directory.Exists(directory.File("pack")));
    }

    [Fact]
    public async Task A_run_that_is_not_the_one_the_verdict_names_is_refused()
    {
        using var directory = new TempDirectory();
        var other = directory.File("other.skrun");
        File.Copy(Sample("sample.skrun"), other);
        await File.AppendAllTextAsync(other, "\n");

        var ex = await Assert.ThrowsAsync<ArtifactValidationException>(() => WriteAsync(directory, o => o with { RunPath = other }));

        Assert.StartsWith("not the run the verdict was computed from", ex.Report.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invalid_verdict_or_sarif_is_refused_before_anything_is_written()
    {
        using var directory = new TempDirectory();
        var sarif = directory.File("broken.sarif");
        await File.WriteAllTextAsync(sarif, """{ "version": "1.0" }""");

        await Assert.ThrowsAsync<ArtifactValidationException>(() => WriteAsync(directory, o => o with { SarifPaths = [sarif] }));
        await File.WriteAllTextAsync(directory.File("verdict.json"), "{}");
        await Assert.ThrowsAsync<ArtifactValidationException>(() => EvidencePack.WriteAsync(
            new EvidenceOptions { VerdictPath = directory.File("verdict.json"), ContractPath = Sample("contract.yaml"), OutputDirectory = directory.File("pack") },
            At,
            CancellationToken.None));

        Assert.False(Directory.Exists(directory.File("pack")));
    }

    [Fact]
    public async Task Writing_again_replaces_the_previous_pack()
    {
        using var directory = new TempDirectory();
        await WriteAsync(directory);

        var again = await WriteAsync(directory, o => o with { SarifPaths = [], RunPath = null });

        Assert.Equal(["contract.yaml", "index.html", "statement.intoto.json", "verdict.json"], again.Files.Select(f => f.Path));
        Assert.False(Directory.Exists(Path.Combine(again.Directory, "sarif")));
        Assert.False(File.Exists(Path.Combine(again.Directory, "run.skrun")));
    }

    [Fact]
    public async Task A_directory_with_other_files_is_never_emptied()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.File("pack"));
        await File.WriteAllTextAsync(directory.File("pack/notes.txt"), "mine");

        var ex = await Assert.ThrowsAsync<EvidenceOutputException>(() => WriteAsync(directory));

        Assert.Contains("is not empty and holds no evidence pack", ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(directory.File("pack/notes.txt")));
    }

    [Fact]
    public async Task A_previous_pack_with_a_stranger_beside_it_is_refused_after_its_own_files_are_removed()
    {
        using var directory = new TempDirectory();
        await WriteAsync(directory);
        await File.WriteAllTextAsync(directory.File("pack/notes.txt"), "mine");

        var ex = await Assert.ThrowsAsync<EvidenceOutputException>(() => WriteAsync(directory));

        Assert.Contains("holds files the previous pack did not write", ex.Message, StringComparison.Ordinal);
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(directory.File("pack")).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData("""{ "kind": "secondkey.evidence-manifest", "files": [ { "path": "../outside.txt" } ] }""", "which is outside the pack")]
    [InlineData("""{ "kind": "something-else", "files": [] }""", "holds no evidence pack")]
    [InlineData("""{ "kind": 7, "files": [] }""", "holds no evidence pack")]
    [InlineData("""{ "kind": "secondkey.evidence-manifest" }""", "holds no evidence pack")]
    [InlineData("not json", "holds no evidence pack")]
    public async Task A_manifest_that_does_not_describe_a_pack_inside_the_directory_deletes_nothing(string manifest, string message)
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.File("pack"));
        await File.WriteAllTextAsync(directory.File("pack/manifest.json"), manifest);
        await File.WriteAllTextAsync(directory.File("outside.txt"), "keep me");

        var ex = await Assert.ThrowsAsync<EvidenceOutputException>(() => WriteAsync(directory));

        Assert.Contains(message, ex.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(directory.File("outside.txt")));
    }

    [Fact]
    public async Task Two_logs_with_the_same_name_are_both_kept()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.File("other"));
        var second = directory.File("other/portcullis.sarif");
        File.Copy(SampleSarif, second);

        var pack = await WriteAsync(directory, o => o with { SarifPaths = [SampleSarif, second, SampleSarif] });

        Assert.Equal(
            ["sarif/portcullis-2.sarif", "sarif/portcullis-3.sarif", "sarif/portcullis.sarif"],
            pack.Files.Where(f => f.Path.StartsWith("sarif/", StringComparison.Ordinal)).Select(f => f.Path));
        Assert.Equal(3, pack.Gate!.Logs.Count);
    }

    [Fact]
    public async Task An_output_path_with_a_trailing_separator_is_the_same_directory()
    {
        using var directory = new TempDirectory();
        await WriteAsync(directory);

        var again = await WriteAsync(directory, o => o with { OutputDirectory = directory.File("pack") + Path.DirectorySeparatorChar });

        Assert.Equal(directory.File("pack"), again.Directory);
    }

    [PdfFact]
    [Trait("Category", "Browser")]
    public async Task With_a_browser_the_pack_has_its_pdf_and_names_it()
    {
        using var directory = new TempDirectory();

        var pack = await WriteAsync(directory, o => o with { Pdf = PdfMode.Required });

        Assert.True(pack.Pdf.Written, pack.Pdf.Reason);
        Assert.Contains(pack.Files, f => f.Path == EvidencePack.PdfName && f.Size > 10_000);
        var statement = Json(Path.Combine(pack.Directory, EvidencePack.StatementName));
        Assert.Contains(statement["subject"]!.AsArray(), s => s!["name"]!.GetValue<string>() == EvidencePack.PdfName);
    }

    [Fact]
    public async Task Null_options_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => EvidencePack.WriteAsync(null!, At, CancellationToken.None));
    }
}
