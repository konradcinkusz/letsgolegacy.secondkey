using System.Diagnostics;
using System.Globalization;

namespace SecondKey.Evidence;

/// <summary>Whether the pack must include a PDF.</summary>
public enum PdfMode
{
    /// <summary>Render when a Chromium-based browser is available; otherwise say so and go on.</summary>
    Auto,

    /// <summary>A pack without its PDF is a failure.</summary>
    Required,

    /// <summary>No PDF.</summary>
    Off,
}

/// <summary>What happened to the PDF: written by which browser, or why not.</summary>
public sealed record PdfOutcome(bool Written, string? Browser, string? Reason);

/// <summary>Why a required PDF could not be rendered.</summary>
public sealed class PdfRenderingException : Exception
{
    public PdfRenderingException(string message)
        : base(message)
    {
    }

    public PdfRenderingException()
    {
    }

    public PdfRenderingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Prints the report to PDF with a headless Chromium-based browser — the one already on the
/// machine (design rule 3), found through <c>SK_CHROME_PATH</c>, the PATH, or where Chrome,
/// Edge and Playwright install it. The page is our own static HTML with no script and a
/// content security policy that forbids any request, which is why the browser's sandbox,
/// unavailable to root in containers and on some CI images, is switched off.
/// </summary>
public static class PdfRenderer
{
    public const string BrowserVariable = "SK_CHROME_PATH";

    private static readonly string[] Names =
    [
        "chromium", "chromium-browser", "google-chrome", "google-chrome-stable", "chrome",
        "microsoft-edge", "microsoft-edge-stable", "msedge",
    ];

    /// <summary>The browser to use, or null when there is none.</summary>
    /// <param name="environment">Reads an environment variable; the process environment when null.</param>
    /// <param name="exists">Whether a file exists; <see cref="File.Exists"/> when null.</param>
    public static string? FindBrowser(Func<string, string?>? environment = null, Func<string, bool>? exists = null) =>
        FindBrowser(environment ?? Environment.GetEnvironmentVariable, exists ?? File.Exists, OperatingSystem.IsWindows());

    /// <summary>The search itself, with the operating system as a parameter so each one's places can be tested anywhere.</summary>
    internal static string? FindBrowser(Func<string, string?> environment, Func<string, bool> exists, bool windows)
    {

        if (environment(BrowserVariable) is { Length: > 0 } configured)
        {
            return exists(configured) ? configured : throw new PdfRenderingException($"{BrowserVariable} is set to '{configured}', which does not exist");
        }

        var suffix = windows ? ".exe" : string.Empty;
        var onPath = (environment("PATH") ?? string.Empty)
            .Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => Names.Select(name => Path.Join(directory, name + suffix)));

        return onPath.Concat(WellKnown(environment, windows)).FirstOrDefault(exists);
    }

    /// <summary>Renders <paramref name="htmlPath"/> to <paramref name="pdfPath"/> as <paramref name="mode"/> asks.</summary>
    public static Task<PdfOutcome> RenderAsync(string htmlPath, string pdfPath, PdfMode mode, CancellationToken cancellationToken, string? browser = null, TimeSpan? timeout = null) =>
        RenderWithAsync(htmlPath, pdfPath, mode, () => browser ?? FindBrowser(), timeout, cancellationToken);

    /// <summary>The rendering itself, with the browser search as a parameter.</summary>
    internal static async Task<PdfOutcome> RenderWithAsync(string htmlPath, string pdfPath, PdfMode mode, Func<string?> find, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(htmlPath);
        ArgumentNullException.ThrowIfNull(pdfPath);
        if (mode == PdfMode.Off)
        {
            return new PdfOutcome(false, null, "switched off (--pdf off)");
        }

        string? browser = null;
        try
        {
            browser = find() ?? throw new PdfRenderingException($"no Chromium-based browser found; install Chrome, Edge or Chromium, or set {BrowserVariable}");
            await PrintAsync(browser, htmlPath, pdfPath, timeout ?? TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            return new PdfOutcome(true, browser, null);
        }
        catch (PdfRenderingException ex) when (mode == PdfMode.Auto)
        {
            return new PdfOutcome(false, browser, ex.Message);
        }
    }

    private static async Task PrintAsync(string browser, string htmlPath, string pdfPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var profile = Directory.CreateTempSubdirectory("sk-pdf-");
        try
        {
            File.Delete(pdfPath);
            var start = new ProcessStartInfo(browser)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in new[]
            {
                "--headless=new", "--disable-gpu", "--no-sandbox", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-background-networking", $"--user-data-dir={profile.FullName}",
                "--no-pdf-header-footer", "--print-to-pdf-no-header", $"--print-to-pdf={Path.GetFullPath(pdfPath)}",
                new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri,
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new PdfRenderingException($"{browser} did not start");
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            _ = process.StandardOutput.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Stop(process);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                throw new PdfRenderingException(string.Create(CultureInfo.InvariantCulture, $"{browser} did not finish within {timeout.TotalSeconds} s"));
            }

            if (process.ExitCode != 0 || !IsPdf(pdfPath))
            {
                var detail = (await errors.ConfigureAwait(false)).Trim();
                throw new PdfRenderingException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{browser} exited with {process.ExitCode} and wrote no PDF{(detail.Length > 0 ? ": " + Last(detail) : string.Empty)}"));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            throw new PdfRenderingException($"{browser} could not be run: {ex.Message}", ex);
        }
        finally
        {
            try
            {
                profile.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A browser helper still holding the profile; the temp directory is cleaned up by the OS.
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited on its own in the meantime.
        }
    }

    private static bool IsPdf(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[5];
        return stream.Read(header) == 5 && header.SequenceEqual("%PDF-"u8);
    }

    private static string Last(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines[^1];
    }

    private static IEnumerable<string> WellKnown(Func<string, string?> environment, bool windows)
    {
        if (windows)
        {
            foreach (var root in new[] { environment("ProgramFiles"), environment("ProgramFiles(x86)"), environment("LOCALAPPDATA") }.OfType<string>())
            {
                yield return Path.Join(root, @"Google\Chrome\Application\chrome.exe");
                yield return Path.Join(root, @"Microsoft\Edge\Application\msedge.exe");
            }

            yield break;
        }

        yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
        yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
        yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";

        // Playwright's browsers, as preinstalled on many CI images: the newest first.
        var playwright = environment("PLAYWRIGHT_BROWSERS_PATH") is { Length: > 0 } custom ? custom : "/opt/pw-browsers";
        if (Directory.Exists(playwright))
        {
            foreach (var directory in Directory.GetDirectories(playwright, "chromium-*").OrderDescending(StringComparer.Ordinal))
            {
                yield return Path.Combine(directory, "chrome-linux", "chrome");
                yield return Path.Combine(directory, "chrome-linux64", "chrome");
            }
        }
    }
}
