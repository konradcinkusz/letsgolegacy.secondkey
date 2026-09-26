using SecondKey.Artifacts.Contracts;
using SecondKey.Contract.Rendering;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class ClauseTextTests
{
    [Theory]
    [InlineData("{ select: response.status, op: exists }", "response.status is present")]
    [InlineData("{ select: response.body.json.password, op: absent }", "response.body.json.password is absent")]
    [InlineData("{ select: response.status, op: equals, value: 201 }", "response.status equals 201")]
    [InlineData("{ select: response.body.json.state, op: equals, value: open, ignoreCase: true }", "response.body.json.state equals \"open\" (ignoring case)")]
    [InlineData("{ select: response.status, op: notEquals, value: 500 }", "response.status does not equal 500")]
    [InlineData("{ select: response.body.json.total, op: lt, value: 0 }", "response.body.json.total is less than 0")]
    [InlineData("{ select: response.body.json.total, op: lte, value: 0 }", "response.body.json.total is at most 0")]
    [InlineData("{ select: response.body.json.total, op: gt, value: 0 }", "response.body.json.total is greater than 0")]
    [InlineData("{ select: response.body.json.total, op: gte, value: 0 }", "response.body.json.total is at least 0")]
    [InlineData("{ select: response.body.json.total, op: between, min: 1, max: 9.5 }", "response.body.json.total is between 1 and 9.5")]
    [InlineData("{ select: response.body.json.total, op: approx, value: 10, absolute: 0.01 }", "response.body.json.total is within 0.01 of 10")]
    [InlineData("{ select: response.body.json.total, op: approx, value: 10, relative: 0.05 }", "response.body.json.total is within 5.00% of 10")]
    [InlineData("{ select: \"response.headers.location[0]\", op: matches, value: \"^/orders/\" }", "response.headers.location[0] matches \"^/orders/\"")]
    [InlineData("{ select: response.body.text, op: notMatches, value: \"Exception\" }", "response.body.text does not match \"Exception\"")]
    [InlineData("{ select: response.body.text, op: contains, value: Total }", "response.body.text contains \"Total\"")]
    [InlineData("{ select: response.body.text, op: notContains, value: Stack }", "response.body.text does not contain \"Stack\"")]
    [InlineData("{ select: response.body.json.currency, op: in, value: [PLN, EUR] }", "response.body.json.currency is one of \"PLN\", \"EUR\"")]
    [InlineData("{ select: response.body.json.currency, op: notIn, value: [USD] }", "response.body.json.currency is none of \"USD\"")]
    [InlineData("{ select: \"response.body.json.items[*]\", op: countEquals, value: 2 }", "response.body.json.items[*] has exactly 2 value(s)")]
    [InlineData("{ select: \"response.body.json.items[*]\", op: countAtLeast, value: 1 }", "response.body.json.items[*] has at least 1 value(s)")]
    [InlineData("{ select: \"response.body.json.items[*]\", op: countAtMost, value: 9 }", "response.body.json.items[*] has at most 9 value(s)")]
    [InlineData("{ select: \"response.body.json.items[*].price\", op: gt, value: 0 }", "every response.body.json.items[*].price is greater than 0")]
    [InlineData("{ select: \"response.body.json.items[*].price\", op: gt, value: 0, quantifier: any }", "some response.body.json.items[*].price is greater than 0")]
    [InlineData("{ select: \"response.body.json.items[*].price\", op: lt, value: 0, quantifier: none }", "no response.body.json.items[*].price is less than 0")]
    [InlineData("{ select: \"response.body.json.**.cardNumber\", op: exists }", "response.body.json.**.cardNumber is present")]
    [InlineData("{ select: extract.cartTotal, op: equals, ref: response.body.json.total }", "extract.cartTotal equals the value of response.body.json.total")]
    public void Every_operator_reads_as_a_sentence(string yaml, string expected)
    {
        Assert.Equal(expected, ClauseText.Predicate(Predicate(yaml)));
    }

    [Theory]
    [InlineData(null, "For every request")]
    [InlineData("{ method: POST, path: \"^/checkout$\" }", "For POST /checkout")]
    [InlineData("{ method: [GET, HEAD] }", "For every GET or HEAD request")]
    [InlineData("{ path: \"^/cart$\" }", "For requests to /cart")]
    [InlineData("{ path: \"^/products/[0-9]+$\" }", "For requests to paths matching ^/products/[0-9]+$")]
    [InlineData("{ method: GET, path: \"^/products/[0-9]+$\" }", "For GET requests to paths matching ^/products/[0-9]+$")]
    [InlineData("{ path: \"^/cart$\", query: { page: \"^[0-9]+$\" } }", "For requests to /cart with query page matching ^[0-9]+$")]
    public void The_scope_reads_as_a_phrase(string? when, string expected)
    {
        var match = when is null ? null : System.Text.Json.JsonSerializer.Deserialize<RequestMatch>(Artifacts.Yaml.YamlJson.Convert("m: " + when)!["m"]!.ToJsonString(), Artifacts.Json.ArtifactJson.Document);

        Assert.Equal(expected, ClauseText.Scope(match));
    }

    [Fact]
    public void A_must_clause_joins_its_assertions_with_and()
    {
        var clause = SampleContract().Document.Clauses.Single(c => c.Id == "ORDER-HAS-LOCATION");

        Assert.Equal(
            "For POST /checkout: response.status equals 201, and response.headers.location[0] matches \"^/orders/[0-9a-f-]{36}$\".",
            ClauseText.Render(clause));
    }

    [Fact]
    public void A_never_clause_says_what_never_happens()
    {
        var clause = SampleContract().Document.Clauses.Single(c => c.Id == "TOTAL-NEVER-NEGATIVE");

        Assert.Equal("For every request: it never happens that response.body.json.total is less than 0.", ClauseText.Render(clause));
    }

    [Fact]
    public void Rendering_null_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ClauseText.Render(null!));
        Assert.Throws<ArgumentNullException>(() => ClauseText.Predicate(null!));
    }
}
