using static SecondKey.Cli.Tests.CliRunner;

namespace SecondKey.Cli.Tests;

public class ValidateCommandTests
{
    [Fact]
    public async Task Every_sample_artifact_validates()
    {
        var (exit, output, _) = await RunAsync("validate", Sample("sample.skcap"), Sample("contract.yaml"), Sample("sample.skrun"), Sample("verdict.json"));

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(4, output.Split(": valid", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public async Task A_broken_artifact_is_invalid_input_and_its_errors_are_printed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skcap");
        await File.WriteAllLinesAsync(path, File.ReadAllLines(Sample("sample.skcap")).Skip(1));
        try
        {
            var (exit, output, _) = await RunAsync("validate", path);

            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains("line 1 /type: the first line of a capture must be its capture.header", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("notes.txt", "hello", "not a Second Key artifact")]
    [InlineData("other.json", "{\"kind\":\"something-else\"}", "not a Second Key artifact")]
    [InlineData("verdict.json", "{ broken", "not valid JSON")]
    [InlineData("contract.yml", "apiVersion: secondkey/v1\n", "Required properties")]
    public async Task Anything_that_is_not_a_valid_artifact_is_reported(string name, string content, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("sk-").FullName;
        var path = Path.Combine(directory, name);
        await File.WriteAllTextAsync(path, content);
        try
        {
            var (exit, output, _) = await RunAsync("validate", path);

            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains(expected, output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_file_is_reported()
    {
        var (exit, output, _) = await RunAsync("validate", Path.Combine(Path.GetTempPath(), "missing.skrun"));

        Assert.Equal(ExitCodes.InvalidInput, exit);
        Assert.Contains("no such file", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configuration_file_is_validated_as_configuration()
    {
        var directory = Directory.CreateTempSubdirectory("sk-").FullName;
        var good = Path.Combine(directory, "secondkey.yaml");
        await File.WriteAllTextAsync(good, "version: 1\nreplay:\n  scenarioMode: session\n");
        try
        {
            Assert.Equal(ExitCodes.Success, (await RunAsync("validate", good)).Exit);

            await File.WriteAllTextAsync(good, "version: 1\nreplay:\n  scenarioModes: session\n");
            var (exit, output, _) = await RunAsync("validate", good);
            Assert.Equal(ExitCodes.InvalidInput, exit);
            Assert.Contains("scenarioModes", output, StringComparison.Ordinal);

            // A value the key's type cannot constrain is checked when the file is read, so
            // validate refuses it exactly as the command that would use it does.
            await File.WriteAllTextAsync(good, "version: 1\nreplay:\n  scenarioMode: exchanges\n");
            var (badValue, badOutput, _) = await RunAsync("validate", good);
            Assert.Equal(ExitCodes.InvalidInput, badValue);
            Assert.Contains("replay.scenarioMode: 'exchanges' is not one of session, exchange", badOutput, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Validate_needs_at_least_one_file()
    {
        Assert.Equal(ExitCodes.Usage, (await RunAsync("validate")).Exit);
    }
}
