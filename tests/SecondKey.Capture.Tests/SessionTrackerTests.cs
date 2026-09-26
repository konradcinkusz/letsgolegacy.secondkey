namespace SecondKey.Capture.Tests;

public class SessionTrackerTests
{
    private static Dictionary<string, string[]> H(params (string Name, string Value)[] headers) =>
        headers.GroupBy(h => h.Name).ToDictionary(g => g.Key, g => g.Select(h => h.Value).ToArray());

    [Fact]
    public void A_session_cookie_names_a_stable_session_without_revealing_its_value()
    {
        var tracker = new SessionTracker(["ASP.NET_SessionId"]);

        var first = tracker.FromRequest(H(("cookie", "theme=dark; ASP.NET_SessionId=abc123")));
        var again = tracker.FromRequest(H(("cookie", "ASP.NET_SessionId=abc123")));
        var other = tracker.FromRequest(H(("cookie", "ASP.NET_SessionId=zzz999")));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.Matches("^s-[0-9a-f]{12}$", first);
        Assert.DoesNotContain("abc123", first, StringComparison.Ordinal);
    }

    [Fact]
    public void A_header_can_be_the_session_key()
    {
        var tracker = new SessionTracker(["x-session"]);

        Assert.NotNull(tracker.FromRequest(H(("x-session", "t-1"))));
        Assert.Null(tracker.FromRequest(H(("x-session", ""))));
    }

    [Fact]
    public void A_request_without_the_key_has_no_session_and_a_response_can_start_one()
    {
        var tracker = new SessionTracker(["sk_session"]);

        Assert.Null(tracker.FromRequest(H(("cookie", "other=1"))));
        Assert.Null(tracker.FromRequest(H(("cookie", "sk_session="))));
        var started = tracker.FromResponse(H(("set-cookie", "sk_session=42; path=/; httponly")));
        Assert.Equal(started, tracker.FromRequest(H(("cookie", "sk_session=42"))));
    }

    [Fact]
    public void A_response_setting_other_cookies_starts_nothing()
    {
        var tracker = new SessionTracker(["sk_session"]);

        Assert.Null(tracker.FromResponse(H(("set-cookie", "theme=dark"), ("set-cookie", "=broken"))));
        Assert.Null(tracker.FromResponse(H()));
    }

    [Fact]
    public void Null_headers_are_refused()
    {
        var tracker = new SessionTracker([]);

        Assert.Throws<ArgumentNullException>(() => tracker.FromRequest(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.FromResponse(null!));
    }
}
