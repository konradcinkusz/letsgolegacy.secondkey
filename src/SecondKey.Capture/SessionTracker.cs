using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SecondKey.Capture;

/// <summary>
/// Assigns each exchange to a user session by the value of a configured cookie or header —
/// stored only as a short digest, so the recording never carries the session secret. A
/// request with no session value that is answered with a new session cookie starts that
/// session; anything else anonymous is a session of its own.
/// </summary>
public sealed class SessionTracker
{
    private readonly IReadOnlyList<string> _keys;
    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal);

    public SessionTracker(IReadOnlyList<string> keys) => _keys = keys;

    /// <summary>The session of a request, or null when it carries none of the keys.</summary>
    public string? FromRequest(IReadOnlyDictionary<string, string[]> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var key in _keys)
        {
            if (Cookie(headers, key) is { } cookie)
            {
                return Id(key, cookie);
            }

            if (Header(headers, key) is { } header)
            {
                return Id(key, header);
            }
        }

        return null;
    }

    /// <summary>The session a response starts by setting one of the session cookies, or null.</summary>
    public string? FromResponse(IReadOnlyDictionary<string, string[]> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!headers.TryGetValue("set-cookie", out var cookies))
        {
            return null;
        }

        foreach (var setCookie in cookies)
        {
            var nameValue = setCookie.Split(';', 2)[0];
            var equals = nameValue.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = nameValue[..equals].Trim();
            var key = _keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key is not null)
            {
                return Id(key, nameValue[(equals + 1)..].Trim());
            }
        }

        return null;
    }

    private string Id(string key, string value) =>
        _known.GetOrAdd($"{key}\n{value}", combined =>
            "s-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(combined)))[..12]);

    private static string? Cookie(IReadOnlyDictionary<string, string[]> headers, string name)
    {
        if (!headers.TryGetValue("cookie", out var values))
        {
            return null;
        }

        foreach (var pair in values.SelectMany(v => v.Split(';')))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && string.Equals(pair[..equals].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                var value = pair[(equals + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static string? Header(IReadOnlyDictionary<string, string[]> headers, string name) =>
        headers.TryGetValue(name.ToLower(CultureInfo.InvariantCulture), out var values) && values.Length > 0 && values[0].Length > 0 ? values[0] : null;
}
