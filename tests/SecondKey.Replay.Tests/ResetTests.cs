using SecondKey.Artifacts.Runs;
using SecondKey.Capture.Tests;
using SecondKey.Replay.Resets;

namespace SecondKey.Replay.Tests;

public class ResetTests
{
    [Fact]
    public async Task No_reset_always_succeeds_instantly()
    {
        var result = await NoReset.Instance.ResetAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(ResetMethod.None, NoReset.Instance.Method);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_command_reset_succeeds_on_exit_code_zero(bool succeeds)
    {
        // The command runs in the platform's shell: sh -c, or cmd.exe /c on Windows.
        var command = succeeds ? "exit 0"
            : OperatingSystem.IsWindows() ? "echo broken 1>&2 & exit 3"
            : "echo broken >&2; exit 3";
        var reset = new CommandReset(command, workingDirectory: null);

        var result = await reset.ResetAsync(CancellationToken.None);

        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(ResetMethod.Command, reset.Method);
        if (!succeeds)
        {
            Assert.Equal($"'{command}' exited 3: broken", result.Detail);
        }
    }

    [Fact]
    public async Task A_command_reset_runs_in_its_working_directory()
    {
        var directory = Directory.CreateTempSubdirectory("sk-reset-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "restore.marker"), "x");

            var check = OperatingSystem.IsWindows() ? "if exist restore.marker (exit 0) else (exit 1)" : "test -f restore.marker";
            var inDirectory = await new CommandReset(check, directory).ResetAsync(CancellationToken.None);
            var elsewhere = await new CommandReset(check, workingDirectory: null).ResetAsync(CancellationToken.None);

            Assert.True(inDirectory.Succeeded, inDirectory.Detail);
            Assert.Null(inDirectory.Detail);
            Assert.False(elsewhere.Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task An_http_reset_succeeds_on_2xx_and_explains_anything_else()
    {
        await using var shop = new SampleShopHost("legacy");
        using var client = new HttpClient();

        var ok = await new HttpReset(client, shop.Address, "POST", "/__sk/reset").ResetAsync(CancellationToken.None);
        var missing = await new HttpReset(client, shop.Address, "POST", "/nope").ResetAsync(CancellationToken.None);
        var down = await new HttpReset(client, new Uri("http://127.0.0.1:9"), "POST", "/__sk/reset").ResetAsync(CancellationToken.None);

        Assert.True(ok.Succeeded);
        Assert.Null(ok.Detail);
        Assert.False(missing.Succeeded);
        Assert.EndsWith("/nope answered 404", missing.Detail, StringComparison.Ordinal);
        Assert.False(down.Succeeded);
        Assert.Contains("failed:", down.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://legacy.test/", "/__sk/reset", "http://legacy.test/__sk/reset")]
    [InlineData("http://legacy.test/shop/", "/__sk/reset", "http://legacy.test/shop/__sk/reset")]
    [InlineData("http://legacy.test/shop", "__sk/reset", "http://legacy.test/shop/__sk/reset")]
    [InlineData("http://legacy.test:8080/shop/", "/__sk/reset?all=true", "http://legacy.test:8080/shop/__sk/reset?all=true")]
    public async Task An_http_reset_is_sent_under_the_base_path_like_every_replayed_request(string baseUrl, string path, string expected)
    {
        var handler = new RecordingHandler();
        using var client = new HttpClient(handler);

        var result = await new HttpReset(client, new Uri(baseUrl), "POST", path).ResetAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, handler.Requested!.AbsoluteUri);
        Assert.Equal(HttpMethod.Post, handler.Method);
    }

    [Fact]
    public void Bad_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpReset(null!, new Uri("http://x"), "POST", "/"));
        Assert.Equal("baseUrl", Assert.Throws<ArgumentNullException>(() => new HttpReset(new HttpClient(), null!, "POST", "/")).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => new HttpReset(new HttpClient(), new Uri("http://x"), "POST", null!)).ParamName);
        Assert.ThrowsAny<ArgumentException>(() => new CommandReset(" ", null));
        Assert.ThrowsAny<ArgumentException>(() => new SqlServerSnapshotReset("", "db", "snap"));
    }

    [Theory]
    [InlineData("Shop", "[Shop]")]
    [InlineData("we]ird", "[we]]ird]")]
    public void Identifiers_are_quoted(string name, string quoted)
    {
        Assert.Equal(quoted, SqlServerSnapshotReset.Quote(name));
        Assert.Equal("N'it''s'", SqlServerSnapshotReset.Literal("it's"));
    }
}

/// <summary>Answers 200 to anything and remembers what was asked, so no socket is involved.</summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    public Uri? Requested { get; private set; }

    public HttpMethod? Method { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requested = request.RequestUri;
        Method = request.Method;
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
