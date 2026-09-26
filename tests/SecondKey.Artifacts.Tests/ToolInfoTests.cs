namespace SecondKey.Artifacts.Tests;

public class ToolInfoTests
{
    [Fact]
    public void Current_names_the_product_and_carries_a_version()
    {
        var current = ToolInfo.Current;

        Assert.Equal("secondkey", current.Name);
        Assert.False(string.IsNullOrWhiteSpace(current.Version));
        Assert.DoesNotContain('+', current.Version);
    }

    [Fact]
    public void Parse_splits_the_commit_off_the_build_metadata()
    {
        var info = ToolInfo.Parse("0.1.0+3f9a2c1");

        Assert.Equal(new ToolInfo("secondkey", "0.1.0", "3f9a2c1"), info);
        Assert.Equal("secondkey 0.1.0 (3f9a2c1)", info.ToString());
    }

    [Fact]
    public void Parse_without_build_metadata_has_no_commit()
    {
        var info = ToolInfo.Parse("0.1.0");

        Assert.Null(info.Commit);
        Assert.Equal("secondkey 0.1.0", info.ToString());
    }

    [Fact]
    public void Parse_with_an_empty_commit_has_no_commit()
    {
        Assert.Null(ToolInfo.Parse("0.1.0+").Commit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_rejects_a_blank_version(string version)
    {
        Assert.ThrowsAny<ArgumentException>(() => ToolInfo.Parse(version));
    }
}
