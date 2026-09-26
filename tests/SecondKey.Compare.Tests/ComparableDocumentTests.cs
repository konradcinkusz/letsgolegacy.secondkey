using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using static SecondKey.Compare.Tests.Fixtures;

namespace SecondKey.Compare.Tests;

public class ComparableDocumentTests
{
    private static JsonObject Build(HttpResponseRecord? response, ContractDocument? contract = null, string? baseUrl = null, ExchangeError? error = null, DbDelta? db = null) =>
        ComparableDocument.Build(Observe(response, contract, error, db: db), contract?.Compare, baseUrl);

    [Fact]
    public void Status_default_headers_body_and_extracts_are_compared_and_the_request_is_not()
    {
        var response = JsonResponse(201, """{ "b": 1, "a": 2 }""", new Dictionary<string, string[]>
        {
            ["content-type"] = ["application/json"],
            ["location"] = ["/orders/1"],
            ["date"] = ["Sat, 26 Sep 2026 10:00:00 GMT"],
        });

        var document = ComparableDocument.Build(Observe(response, request: Request("POST", "/checkout", "?x=1")), null, null);

        Assert.Equal(
            """{"extract":{},"response":{"body":{"json":{"a":2,"b":1}},"headers":{"content-type":["application/json"],"location":["/orders/1"]},"status":201}}""",
            document.ToJsonString());
    }

    [Fact]
    public void The_contract_names_the_headers_compared()
    {
        var contract = ContractWith("compare: { headers: [x-total, cache-control] }");
        var response = JsonResponse(200, "{}", new Dictionary<string, string[]>
        {
            ["content-type"] = ["application/json"],
            ["cache-control"] = ["no-store"],
            ["x-total"] = ["3", "4"],
        });

        var headers = Build(response, contract)["response"]!["headers"]!;

        Assert.Equal("""{"cache-control":["no-store"],"x-total":["3","4"]}""", headers.ToJsonString());
    }

    [Fact]
    public void Header_names_are_matched_in_lower_case_whoever_wrote_them()
    {
        var response = JsonResponse(200, "{}", new Dictionary<string, string[]> { ["Cache-Control"] = ["no-store"] });

        var document = ComparableDocument.Build(Observe(response), new CompareSettings { Headers = ["CACHE-Control"] }, null);

        Assert.Equal("""{"cache-control":["no-store"]}""", document["response"]!["headers"]!.ToJsonString());
    }

    [Theory]
    [InlineData("application/json; charset=utf-8", "application/json; charset=utf-8")]
    [InlineData("Application/JSON;Charset=\"UTF-8\"", "application/json; charset=utf-8")]
    [InlineData("  text/html ;  charset = ISO-8859-2 ", "text/html; charset=iso-8859-2")]
    [InlineData("multipart/form-data; Boundary=AbC", "multipart/form-data; boundary=AbC")]
    [InlineData("text/plain; format", "text/plain; format")]
    [InlineData("TEXT/PLAIN;;", "text/plain")]
    [InlineData(" ; ", "")]
    [InlineData("text/plain; =X", "text/plain; =X")]
    public void A_content_type_is_written_in_one_case_and_spacing(string raw, string canonical)
    {
        Assert.Equal(canonical, ComparableDocument.CanonicalContentType(raw));
    }

    [Fact]
    public void The_content_type_header_is_canonical_and_other_headers_are_not_touched()
    {
        var contract = ContractWith("compare: { headers: [content-type, x-mode] }");
        var response = JsonResponse(200, "{}", new Dictionary<string, string[]> { ["content-type"] = ["Application/Json"], ["x-mode"] = ["Mixed Case"] });

        var headers = Build(response, contract)["response"]!["headers"]!;

        Assert.Equal("application/json", headers["content-type"]![0]!.GetValue<string>());
        Assert.Equal("Mixed Case", headers["x-mode"]![0]!.GetValue<string>());
    }

    [Fact]
    public void An_html_page_is_compared_through_its_extracts_by_default()
    {
        var contract = ContractWith("""
            extract:
              - { name: total, from: html, selector: ".total", as: decimal }
            """);
        var page = TextResponse(200, """<p>Rendered at 10:00 <span class="total">12.50</span></p>""", "text/html; charset=utf-8");

        var document = Build(page, contract);

        Assert.Null(document["response"]!["body"]);
        Assert.Equal(12.5m, document["extract"]!["total"]!.GetValue<decimal>());
    }

    [Fact]
    public void An_html_page_is_compared_as_text_when_the_contract_says_so()
    {
        var contract = ContractWith("compare: { html: text }");
        var page = TextResponse(200, "<p>hi</p>", "text/html");

        Assert.Equal("<p>hi</p>", Build(page, contract)["response"]!["body"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Plain_text_and_binary_bodies_are_compared_without_their_size()
    {
        var text = Build(TextResponse(200, "hello"))["response"]!["body"]!;
        var binary = Build(new HttpResponseRecord
        {
            Status = 200,
            Headers = new Dictionary<string, string[]> { ["content-type"] = ["image/png"] },
            Body = BodyCodec.Encode([0x89, 0x50, 0x4e, 0x47], "image/png", 4, truncated: false),
        })["response"]!["body"]!;

        Assert.Equal("""{"text":"hello"}""", text.ToJsonString());
        Assert.Equal("""{"base64":"iVBORw=="}""", binary.ToJsonString());
    }

    [Fact]
    public void A_json_null_body_is_a_value_and_a_missing_body_is_nothing()
    {
        Assert.Equal("""{"json":null}""", Build(JsonResponse(200, "null"))["response"]!["body"]!.ToJsonString());
        Assert.False(Build(new HttpResponseRecord { Status = 204, Headers = new Dictionary<string, string[]>() })["response"]!.AsObject().ContainsKey("body"));
    }

    [Fact]
    public void A_response_without_a_content_type_is_not_taken_for_html()
    {
        var response = new HttpResponseRecord { Status = 200, Headers = new Dictionary<string, string[]> { ["content-type"] = [] }, Body = BodyRecord.FromText("<p>x</p>", null) };

        Assert.Equal("<p>x</p>", Build(response)["response"]!["body"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_side_that_did_not_answer_is_compared_by_the_kind_of_failure_only()
    {
        var document = Build(null, error: new ExchangeError { Kind = "timeout", Message = "no answer within 30 s from 10.0.0.7" });

        Assert.Equal("""{"error":{"kind":"timeout"},"extract":{}}""", document.ToJsonString());
    }

    [Fact]
    public void Extraction_errors_are_compared_so_a_value_that_stops_parsing_is_a_difference()
    {
        var contract = ContractWith("""
            extract:
              - { name: total, from: html, selector: ".total", as: decimal }
            """);

        var document = Build(TextResponse(200, """<span class="total">twelve</span>""", "text/html"), contract);

        Assert.Contains("twelve", document["extractErrors"]!["total"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(Build(TextResponse(200, "<p/>", "text/html"), contract).ContainsKey("extractErrors"));
    }

    [Fact]
    public void Row_changes_are_compared()
    {
        var db = new DbDelta
        {
            Tables = new Dictionary<string, TableDelta>
            {
                ["dbo.Orders"] = new() { Inserted = [new JsonObject { ["Id"] = 1 }], Deleted = [], Updated = [] },
            },
        };

        var document = Build(JsonResponse(200, "{}"), db: db);

        Assert.Equal(1m, document["db"]!["dbo.Orders"]!["inserted"]![0]!["Id"]!.GetValue<decimal>());
        Assert.False(Build(JsonResponse(200, "{}")).ContainsKey("db"));
    }

    [Fact]
    public void Each_sides_own_address_is_replaced_wherever_it_appears()
    {
        var response = JsonResponse(302, $$"""{ "next": "{{LegacyUrl}}/cart", "other": "http://elsewhere.test/cart", "n": 1 }""", new Dictionary<string, string[]>
        {
            ["content-type"] = ["application/json"],
            ["location"] = [$"{LegacyUrl}/cart?step=2"],
        });

        var document = Build(response, baseUrl: LegacyUrl + "/");

        Assert.Equal("<base-url>/cart?step=2", document["response"]!["headers"]!["location"]![0]!.GetValue<string>());
        Assert.Equal("<base-url>/cart", document["response"]!["body"]!["json"]!["next"]!.GetValue<string>());
        Assert.Equal("http://elsewhere.test/cart", document["response"]!["body"]!["json"]!["other"]!.GetValue<string>());
        Assert.Equal(1m, document["response"]!["body"]!["json"]!["n"]!.GetValue<decimal>());
    }

    [Fact]
    public void Outbound_calls_are_compared()
    {
        var observation = new Contract.Observations.ObservationBuilder(Contract.Extraction.ExtractorSet.None).Build(
            Request(),
            JsonResponse(200, "{}"),
            error: null,
            db: null,
            outbound: [new OutboundCall { Method = "POST", Url = "https://payments.test/charge" }]);

        var document = ComparableDocument.Build(observation, null, null);

        Assert.Equal("https://payments.test/charge", document["outbound"]![0]!["url"]!.GetValue<string>());
        Assert.False(Build(JsonResponse(200, "{}")).ContainsKey("outbound"));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ComparableDocument.Build(null!, null, null));
        Assert.Throws<ArgumentNullException>(() => ComparableDocument.CanonicalContentType(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public void Without_an_address_nothing_is_replaced(string? baseUrl)
    {
        var document = Build(JsonResponse(200, """{ "next": "/cart" }"""), baseUrl: baseUrl);

        Assert.Equal("/cart", document["response"]!["body"]!["json"]!["next"]!.GetValue<string>());
    }

    [Fact]
    public void The_same_answer_from_two_addresses_is_the_same_document()
    {
        HttpResponseRecord At(string url) => JsonResponse(201, $$"""{ "self": "{{url}}/orders/7" }""", new Dictionary<string, string[]> { ["location"] = [$"{url}/orders/7"] });

        var legacy = Build(At(LegacyUrl), baseUrl: LegacyUrl);
        var candidate = Build(At(CandidateUrl), baseUrl: CandidateUrl);

        Assert.Empty(JsonDiff.Compare(legacy, candidate));
    }

    [Fact]
    public void Numbers_are_canonical()
    {
        Assert.Equal("12.5", Build(JsonResponse(200, """{ "t": 12.50 }"""))["response"]!["body"]!["json"]!["t"]!.ToJsonString());
    }
}
