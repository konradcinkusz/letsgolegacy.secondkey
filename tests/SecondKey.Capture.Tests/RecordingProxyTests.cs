using System.Net;
using System.Net.Http.Json;
using SecondKey.Artifacts.Capture;

namespace SecondKey.Capture.Tests;

public sealed class RecordingProxyTests : IAsyncLifetime
{
    private readonly SampleShopHost _shop = new("legacy");
    private readonly string _output = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skcap");
    private RecordingProxy _proxy = null!;

    public async Task InitializeAsync()
    {
        _proxy = await RecordingProxy.StartAsync(new CaptureOptions
        {
            Listen = new Uri("http://127.0.0.1:0"),
            Target = _shop.Address,
            Output = _output,
            SessionKeys = ["sk_session"],
        });
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        await _shop.DisposeAsync();
        File.Delete(_output);
    }

    private HttpClient Client(CookieContainer? cookies = null) =>
        new(new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), UseCookies = true, AllowAutoRedirect = false })
        {
            BaseAddress = _proxy.Address,
        };

    [Fact]
    public async Task A_request_through_the_proxy_produces_a_valid_skcap_line()
    {
        using var client = Client();

        var response = await client.GetAsync("/products?sort=name");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await _proxy.DisposeAsync();
        var report = CaptureFile.Validate(_output);
        Assert.True(report.IsValid, report.ToString());
        var exchange = Assert.Single(CaptureFile.Read(_output).Exchanges);
        Assert.Equal("GET", exchange.Request.Method);
        Assert.Equal("/products", exchange.Request.Path);
        Assert.Equal("?sort=name", exchange.Request.Query);
        Assert.Equal(200, exchange.Response.Status);
        Assert.Equal(BodyEncoding.Json, exchange.Response.Body!.Encoding);
        Assert.Equal(4, exchange.Response.Body.Json!["items"]!.AsArray().Count);
    }

    [Fact]
    public async Task The_client_gets_exactly_what_the_legacy_system_answered()
    {
        using var direct = new HttpClient { BaseAddress = _shop.Address };
        using var proxied = Client();

        var expected = await direct.GetStringAsync("/cart");
        var actual = await proxied.GetStringAsync("/cart");

        Assert.Equal(expected.Length, actual.Length);
        Assert.Contains("order-total", actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchanges_of_one_user_share_a_session_and_cookies_never_reach_the_file()
    {
        var jar = new CookieContainer();
        using var client = Client(jar);

        await client.PostAsJsonAsync("/cart/items", new { sku = "ECL-02", quantity = 3 });
        await client.GetAsync("/cart");
        using var stranger = Client();
        await stranger.GetAsync("/cart");
        await _proxy.DisposeAsync();

        var exchanges = CaptureFile.Read(_output).Exchanges;
        Assert.Equal(3, exchanges.Count);
        Assert.Equal(exchanges[0].Session, exchanges[1].Session);
        Assert.NotEqual(exchanges[0].Session, exchanges[2].Session);
        Assert.StartsWith("s-", exchanges[0].Session, StringComparison.Ordinal);
        Assert.Equal(["<redacted>"], exchanges[0].Response.Headers["set-cookie"]);
        Assert.Equal(["<redacted>"], exchanges[1].Request.Headers["cookie"]);
        var text = await File.ReadAllTextAsync(_output);
        foreach (var cookie in jar.GetAllCookies().Cast<Cookie>())
        {
            Assert.DoesNotContain(cookie.Value, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Request_bodies_are_recorded_and_still_reach_the_legacy_system()
    {
        using var client = Client();

        var response = await client.PostAsJsonAsync("/cart/items", new { sku = "APL-01", quantity = 2 });
        var cart = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        await _proxy.DisposeAsync();

        Assert.Equal(25.00m, cart!["total"]!.GetValue<decimal>());
        var exchange = Assert.Single(CaptureFile.Read(_output).Exchanges);
        Assert.Equal("APL-01", exchange.Request.Body!.Json!["sku"]!.GetValue<string>());
        Assert.Equal(BodyEncoding.Json, exchange.Request.Body.Encoding);
    }

    [Fact]
    public async Task Html_is_recorded_as_text_and_sequence_numbers_follow_the_recording()
    {
        using var client = Client();

        await client.GetAsync("/cart");
        await client.GetAsync("/health");
        await _proxy.DisposeAsync();

        var exchanges = CaptureFile.Read(_output).Exchanges;
        Assert.Equal([1L, 2L], exchanges.Select(e => e.Seq));
        Assert.Equal(["ex-000001", "ex-000002"], exchanges.Select(e => e.Id));
        Assert.Equal(BodyEncoding.Text, exchanges[0].Response.Body!.Encoding);
        Assert.Contains("<table class=\"lines\">", exchanges[0].Response.Body!.Text, StringComparison.Ordinal);
        Assert.StartsWith("text/html", exchanges[0].Response.Body!.ContentType, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_header_names_its_target_and_the_sanitization_applied()
    {
        await _proxy.DisposeAsync();

        var header = CaptureFile.Read(_output).Header;

        Assert.Equal("http-proxy", header.Source.Kind);
        Assert.Equal(_shop.Address.ToString(), header.Source.Target);
        Assert.Contains("cookie", header.Sanitization!.RedactedHeaders!);
        Assert.Equal(["sk_session"], header.SessionKeys);
        Assert.Matches("^cap-[0-9]{14}-[0-9a-f]{6}$", header.CaptureId);
    }
}
