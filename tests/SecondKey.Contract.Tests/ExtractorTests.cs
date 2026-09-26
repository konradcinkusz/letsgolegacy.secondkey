using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Contract.Extraction;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class ExtractorTests
{
    private const string Page = """
        <html><body>
          <table class="lines">
            <tr><td class="line-total"> 19,99 </td><td><a class="sku" href="/p/A">A</a></td></tr>
            <tr><td class="line-total">40,00</td><td><a class="sku" href="/p/B">B</a></td></tr>
          </table>
          <p>Total:   <span class="order-total">59,99&nbsp;zł</span></p>
          <p class="flag">true</p>
        </body></html>
        """;

    private static (JsonObject Values, IReadOnlyDictionary<string, string> Errors) Extract(string extractorsYaml, HttpResponseRecord response, string path = "/cart")
    {
        var contract = ContractOf($$"""
            apiVersion: secondkey/v1
            kind: Contract
            metadata: { name: x, revision: 1 }
            extract:
            {{extractorsYaml}}
            clauses:
              - id: C
                kind: never
                title: placeholder
                why: a contract needs one never clause to load at all
                assert: [ { select: response.status, op: gte, value: 500 } ]
                provenance: { origin: designed, status: accepted }
            """);
        return new ExtractorSet(contract.Extract).Extract(Request("GET", path), response);
    }

    [Fact]
    public void Html_text_is_trimmed_collapsed_filtered_and_parsed_in_its_culture()
    {
        var (values, errors) = Extract("""
              - { name: total, from: html, selector: ".order-total", regex: "([0-9,]+)", as: decimal, culture: pl-PL }
            """, HtmlResponse(200, Page));

        Assert.Empty(errors);
        Assert.Equal(59.99m, values["total"]!.GetValue<decimal>());
    }

    [Fact]
    public void All_matches_become_an_array_in_document_order()
    {
        var (values, _) = Extract("""
              - { name: lines, from: html, selector: "td.line-total", as: decimal, culture: pl-PL, all: true }
            """, HtmlResponse(200, Page));

        Assert.Equal("[19.99,40.00]", values["lines"]!.ToJsonString());
    }

    [Fact]
    public void An_attribute_is_read_instead_of_the_text()
    {
        var (values, _) = Extract("""
              - { name: links, from: html, selector: "a.sku", attribute: href, all: true }
            """, HtmlResponse(200, Page));

        Assert.Equal("""["/p/A","/p/B"]""", values["links"]!.ToJsonString());
    }

    [Fact]
    public void A_selector_that_matches_nothing_leaves_the_value_absent()
    {
        var (values, errors) = Extract("""
              - { name: gone, from: html, selector: ".no-such-thing", as: decimal }
            """, HtmlResponse(200, Page));

        Assert.False(values.ContainsKey("gone"));
        Assert.Empty(errors);
    }

    [Fact]
    public void A_regex_that_does_not_match_leaves_the_value_absent()
    {
        var (values, _) = Extract("""
              - { name: total, from: html, selector: ".order-total", regex: "EUR ([0-9]+)" }
            """, HtmlResponse(200, Page));

        Assert.False(values.ContainsKey("total"));
    }

    [Fact]
    public void A_value_that_cannot_be_converted_is_an_error_naming_the_culture()
    {
        var (values, errors) = Extract("""
              - { name: total, from: html, selector: ".order-total", as: decimal }
            """, HtmlResponse(200, Page));

        Assert.False(values.ContainsKey("total"));
        Assert.Equal("extractor 'total' found \"59,99 zł\", which cannot be read as decimal in culture 'invariant'", errors["total"]);
    }

    [Theory]
    [InlineData("integer", "12", "12")]
    [InlineData("integer", "1,200", "1200")]
    [InlineData("boolean", "True", "true")]
    [InlineData("string", "  spaced  ", "\"spaced\"")]
    public void Types_convert_as_declared(string type, string text, string json)
    {
        var (values, errors) = Extract($$"""
              - { name: v, from: header, selector: x-value, as: {{type}} }
            """, new HttpResponseRecord { Status = 200, Headers = new Dictionary<string, string[]> { ["X-Value"] = [text] } });

        Assert.Empty(errors);
        Assert.Equal(json, values["v"]!.ToJsonString());
    }

    [Theory]
    [InlineData("integer", "12.5")]
    [InlineData("boolean", "yes")]
    public void A_wrong_type_is_an_error(string type, string text)
    {
        var (_, errors) = Extract($$"""
              - { name: v, from: header, selector: x-value, as: {{type}} }
            """, new HttpResponseRecord { Status = 200, Headers = new Dictionary<string, string[]> { ["x-value"] = [text] } });

        Assert.Contains($"cannot be read as {type}", errors["v"], StringComparison.Ordinal);
    }

    [Fact]
    public void Json_values_are_selected_by_a_path_relative_to_the_body()
    {
        var (values, _) = Extract("""
              - { name: price, from: json, selector: "items[1].price", as: decimal }
              - { name: skus, from: json, selector: "items[*].sku", all: true }
            """, JsonResponse(200, """{ "items": [ { "sku": "A", "price": 1.5 }, { "sku": "B", "price": 2.25 } ] }"""));

        Assert.Equal(2.25m, values["price"]!.GetValue<decimal>());
        Assert.Equal("""["A","B"]""", values["skus"]!.ToJsonString());
    }

    [Fact]
    public void A_json_object_is_extracted_as_its_text()
    {
        var (values, _) = Extract("""
              - { name: first, from: json, selector: "items[0]" }
            """, JsonResponse(200, """{ "items": [ { "sku": "A" } ] }"""));

        Assert.Equal("{\"sku\":\"A\"}", values["first"]!.GetValue<string>());
    }

    [Fact]
    public void Text_is_matched_by_the_first_group_of_the_selector()
    {
        var (values, _) = Extract("""
              - { name: n, from: text, selector: "Order no\\. ([0-9]+)", as: integer, all: true }
            """, new HttpResponseRecord { Status = 200, Headers = new Dictionary<string, string[]>(), Body = BodyRecord.FromText("Order no. 17, Order no. 18", "text/plain") });

        Assert.Equal("[17,18]", values["n"]!.ToJsonString());
    }

    [Fact]
    public void An_extractor_applies_only_where_its_scope_matches()
    {
        var (values, _) = Extract("""
              - { name: total, when: { path: "^/checkout$" }, from: html, selector: ".order-total" }
            """, HtmlResponse(200, Page), path: "/cart");

        Assert.False(values.ContainsKey("total"));
    }

    [Fact]
    public void Without_a_response_or_a_body_nothing_is_extracted()
    {
        var set = new ExtractorSet(ContractOf(File.ReadAllText(Sample("contract.yaml"))).Extract);

        Assert.Empty(set.Extract(Request("GET", "/cart"), null).Values);
        var noBody = set.Extract(Request("GET", "/cart"), new HttpResponseRecord { Status = 204, Headers = new Dictionary<string, string[]>() }).Values;
        Assert.False(noBody.ContainsKey("cartTotal"));
        Assert.Equal("[]", noBody["cartLineTotals"]!.ToJsonString());
        Assert.Empty(ExtractorSet.None.Extract(Request("GET", "/cart"), HtmlResponse(200, Page)).Values);
    }
}
