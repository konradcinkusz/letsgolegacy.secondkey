using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using SecondKey.Contract.Extraction;
using SecondKey.Contract.Observations;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class ObservationTests
{
    [Fact]
    public void A_form_body_is_parsed_into_fields_and_the_query_into_lists()
    {
        var request = Request("POST", "/login", "?next=%2Fcart&tag=a&tag=b+c", BodyRecord.FromText("user=ann&remember=on&empty=", "application/x-www-form-urlencoded"));

        var observation = Observe(request, JsonResponse(302, "{}"));

        Assert.Equal("""{"next":["/cart"],"tag":["a","b c"]}""", observation.Document["request"]!["query"]!.ToJsonString());
        Assert.Equal("""{"user":["ann"],"remember":["on"],"empty":[""]}""", observation.Document["request"]!["body"]!["form"]!.ToJsonString());
        Assert.Equal(["a", "b c"], observation.Query["tag"]);
        Assert.Equal("POST", observation.Method);
        Assert.Equal("/login", observation.Path);
    }

    [Fact]
    public void A_form_is_recognised_from_the_header_when_the_body_does_not_say()
    {
        var request = Request("POST", "/login", body: new BodyRecord { Encoding = BodyEncoding.Text, Text = "a=1" }, headers: new() { ["Content-Type"] = ["application/x-www-form-urlencoded; charset=utf-8"] });

        Assert.Equal("""{"a":["1"]}""", Observe(request, null).Document["request"]!["body"]!["form"]!.ToJsonString());
    }

    [Fact]
    public void A_plain_text_body_has_no_form()
    {
        var observation = Observe(Request("POST", "/x", body: BodyRecord.FromText("a=1", "text/plain")), null);

        Assert.Null(observation.Document["request"]!["body"]!["form"]);
    }

    [Fact]
    public void Headers_are_lower_cased_and_bodies_keep_their_encoding()
    {
        var response = new HttpResponseRecord
        {
            Status = 200,
            Headers = new Dictionary<string, string[]> { ["X-Trace"] = ["1"] },
            Body = BodyRecord.FromBytes([1, 2], "application/octet-stream", 2),
        };

        var observation = Observe(Request(), response);

        Assert.Equal("""["1"]""", observation.Document["response"]!["headers"]!["x-trace"]!.ToJsonString());
        Assert.Equal("AQI=", observation.Document["response"]!["body"]!["base64"]!.GetValue<string>());
        Assert.Equal(2, observation.Document["response"]!["body"]!["size"]!.GetValue<long>());
    }

    [Fact]
    public void An_error_replaces_the_response()
    {
        var observation = Observe(Request(), null, new ExchangeError { Kind = "timeout", Message = "30 s" });

        Assert.Null(observation.Document["response"]);
        Assert.Equal("timeout", observation.Document["error"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Database_deltas_and_outbound_calls_are_observable()
    {
        var builder = new ObservationBuilder(ExtractorSet.None);
        var db = new DbDelta
        {
            Tables = new Dictionary<string, TableDelta>
            {
                ["dbo.Orders"] = new() { Inserted = [new JsonObject { ["Id"] = 7 }], Deleted = [], Updated = [] },
            },
        };
        var outbound = new[] { new OutboundCall { Method = "POST", Url = "https://pay.example/charge" } };

        var observation = builder.Build(Request(), JsonResponse(200, "{}"), error: null, db, outbound);

        Assert.Equal(7, Artifacts.Paths.SelectorPath.Parse("db[\"dbo.Orders\"].inserted[0].Id").Select(observation.Document).Single()!.GetValue<int>());
        Assert.Equal("https://pay.example/charge", Artifacts.Paths.SelectorPath.Parse("outbound[0].url").Select(observation.Document).Single()!.GetValue<string>());
    }

    [Fact]
    public void A_captured_exchange_and_a_run_result_build_the_same_kind_of_observation()
    {
        var capture = CaptureFile.Read(Sample("sample.skcap"));
        var builder = new ObservationBuilder(new ExtractorSet(SampleContract().Document.Extract));

        var fromCapture = builder.Build(capture.Exchanges[2]);
        var fromRun = builder.Build(SampleRun().Find("ex-000003", Side.Legacy)!);

        Assert.Equal(59.97m, fromCapture.Document["extract"]!["cartTotal"]!.GetValue<decimal>());
        Assert.Equal(fromCapture.Document["extract"]!.ToJsonString(), fromRun.Document["extract"]!.ToJsonString());
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("?", "{}")]
    [InlineData("?a", """{"a":[""]}""")]
    [InlineData("a=1&&b=%20x", """{"a":["1"],"b":[" x"]}""")]
    public void Query_strings_parse_leniently(string? text, string expected)
    {
        var parsed = QueryString.Parse(text);

        Assert.Equal(expected, System.Text.Json.JsonSerializer.Serialize(parsed));
    }

    [Fact]
    public void Building_from_null_is_refused()
    {
        var builder = new ObservationBuilder(ExtractorSet.None);

        Assert.Throws<ArgumentNullException>(() => builder.Build((ExchangeResult)null!));
        Assert.Throws<ArgumentNullException>(() => builder.Build((HttpExchange)null!));
        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, null, null, null, null));
    }
}
