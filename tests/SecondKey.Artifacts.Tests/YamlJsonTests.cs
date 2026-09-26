using System.Text.Json;
using SecondKey.Artifacts.Yaml;

namespace SecondKey.Artifacts.Tests;

public class YamlJsonTests
{
    [Theory]
    [InlineData("v: 2026-09-26", JsonValueKind.String)]
    [InlineData("v: no", JsonValueKind.String)]
    [InlineData("v: yes", JsonValueKind.String)]
    [InlineData("v: '42'", JsonValueKind.String)]
    [InlineData("v: \"true\"", JsonValueKind.String)]
    [InlineData("v: 42", JsonValueKind.Number)]
    [InlineData("v: -3", JsonValueKind.Number)]
    [InlineData("v: 0.01", JsonValueKind.Number)]
    [InlineData("v: 1e3", JsonValueKind.Number)]
    [InlineData("v: true", JsonValueKind.True)]
    [InlineData("v: False", JsonValueKind.False)]
    [InlineData("v: ~", JsonValueKind.Null)]
    [InlineData("v: null", JsonValueKind.Null)]
    [InlineData("v:", JsonValueKind.Null)]
    public void Scalars_are_typed_by_the_yaml_1_2_core_schema(string yaml, JsonValueKind expected)
    {
        var node = YamlJson.Convert(yaml)!;

        var kind = node["v"] is null ? JsonValueKind.Null : node["v"]!.GetValueKind();
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void Decimals_keep_their_exact_value()
    {
        var node = YamlJson.Convert("v: 0.1")!;

        Assert.Equal(0.1m, node["v"]!.GetValue<decimal>());
    }

    [Fact]
    public void Sequences_and_mappings_nest()
    {
        var node = YamlJson.Convert("a:\n  - { b: 1 }\n  - [2, x]\n")!;

        Assert.Equal("""{"a":[{"b":1},[2,"x"]]}""", node.ToJsonString());
    }

    [Fact]
    public void An_empty_text_is_no_document()
    {
        Assert.Null(YamlJson.Convert(""));
    }

    [Fact]
    public void Two_documents_are_refused()
    {
        var ex = Assert.Throws<YamlConversionException>(() => YamlJson.Convert("a: 1\n---\nb: 2\n"));

        Assert.Contains("expected one YAML document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complex_key_is_refused()
    {
        Assert.Throws<YamlConversionException>(() => YamlJson.Convert("? [a, b]\n: 1\n"));
    }

    [Theory]
    [InlineData("v: NULL")]
    [InlineData("v: Null")]
    public void Every_spelling_of_null_is_null(string yaml)
    {
        Assert.Null(YamlJson.Convert(yaml)!["v"]);
    }

    [Theory]
    [InlineData("v: TRUE", true)]
    [InlineData("v: True", true)]
    [InlineData("v: FALSE", false)]
    [InlineData("v: false", false)]
    public void Every_spelling_of_a_boolean_is_a_boolean(string yaml, bool expected)
    {
        Assert.Equal(expected, YamlJson.Convert(yaml)!["v"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("v: +7", 7)]
    [InlineData("v: 9223372036854775807", long.MaxValue)]
    public void Integers_keep_their_sign_and_range(string yaml, long expected)
    {
        Assert.Equal(expected, YamlJson.Convert(yaml)!["v"]!.GetValue<long>());
    }

    [Fact]
    public void An_integer_too_large_for_a_long_becomes_a_decimal()
    {
        Assert.Equal(9223372036854775808m, YamlJson.Convert("v: 9223372036854775808")!["v"]!.GetValue<decimal>());
    }

    [Theory]
    [InlineData("v: 1.2.3")]
    [InlineData("v: 0x1F")]
    [InlineData("v: .inf")]
    public void Anything_else_stays_a_string(string yaml)
    {
        Assert.Equal(JsonValueKind.String, YamlJson.Convert(yaml)!["v"]!.GetValueKind());
    }

    [Fact]
    public void A_syntax_error_carries_its_position()
    {
        var ex = Assert.Throws<YamlConversionException>(() => YamlJson.Convert("a: 1\nb: 'unterminated\n"));

        Assert.True(ex.Line >= 2, ex.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(ex.Column >= 1);
    }

    [Fact]
    public void A_scanner_failure_without_a_position_is_still_a_conversion_error()
    {
        var ex = Assert.Throws<YamlConversionException>(() => YamlJson.Convert("a: [1\nb: 2\n"));

        Assert.Equal(0, ex.Line);
        Assert.StartsWith("malformed YAML", ex.Message, StringComparison.Ordinal);
    }
}
