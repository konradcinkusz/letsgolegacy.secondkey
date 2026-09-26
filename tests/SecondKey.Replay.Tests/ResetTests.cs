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
    [InlineData("exit 0", true)]
    [InlineData("echo broken >&2; exit 3", false)]
    public async Task A_command_reset_succeeds_on_exit_code_zero(string command, bool succeeds)
    {
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

            var inDirectory = await new CommandReset("test -f restore.marker", directory).ResetAsync(CancellationToken.None);
            var elsewhere = await new CommandReset("test -f restore.marker", workingDirectory: null).ResetAsync(CancellationToken.None);

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

    [Fact]
    public void Bad_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new HttpReset(null!, new Uri("http://x"), "POST", "/"));
        Assert.Equal("baseUrl", Assert.Throws<ArgumentNullException>(() => new HttpReset(new HttpClient(), null!, "POST", "/")).ParamName);
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
