using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Hashing;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Compare;

namespace SecondKey.Evidence.Tests;

/// <summary>The repository's samples, and a verdict computed from them so its digests are real.</summary>
internal static class Fixtures
{
    public static readonly DateTimeOffset At = new(2026, 9, 26, 10, 5, 0, TimeSpan.Zero);

    public static string Root { get; } = FindRoot();

    public static string Sample(string file) => Path.Combine(Root, "schemas", "samples", file);

    public static string SampleSarif { get; } = Path.Combine(Root, "samples", "gate", "portcullis.sarif");

    public static LoadedContract SampleContract() => ContractFile.Load(Sample("contract.yaml"));

    /// <summary>The verdict sk compare makes from the sample contract and run.</summary>
    public static VerdictDocument SampleVerdict() => Comparator.Compare(
        new ComparisonInputs
        {
            Contract = SampleContract().Document,
            ContractSha256 = Sha256Digest.OfFile(Sample("contract.yaml")),
            ContractPath = "contract.yaml",
            Run = RunFile.Read(Sample("sample.skrun")),
            RunSha256 = Sha256Digest.OfFile(Sample("sample.skrun")),
            RunPath = "sample.skrun",
        },
        At);

    /// <summary>A fresh directory under the system's temporary directory, removed when disposed.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory() => Path = Directory.CreateTempSubdirectory("sk-evidence-").FullName;

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A browser helper may still hold a file for a moment; the OS cleans temp up.
            }
        }
    }

    public static async Task<string> WriteVerdictAsync(TempDirectory directory, VerdictDocument? verdict = null)
    {
        var path = directory.File("verdict.json");
        await VerdictFile.WriteAsync(path, verdict ?? SampleVerdict());
        return path;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "SecondKey.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("SecondKey.slnx not found");
    }
}

/// <summary>
/// A test that needs a Chromium-based browser. It runs when one is found, and always when
/// SK_REQUIRE_PDF=1 — then a missing browser fails it instead of skipping it (CI sets that).
/// </summary>
public sealed class PdfFactAttribute : FactAttribute
{
    public PdfFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SK_REQUIRE_PDF") == "1")
        {
            return;
        }

        try
        {
            if (PdfRenderer.FindBrowser() is null)
            {
                Skip = "no Chromium-based browser here; set SK_CHROME_PATH, or SK_REQUIRE_PDF=1 to make this a failure";
            }
        }
        catch (PdfRenderingException ex)
        {
            Skip = ex.Message;
        }
    }
}
