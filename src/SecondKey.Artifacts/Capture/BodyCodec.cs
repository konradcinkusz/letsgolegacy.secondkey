using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SecondKey.Artifacts.Capture;

/// <summary>
/// Turns raw body bytes into a <see cref="BodyRecord"/> and back, the same way for capture
/// and replay: JSON is parsed, text is decoded with its declared charset (legacy systems
/// often speak windows-1250 or ISO-8859-2), anything else is base64.
/// </summary>
public static class BodyCodec
{
    static BodyCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Records a body; null when there is none.</summary>
    /// <param name="bytes">What was read — possibly only the first part of the body.</param>
    /// <param name="contentType">The Content-Type header, if any.</param>
    /// <param name="totalSize">The body's full size in bytes.</param>
    /// <param name="truncated">True when <paramref name="bytes"/> is only the start of the body.</param>
    public static BodyRecord? Encode(ReadOnlySpan<byte> bytes, string? contentType, long totalSize, bool truncated)
    {
        if (totalSize == 0 && bytes.Length == 0)
        {
            return null;
        }

        var mediaType = MediaType(contentType);
        if (!truncated && IsJson(mediaType))
        {
            try
            {
                return BodyRecord.FromJson(JsonNode.Parse(bytes), contentType, totalSize);
            }
            catch (JsonException)
            {
                // Declared JSON that is not JSON is recorded as the text it is.
            }
        }

        if (IsText(mediaType) || IsJson(mediaType))
        {
            var encoding = Charset(contentType) ?? Encoding.UTF8;
            return BodyRecord.FromText(encoding.GetString(bytes), contentType, totalSize, truncated);
        }

        return BodyRecord.FromBytes(bytes, contentType, totalSize, truncated);
    }

    /// <summary>The bytes to send for a recorded body — what replay puts on the wire.</summary>
    public static byte[] Decode(BodyRecord? body)
    {
        if (body is null)
        {
            return [];
        }

        return body.Encoding switch
        {
            BodyEncoding.Json => Encoding.UTF8.GetBytes(body.Json?.ToJsonString() ?? "null"),
            BodyEncoding.Text => (Charset(body.ContentType) ?? Encoding.UTF8).GetBytes(body.Text ?? string.Empty),
            BodyEncoding.Base64 => Convert.FromBase64String(body.Base64 ?? string.Empty),
            _ => throw new ArgumentOutOfRangeException(nameof(body), body.Encoding, "unknown body encoding"),
        };
    }

    /// <summary>The media type of a Content-Type header, lower case, without parameters.</summary>
    public static string? MediaType(string? contentType) =>
        contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            ? parsed.MediaType?.ToLowerInvariant()
            : null;

    public static bool IsHtml(string? contentType) => MediaType(contentType) is "text/html" or "application/xhtml+xml";

    private static bool IsJson(string? mediaType) =>
        mediaType is not null && (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal));

    private static bool IsText(string? mediaType) =>
        mediaType is not null
        && (mediaType.StartsWith("text/", StringComparison.Ordinal)
            || mediaType is "application/xml" or "application/javascript" or "application/x-javascript" or "application/x-www-form-urlencoded" or "application/soap+xml"
            || mediaType.EndsWith("+xml", StringComparison.Ordinal));

    private static Encoding? Charset(string? contentType)
    {
        if (contentType is null || !MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.CharSet is not { Length: > 0 } charset)
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
