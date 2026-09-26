using System.Text;
using SecondKey.Artifacts.Capture;

namespace SecondKey.Artifacts.Tests;

public class BodyCodecTests
{
    [Fact]
    public void No_bytes_is_no_body()
    {
        Assert.Null(BodyCodec.Encode([], "application/json", 0, truncated: false));
    }

    [Fact]
    public void Json_is_parsed_and_sent_back_as_json()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"a\":[1,2]}");

        var body = BodyCodec.Encode(bytes, "application/json; charset=utf-8", bytes.Length, truncated: false)!;

        Assert.Equal(BodyEncoding.Json, body.Encoding);
        Assert.Equal(bytes.Length, body.Size);
        Assert.Equal("{\"a\":[1,2]}", Encoding.UTF8.GetString(BodyCodec.Decode(body)));
    }

    [Theory]
    [InlineData("application/problem+json")]
    [InlineData("application/vnd.api+json")]
    public void Structured_json_types_are_json(string contentType)
    {
        Assert.Equal(BodyEncoding.Json, BodyCodec.Encode("{}"u8, contentType, 2, truncated: false)!.Encoding);
    }

    [Fact]
    public void Declared_json_that_is_not_json_is_kept_as_text()
    {
        var body = BodyCodec.Encode("not json"u8, "application/json", 8, truncated: false)!;

        Assert.Equal(BodyEncoding.Text, body.Encoding);
        Assert.Equal("not json", body.Text);
    }

    [Fact]
    public void Truncated_json_is_kept_as_the_text_it_is()
    {
        var body = BodyCodec.Encode("{\"a\":"u8, "application/json", 100, truncated: true)!;

        Assert.Equal(BodyEncoding.Text, body.Encoding);
        Assert.True(body.Truncated);
        Assert.Equal(100, body.Size);
    }

    [Theory]
    [InlineData("text/html; charset=utf-8")]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData("application/soap+xml")]
    [InlineData("image/svg+xml")]
    [InlineData("application/javascript")]
    [InlineData("application/x-javascript")]
    [InlineData("application/x-www-form-urlencoded")]
    public void Textual_types_are_text(string contentType)
    {
        Assert.Equal(BodyEncoding.Text, BodyCodec.Encode("x"u8, contentType, 1, truncated: false)!.Encoding);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("image/png")]
    [InlineData(null)]
    [InlineData("not a content type")]
    public void Anything_else_is_base64(string? contentType)
    {
        var body = BodyCodec.Encode(new byte[] { 0, 255 }, contentType, 2, truncated: false)!;

        Assert.Equal(BodyEncoding.Base64, body.Encoding);
        Assert.Equal(new byte[] { 0, 255 }, BodyCodec.Decode(body));
    }

    [Fact]
    public void A_legacy_charset_is_decoded_and_encoded_faithfully()
    {
        var windows1250 = CodePagesEncoding("windows-1250");
        var bytes = windows1250.GetBytes("Zażółć gęślą jaźń");

        var body = BodyCodec.Encode(bytes, "text/html; charset=windows-1250", bytes.Length, truncated: false)!;

        Assert.Equal("Zażółć gęślą jaźń", body.Text);
        Assert.Equal(bytes, BodyCodec.Decode(body));
    }

    [Fact]
    public void An_unknown_charset_falls_back_to_utf8()
    {
        var body = BodyCodec.Encode("é"u8, "text/plain; charset=no-such-charset", 2, truncated: false)!;

        Assert.Equal("é", body.Text);
        Assert.Equal("é"u8.ToArray(), BodyCodec.Decode(body));
    }

    [Fact]
    public void A_quoted_charset_is_understood()
    {
        var body = BodyCodec.Encode("é"u8, "text/plain; charset=\"utf-8\"", 2, truncated: false)!;

        Assert.Equal("é", body.Text);
    }

    [Theory]
    [InlineData("text/html", true)]
    [InlineData("TEXT/HTML; charset=utf-8", true)]
    [InlineData("application/xhtml+xml", true)]
    [InlineData("application/json", false)]
    [InlineData(null, false)]
    public void Html_is_recognised(string? contentType, bool expected)
    {
        Assert.Equal(expected, BodyCodec.IsHtml(contentType));
    }

    [Fact]
    public void Media_types_are_lower_case_without_parameters()
    {
        Assert.Equal("text/html", BodyCodec.MediaType("Text/HTML; charset=UTF-8"));
        Assert.Null(BodyCodec.MediaType(null));
    }

    [Fact]
    public void Decoding_no_body_is_no_bytes_and_a_null_json_body_is_null()
    {
        Assert.Empty(BodyCodec.Decode(null));
        Assert.Equal("null", Encoding.UTF8.GetString(BodyCodec.Decode(new BodyRecord { Encoding = BodyEncoding.Json })));
        Assert.Empty(BodyCodec.Decode(new BodyRecord { Encoding = BodyEncoding.Text }));
        Assert.Empty(BodyCodec.Decode(new BodyRecord { Encoding = BodyEncoding.Base64 }));
    }

    private static Encoding CodePagesEncoding(string name)
    {
        _ = BodyCodec.MediaType("text/plain"); // the codec registers the code-page provider
        _ = BodyCodec.Encode("x"u8, "text/plain", 1, truncated: false);
        return Encoding.GetEncoding(name);
    }
}
