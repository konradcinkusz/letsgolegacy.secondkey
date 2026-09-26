using System.Diagnostics;
using System.Runtime.Versioning;
using static SecondKey.Evidence.Tests.Fixtures;

namespace SecondKey.Evidence.Tests;

public class PdfRendererTests
{
    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    // ---- finding a browser ----------------------------------------------------------------

    [Fact]
    public void The_configured_browser_wins_and_a_wrong_path_is_an_error_not_a_silent_fallback()
    {
        Assert.Equal("/x/chrome", PdfRenderer.FindBrowser(Environment(("SK_CHROME_PATH", "/x/chrome"), ("PATH", "/usr/bin")), _ => true, windows: false));
        Assert.Equal("/x/chrome", PdfRenderer.FindBrowser(Environment(("SK_CHROME_PATH", "/x/chrome")), _ => true));

        var ex = Assert.Throws<PdfRenderingException>(() => PdfRenderer.FindBrowser(Environment(("SK_CHROME_PATH", "/missing/chrome")), path => path != "/missing/chrome", windows: false));
        Assert.Equal("SK_CHROME_PATH is set to '/missing/chrome', which does not exist", ex.Message);
    }

    [Theory]
    [InlineData("chromium")]
    [InlineData("chromium-browser")]
    [InlineData("google-chrome")]
    [InlineData("google-chrome-stable")]
    [InlineData("chrome")]
    [InlineData("microsoft-edge")]
    [InlineData("microsoft-edge-stable")]
    [InlineData("msedge")]
    public void Every_known_browser_name_is_looked_for_on_the_path(string name)
    {
        Assert.Equal($"/opt/b/{name}", PdfRenderer.FindBrowser(Environment(("PATH", "/opt/a:/opt/b")), path => path == $"/opt/b/{name}", windows: false));
    }

    [Fact]
    public void Google_chrome_is_preferred_to_chromium_in_the_same_directory()
    {
        Assert.Equal("/usr/bin/google-chrome", PdfRenderer.FindBrowser(Environment(("PATH", "/usr/bin")), path => path is "/usr/bin/chromium" or "/usr/bin/google-chrome", windows: false));
        Assert.Equal("/usr/bin/msedge", PdfRenderer.FindBrowser(Environment(("PATH", "/usr/bin")), path => path is "/usr/bin/chromium-browser" or "/usr/bin/msedge", windows: false));
    }

    [Fact]
    public void The_path_is_searched_directory_by_directory_and_an_empty_setting_is_no_setting()
    {
        var found = PdfRenderer.FindBrowser(
            Environment(("PATH", "/opt/a::/opt/b"), ("SK_CHROME_PATH", "")),
            path => path is "/opt/b/chromium" or "/opt/b/google-chrome" or "/opt/a/msedge",
            windows: false);

        Assert.Equal("/opt/a/msedge", found);
    }

    [Fact]
    public void On_windows_the_path_uses_semicolons_executables_end_in_exe_and_program_files_are_searched()
    {
        var chrome = Path.Join(@"C:\Program Files", @"Google\Chrome\Application\chrome.exe");
        var edge = Path.Join(@"C:\Program Files (x86)", @"Microsoft\Edge\Application\msedge.exe");
        var local = Path.Join(@"C:\Users\me\AppData\Local", @"Google\Chrome\Application\chrome.exe");
        var environment = Environment(("PATH", @"C:\tools;C:\bin"), ("ProgramFiles", @"C:\Program Files"), ("ProgramFiles(x86)", @"C:\Program Files (x86)"), ("LOCALAPPDATA", @"C:\Users\me\AppData\Local"));

        Assert.Equal(Path.Join(@"C:\bin", "msedge.exe"), PdfRenderer.FindBrowser(environment, path => path == Path.Join(@"C:\bin", "msedge.exe"), windows: true));
        Assert.Equal(chrome, PdfRenderer.FindBrowser(environment, path => path == chrome || path == edge, windows: true));
        Assert.Equal(edge, PdfRenderer.FindBrowser(environment, path => path == edge || path == local, windows: true));
        Assert.Equal(local, PdfRenderer.FindBrowser(environment, path => path == local, windows: true));
        Assert.Null(PdfRenderer.FindBrowser(environment, path => path.StartsWith('/'), windows: true));
    }

    [Theory]
    [InlineData("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome")]
    [InlineData("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge")]
    [InlineData("/Applications/Chromium.app/Contents/MacOS/Chromium")]
    public void The_usual_mac_applications_are_found(string application)
    {
        Assert.Equal(application, PdfRenderer.FindBrowser(Environment(), path => path == application, windows: false));
    }

    [Fact]
    public void Playwrights_browsers_are_found_newest_first_in_either_layout()
    {
        using var directory = new TempDirectory();
        foreach (var build in new[] { "chromium-1100/chrome-linux", "chromium-1200/chrome-linux64", "chromium_headless_shell-1300/chrome-linux" })
        {
            Directory.CreateDirectory(directory.File(build));
            File.WriteAllText(directory.File(build + "/chrome"), string.Empty);
        }

        var environment = Environment(("PLAYWRIGHT_BROWSERS_PATH", directory.Path));

        Assert.Equal(Path.Combine(directory.Path, "chromium-1200", "chrome-linux64", "chrome"), PdfRenderer.FindBrowser(environment, File.Exists, windows: false));
        File.Delete(directory.File("chromium-1200/chrome-linux64/chrome"));
        Assert.Equal(Path.Combine(directory.Path, "chromium-1100", "chrome-linux", "chrome"), PdfRenderer.FindBrowser(environment, File.Exists, windows: false));
    }

    [Fact]
    public void Without_any_browser_there_is_none()
    {
        Assert.Null(PdfRenderer.FindBrowser(Environment(("PATH", "/nowhere"), ("PLAYWRIGHT_BROWSERS_PATH", "/no/such/dir")), _ => false, windows: false));
        Assert.Null(PdfRenderer.FindBrowser(Environment(), _ => false, windows: false));
        Assert.Null(PdfRenderer.FindBrowser(Environment(), _ => false));
    }

    // ---- rendering ------------------------------------------------------------------------

    [Fact]
    public async Task Switched_off_it_writes_nothing()
    {
        using var directory = new TempDirectory();

        var outcome = await PdfRenderer.RenderAsync(directory.File("a.html"), directory.File("a.pdf"), PdfMode.Off, CancellationToken.None);

        Assert.Equal(new PdfOutcome(false, null, "switched off (--pdf off)"), outcome);
        Assert.False(File.Exists(directory.File("a.pdf")));
    }

    [Fact]
    public async Task No_browser_is_reported_in_auto_mode_and_is_an_error_when_required()
    {
        using var directory = new TempDirectory();
        const string Message = "no Chromium-based browser found; install Chrome, Edge or Chromium, or set SK_CHROME_PATH";

        var auto = await PdfRenderer.RenderWithAsync(directory.File("a.html"), directory.File("a.pdf"), PdfMode.Auto, () => null, null, CancellationToken.None);
        var required = await Assert.ThrowsAsync<PdfRenderingException>(() => PdfRenderer.RenderWithAsync(directory.File("a.html"), directory.File("a.pdf"), PdfMode.Required, () => null, null, CancellationToken.None));

        Assert.Equal(new PdfOutcome(false, null, Message), auto);
        Assert.Equal(Message, required.Message);
    }

    [Fact]
    public async Task A_browser_that_prints_is_given_the_page_headless_with_its_own_profile_which_is_removed_after()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a b.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        var browser = FakeBrowser(directory);

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Required, CancellationToken.None, browser);

        Assert.Equal(new PdfOutcome(true, browser, null), outcome);
        var arguments = File.ReadAllLines(directory.File("arguments.txt"));
        Assert.Equal(
            [
                "--headless=new", "--disable-gpu", "--no-sandbox", "--no-first-run", "--no-default-browser-check",
                "--disable-extensions", "--disable-background-networking", "--disable-component-update",
                "--disable-default-apps", "--disable-sync", "--disable-breakpad", "--disable-dev-shm-usage",
                "--password-store=basic", "--use-mock-keychain", "--mute-audio", "--hide-scrollbars",
            ],
            arguments[..16]);
        Assert.StartsWith("--user-data-dir=", arguments[16], StringComparison.Ordinal);
        Assert.False(Directory.Exists(arguments[16]["--user-data-dir=".Length..]), "the browser profile is removed");
        Assert.Equal(["--no-pdf-header-footer", "--print-to-pdf-no-header", $"--print-to-pdf={directory.File("a.pdf")}", new Uri(html).AbsoluteUri], arguments[17..]);
        Assert.EndsWith("a%20b.html", arguments[^1], StringComparison.Ordinal);
        Assert.Equal("disabled:", File.ReadAllText(directory.File("dbus.txt")));
    }

    [Fact]
    public async Task A_stale_pdf_from_an_earlier_run_never_passes_for_a_new_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        await File.WriteAllTextAsync(directory.File("a.pdf"), "%PDF-1.4 from yesterday");

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, Script(directory, "silent", "exit 0"));

        Assert.False(outcome.Written);
        Assert.False(File.Exists(directory.File("a.pdf")));
    }

    [Fact]
    public async Task A_browser_that_fails_is_reported_in_auto_mode_and_is_an_error_when_required()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        var failing = Script(directory, "fails", "printf 'first line\\n\\n   \\n  the last line says why  \\n' >&2\nexit 3");
        var silent = Script(directory, "silent", "exit 0");

        var auto = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, failing);
        var nothing = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, silent);
        var required = await Assert.ThrowsAsync<PdfRenderingException>(() => PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Required, CancellationToken.None, failing));

        Assert.False(auto.Written);
        Assert.Equal(failing, auto.Browser);
        Assert.Equal($"{failing} exited with 3 and wrote no PDF: the last line says why", auto.Reason);
        Assert.Equal($"{silent} exited with 0 and wrote no PDF", nothing.Reason);
        Assert.Equal(auto.Reason, required.Message);
    }

    [Fact]
    public async Task A_file_that_is_not_a_pdf_does_not_count_as_one()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, FakeBrowser(directory, "HTML!"));
        var shortFile = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, FakeBrowser(directory, "%PD"));

        Assert.False(outcome.Written);
        Assert.Contains("wrote no PDF", outcome.Reason, StringComparison.Ordinal);
        Assert.False(shortFile.Written);
    }

    [Fact]
    public async Task A_browser_that_hangs_is_stopped_with_everything_it_started()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        var hangs = Script(directory, "hangs", $"echo $$ > '{directory.File("parent.pid")}'\nsleep 30 &\necho $! > '{directory.File("child.pid")}'\nwait");

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, hangs, TimeSpan.FromMilliseconds(500));

        Assert.Equal($"{hangs} did not finish within 0.5 s", outcome.Reason);
        AssertStopped(directory.File("parent.pid"));
        AssertStopped(directory.File("child.pid"));
    }

    [Fact]
    public async Task A_browser_that_hangs_is_reported_with_the_last_thing_it_said()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        var hangs = Script(directory, "hangs", "echo 'starting' >&2\necho 'waiting for the keyring' >&2\nexec sleep 30");

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, hangs, TimeSpan.FromMilliseconds(500));

        Assert.Equal($"{hangs} did not finish within 0.5 s: waiting for the keyring", outcome.Reason);
    }

    [Fact]
    public void A_browser_gets_a_minute_by_default()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), PdfRenderer.DefaultTimeout);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_started_is_reported()
    {
        using var directory = new TempDirectory();
        var missing = directory.File("no-such-browser");

        var outcome = await PdfRenderer.RenderAsync(directory.File("a.html"), directory.File("a.pdf"), PdfMode.Auto, CancellationToken.None, missing);

        Assert.False(outcome.Written);
        Assert.StartsWith($"{missing} could not be run: ", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_stops_the_browser_and_is_not_reported_as_a_timeout()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TempDirectory();
        var html = directory.File("a.html");
        await File.WriteAllTextAsync(html, "<p>x</p>");
        var hangs = Script(directory, "hangs", $"echo $$ > '{directory.File("parent.pid")}'\nexec sleep 30");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfRenderer.RenderAsync(html, directory.File("a.pdf"), PdfMode.Auto, cancel.Token, hangs));
        AssertStopped(directory.File("parent.pid"));
    }

    [PdfFact]
    [Trait("Category", "Browser")]
    public async Task A_real_browser_prints_the_report_to_pdf()
    {
        using var directory = new TempDirectory();
        var html = directory.File("index.html");
        await File.WriteAllTextAsync(html, HtmlReport.Render(new ReportInput
        {
            Verdict = SampleVerdict(),
            Contract = SampleContract().Document,
            Digests = [],
            GeneratedAt = At,
            Tool = Artifacts.ToolInfo.Current,
        }));

        var outcome = await PdfRenderer.RenderAsync(html, directory.File("report.pdf"), PdfMode.Required, CancellationToken.None);

        Assert.True(outcome.Written, outcome.Reason);
        Assert.NotNull(outcome.Browser);
        var bytes = await File.ReadAllBytesAsync(directory.File("report.pdf"));
        Assert.True(bytes.Length > 10_000);
        Assert.Equal("%PDF-"u8.ToArray(), bytes[..5]);
    }

    [Fact]
    public async Task Null_arguments_are_refused()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => PdfRenderer.RenderAsync(null!, "a.pdf", PdfMode.Off, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => PdfRenderer.RenderAsync("a.html", null!, PdfMode.Off, CancellationToken.None));
    }

    /// <summary>A stand-in browser: records its arguments and writes <paramref name="content"/> where it is told to print.</summary>
    [UnsupportedOSPlatform("windows")]
    private static string FakeBrowser(TempDirectory directory, string content = "%PDF-1.4 fake") => Script(
        directory,
        "fake-browser",
        $$"""
        printf '%s' "$DBUS_SESSION_BUS_ADDRESS" > '{{directory.File("dbus.txt")}}'
        : > '{{directory.File("arguments.txt")}}'
        for a in "$@"; do
          printf '%s\n' "$a" >> '{{directory.File("arguments.txt")}}'
          case "$a" in
            --user-data-dir=*) [ -d "${a#--user-data-dir=}" ] || exit 9 ;;
            --print-to-pdf=*) printf '%s' '{{content}}' > "${a#--print-to-pdf=}" ;;
          esac
        done
        exit 0
        """);

    [UnsupportedOSPlatform("windows")]
    private static string Script(TempDirectory directory, string name, string body)
    {
        var path = directory.File(name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>The process whose id the file holds is gone (it is killed here if not, so no test leaves one behind).</summary>
    private static void AssertStopped(string pidFile)
    {
        var id = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (process.HasExited)
                {
                    return;
                }

                // A killed child can linger as a zombie until its parent is reaped; that is stopped too.
                if (File.Exists($"/proc/{id}/stat") && File.ReadAllText($"/proc/{id}/stat").Split(' ')[2] == "Z")
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            Thread.Sleep(50);
        }

        try
        {
            Process.GetProcessById(id).Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
        }

        Assert.Fail($"process {id} was still running");
    }
}
