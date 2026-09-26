using System.Text.Json.Nodes;
using SecondKey.Artifacts.Paths;

namespace SecondKey.Artifacts.Tests;

public class SelectorPathTests
{
    private static readonly JsonNode Observation = JsonNode.Parse("""
        {
          "request": { "method": "GET", "path": "/cart" },
          "response": {
            "status": 200,
            "headers": { "content-type": ["text/html"], "location": ["/orders/1"] },
            "body": { "json": {
              "total": 12.5,
              "items": [ { "sku": "A", "price": 1.5, "card": { "cardNumber": "4111" } }, { "sku": "B", "price": 11, "note": null } ],
              "cardNumber": "top"
            } }
          },
          "db": { "dbo.Orders": { "inserted": [ { "Id": 7 } ] } },
          "outbound": [ { "url": "https://pay.example/charge" } ]
        }
        """)!;

    [Theory]
    [InlineData("response.status", "[200]")]
    [InlineData("response.headers.content-type[0]", "[\"text/html\"]")]
    [InlineData("response.body.json.items[*].price", "[1.5,11]")]
    [InlineData("response.body.json.items[1].sku", "[\"B\"]")]
    [InlineData("response.body.json.items[5].sku", "[]")]
    [InlineData("response.body.json.missing", "[]")]
    [InlineData("response.body.json.items[1].note", "[null]")]
    [InlineData("response.body.json.**.cardNumber", "[\"top\",\"4111\"]")]
    [InlineData("db[\"dbo.Orders\"].inserted[0].Id", "[7]")]
    [InlineData("outbound[*].url", "[\"https://pay.example/charge\"]")]
    [InlineData("response.headers[*][0]", "[\"text/html\",\"/orders/1\"]")]
    public void A_path_selects_exactly_what_it_names(string path, string expected)
    {
        var values = SelectorPath.Parse(path).Select(Observation);

        Assert.Equal(expected, new JsonArray(values.Select(v => v?.DeepClone()).ToArray()).ToJsonString());
    }

    [Theory]
    [InlineData("", "starts with one of")]
    [InlineData("body.total", "starts with one of")]
    [InlineData("response..status", "expected a name")]
    [InlineData("response.items[", "unterminated")]
    [InlineData("response.items[x]", "expected an index")]
    [InlineData("response.items[1", "expected ']'")]
    [InlineData("response.**", "must be followed by a segment")]
    [InlineData("response[\"a", "unterminated quoted name")]
    [InlineData("response.[\"a\"]", "expected a name")]
    [InlineData("response status", "unexpected ' '")]
    public void A_malformed_path_is_refused_with_a_reason(string path, string reason)
    {
        var ok = SelectorPath.TryParse(path, out var parsed, out var error);

        Assert.False(ok);
        Assert.Null(parsed);
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_name_may_contain_dots_and_escaped_quotes()
    {
        var path = SelectorPath.Parse("db[\"dbo.\\\"Odd\\\".Name\"]");

        Assert.Equal(new PropertySegment("dbo.\"Odd\".Name"), path.Segments[1]);
    }

    [Theory]
    [InlineData("response.body.json.items[*].price", false, true)]
    [InlineData("response.body.json.**.cardNumber", false, true)]
    [InlineData("response.status", true, false)]
    public void Multi_valued_paths_are_recognised(string text, bool single, bool multi)
    {
        var path = SelectorPath.Parse(text);

        Assert.Equal(multi, path.IsMultiValued);
        Assert.NotEqual(single, path.IsMultiValued);
        Assert.Equal("response", path.Root);
    }

    [Theory]
    [InlineData("response.body.json.items[*].price", "response.body.json.items[3].price", true)]
    [InlineData("response.body.json.items[*].price", "response.body.json.items[3].sku", false)]
    [InlineData("response.body.json.**.createdAt", "response.body.json.order.lines[2].createdAt", true)]
    [InlineData("response.body.json.**.createdAt", "response.body.json.createdAt", true)]
    [InlineData("response.body.json.total", "response.body.json.total", true)]
    [InlineData("response.body.json.total", "response.body.json", false)]
    [InlineData("response.headers[*]", "response.headers.date", true)]
    public void A_path_matches_the_concrete_locations_it_would_select(string pattern, string concrete, bool expected)
    {
        var concreteSegments = SelectorPath.Parse(concrete).Segments;

        Assert.Equal(expected, SelectorPath.Parse(pattern).Matches(concreteSegments));
    }

    [Fact]
    public void Formatting_round_trips_concrete_segments_and_quotes_when_needed()
    {
        var segments = new PathSegment[] { new PropertySegment("db"), new PropertySegment("dbo.Orders"), new IndexSegment(2), new PropertySegment("content-type") };

        var text = SelectorPath.Format(segments);

        Assert.Equal("db[\"dbo.Orders\"][2].content-type", text);
        Assert.Equal(segments, SelectorPath.Parse(text).Segments);
    }

    [Fact]
    public void Paths_compare_by_their_text()
    {
        Assert.Equal(SelectorPath.Parse("response.status"), SelectorPath.Parse("response.status"));
        Assert.NotEqual(SelectorPath.Parse("response.status"), SelectorPath.Parse("request.method"));
        Assert.Equal("response.status", SelectorPath.Parse("response.status").ToString());
    }

    [Fact]
    public void Formatting_writes_wildcards_and_descent()
    {
        var text = SelectorPath.Format(SelectorPath.Parse("response.body.json.**.items[*].x").Segments);

        Assert.Equal("response.body.json.**.items[*].x", text);
    }

    [Fact]
    public void Formatting_quotes_an_empty_name_and_escapes_quotes_and_backslashes()
    {
        var text = SelectorPath.Format([new PropertySegment("db"), new PropertySegment(""), new PropertySegment("a\\b\"c")]);

        Assert.Equal("db[\"\"][\"a\\\\b\\\"c\"]", text);
        Assert.Equal(new PropertySegment("a\\b\"c"), SelectorPath.Parse(text).Segments[2]);
    }

    [Theory]
    [InlineData("response.body.json.items[2].price", "response.body.json.items[2].price", true)]
    [InlineData("response.body.json.items[2].price", "response.body.json.items[3].price", false)]
    [InlineData("response.body.json.items[2]", "response.body.json.items.x", false)]
    [InlineData("response.body.json.**.a", "response.body.json", false)]
    [InlineData("response.body", "response.body.json", false)]
    public void Matching_compares_indexes_and_lengths_exactly(string pattern, string concrete, bool expected)
    {
        Assert.Equal(expected, SelectorPath.Parse(pattern).Matches(SelectorPath.Parse(concrete).Segments));
    }

    [Fact]
    public void An_index_past_the_end_selects_nothing_and_the_last_index_selects_the_last()
    {
        var node = JsonNode.Parse("""{"response":{"a":[1,2,3]}}""");

        Assert.Empty(SelectorPath.Parse("response.a[3]").Select(node));
        Assert.Equal(3, SelectorPath.Parse("response.a[2]").Select(node).Single()!.GetValue<int>());
    }

    [Fact]
    public void A_wildcard_over_a_scalar_selects_nothing()
    {
        var node = JsonNode.Parse("""{"response":{"status":200}}""");

        Assert.Empty(SelectorPath.Parse("response.status[*]").Select(node));
        Assert.Empty(SelectorPath.Parse("response.status.x").Select(node));
    }

    [Fact]
    public void The_roots_are_listed_in_order_when_a_path_starts_elsewhere()
    {
        SelectorPath.TryParse("body", out _, out var error);

        Assert.Equal("'body': a path starts with one of db, extract, outbound, request, response", error);
    }

    [Fact]
    public void Parsing_null_is_an_argument_error()
    {
        Assert.Throws<ArgumentNullException>(() => SelectorPath.Parse(null!));
    }

    [Fact]
    public void Paths_are_usable_as_dictionary_keys()
    {
        var set = new HashSet<SelectorPath> { SelectorPath.Parse("response.status"), SelectorPath.Parse("response.status") };

        Assert.Single(set);
        Assert.False(SelectorPath.Parse("response.status").Equals((object)"response.status"));
    }
}
