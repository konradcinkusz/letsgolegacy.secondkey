using System.Text.Json.Nodes;
using SecondKey.Contract.Predicates;

namespace SecondKey.Contract.Tests;

public class JsonValuesTests
{
    [Theory]
    [InlineData("1", "1.0", true)]
    [InlineData("1", "2", false)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("\"a\"", "\"A\"", false)]
    [InlineData("true", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("\"1\"", "1", false)]
    [InlineData("{\"a\":1,\"b\":[1,2]}", "{\"b\":[1,2.0],\"a\":1.00}", true)]
    [InlineData("{\"a\":1}", "{\"a\":1,\"b\":2}", false)]
    [InlineData("{\"a\":1,\"b\":2}", "{\"a\":1,\"c\":2}", false)]
    [InlineData("{\"a\":1,\"b\":2}", "{\"a\":1,\"b\":3}", false)]
    [InlineData("[1,2]", "[1,2]", true)]
    [InlineData("[1,2]", "[2,1]", false)]
    [InlineData("[1,2]", "[1,2,3]", false)]
    [InlineData("[1,2]", "[1,3]", false)]
    [InlineData("[]", "{}", false)]
    public void Equality_is_structural_and_numeric(string a, string b, bool expected)
    {
        Assert.Equal(expected, JsonValues.Equal(JsonNode.Parse(a), JsonNode.Parse(b)));
        Assert.Equal(expected, JsonValues.Equal(JsonNode.Parse(b), JsonNode.Parse(a)));
    }

    [Fact]
    public void Nulls_equal_only_nulls()
    {
        Assert.True(JsonValues.Equal(null, null));
        Assert.False(JsonValues.Equal(null, JsonValue.Create(0)));
        Assert.False(JsonValues.Equal(JsonValue.Create(0), null));
    }

    [Fact]
    public void Case_is_ignored_only_when_asked_and_reaches_into_structures()
    {
        Assert.True(JsonValues.Equal(JsonNode.Parse("{\"s\":[\"ABC\"]}"), JsonNode.Parse("{\"s\":[\"abc\"]}"), ignoreCase: true));
        Assert.False(JsonValues.Equal(JsonNode.Parse("{\"s\":[\"ABC\"]}"), JsonNode.Parse("{\"s\":[\"abc\"]}")));
    }

    [Theory]
    [InlineData("12.5", 12.5)]
    [InlineData("-3", -3)]
    [InlineData("1e2", 100)]
    public void Numbers_read_as_decimals(string json, double expected)
    {
        Assert.True(JsonValues.TryGetNumber(JsonNode.Parse(json), out var number));
        Assert.Equal((decimal)expected, number);
    }

    [Fact]
    public void Numbers_built_in_code_read_as_decimals_too()
    {
        Assert.True(JsonValues.TryGetNumber(JsonValue.Create(0.25d), out var fromDouble));
        Assert.Equal(0.25m, fromDouble);
        Assert.True(JsonValues.TryGetNumber(JsonValue.Create(7L), out var fromLong));
        Assert.Equal(7m, fromLong);
        Assert.True(JsonValues.TryGetNumber(JsonValue.Create(12.5m), out var fromDecimal));
        Assert.Equal(12.5m, fromDecimal);
    }

    [Theory]
    [InlineData("\"5\"")]
    [InlineData("true")]
    [InlineData("[1]")]
    [InlineData("null")]
    public void Non_numbers_are_not_numbers(string json)
    {
        Assert.False(JsonValues.TryGetNumber(JsonNode.Parse(json), out _));
    }

    [Theory]
    [InlineData("\"x\"", "x")]
    [InlineData("12.50", "12.50")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    public void Scalars_read_as_text(string json, string expected)
    {
        Assert.Equal(expected, JsonValues.AsText(JsonNode.Parse(json)));
    }

    [Theory]
    [InlineData("[1]")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Structures_and_null_have_no_text(string json)
    {
        Assert.Null(JsonValues.AsText(JsonNode.Parse(json)));
    }

    [Fact]
    public void Rendering_shows_null_and_shortens_long_values_to_eighty_characters()
    {
        Assert.Equal("null", JsonValues.Render(null));
        Assert.Equal("\"short\"", JsonValues.Render(JsonValue.Create("short")));
        var exactly80 = JsonValue.Create(new string('x', 78));
        Assert.Equal(80, JsonValues.Render(exactly80).Length);
        var rendered = JsonValues.Render(JsonValue.Create(new string('y', 200)));
        Assert.Equal(80, rendered.Length);
        Assert.EndsWith("...", rendered, StringComparison.Ordinal);
    }
}
