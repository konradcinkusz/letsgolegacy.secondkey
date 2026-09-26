using System.Text.Json;
using SecondKey.Artifacts.Schemas;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Tests;

public class ArtifactSchemasTests
{
    [Theory]
    [InlineData(ArtifactKind.Capture, "skcap.schema.json")]
    [InlineData(ArtifactKind.Contract, "contract.schema.json")]
    [InlineData(ArtifactKind.Run, "skrun.schema.json")]
    [InlineData(ArtifactKind.Verdict, "verdict.schema.json")]
    public void Every_kind_embeds_the_schema_file_of_the_same_name(ArtifactKind kind, string file)
    {
        Assert.Equal(file, ArtifactSchemas.FileName(kind));
        var text = ArtifactSchemas.Text(kind);

        Assert.Equal(File.ReadAllText(Path.Combine(RepoPaths.Root, "schemas", file)), text);
        using var schema = JsonDocument.Parse(text);
        Assert.Equal(ArtifactSchemas.BaseUri + file, schema.RootElement.GetProperty("$id").GetString());
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArtifactSchemas.FileName((ArtifactKind)42));
    }

    [Theory]
    [InlineData(ArtifactKind.Capture)]
    [InlineData(ArtifactKind.Run)]
    public void Event_artifacts_are_not_validated_as_documents(ArtifactKind kind)
    {
        using var json = JsonDocument.Parse("{}");

        var ex = Assert.Throws<ArgumentException>(() => ArtifactSchemas.ValidateDocument(kind, json.RootElement));

        Assert.Contains("validate it line by line", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ArtifactKind.Contract)]
    [InlineData(ArtifactKind.Verdict)]
    public void Document_artifacts_are_not_validated_as_events(ArtifactKind kind)
    {
        using var json = JsonDocument.Parse("{}");

        var ex = Assert.Throws<ArgumentException>(() => ArtifactSchemas.ValidateEvent(kind, json.RootElement, 1));

        Assert.Contains("validate it whole", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1,2]", "", "an event must be a JSON object")]
    [InlineData("{\"v\":1}", "/type", "an event must carry a string 'type'")]
    [InlineData("{\"type\":7}", "/type", "an event must carry a string 'type'")]
    [InlineData("{\"type\":\"sql.statement\"}", "/type", "unknown event type 'sql.statement'; this version knows: capture.header, http.exchange")]
    public void An_event_without_a_known_type_is_refused_before_the_schema(string json, string location, string message)
    {
        using var document = JsonDocument.Parse(json);

        var error = Assert.Single(ArtifactSchemas.ValidateEvent(ArtifactKind.Capture, document.RootElement, 7));

        Assert.Equal(new ArtifactError(7, location, message), error);
    }

    [Fact]
    public void Run_event_types_are_listed_in_order_when_one_is_unknown()
    {
        using var document = JsonDocument.Parse("{\"type\":\"run.middle\"}");

        var error = Assert.Single(ArtifactSchemas.ValidateEvent(ArtifactKind.Run, document.RootElement, 2));

        Assert.EndsWith("exchange.result, run.end, run.header, state.reset", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Date_time_formats_are_enforced_not_just_described()
    {
        var header = RepoPaths.SampleLines("sample.skcap")[0].Replace("2026-09-26T09:00:00Z", "yesterday", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(header);

        var errors = ArtifactSchemas.ValidateEvent(ArtifactKind.Capture, document.RootElement, 1);

        Assert.Contains(errors, e => e.Location == "/startedAt");
    }

    [Fact]
    public void A_schema_error_names_the_keyword_and_the_location()
    {
        var line = RepoPaths.SampleLines("sample.skcap")[1].Replace("\"seq\":1,", "", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(line);

        var errors = ArtifactSchemas.ValidateEvent(ArtifactKind.Capture, document.RootElement, 2);

        var error = Assert.Single(errors);
        Assert.Equal(2, error.Line);
        Assert.Equal("", error.Location);
        Assert.Contains("seq", error.Message, StringComparison.Ordinal);
        Assert.EndsWith("(required)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_valid_document_has_no_errors()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPaths.Sample("verdict.json")));

        Assert.Empty(ArtifactSchemas.ValidateDocument(ArtifactKind.Verdict, document.RootElement));
    }
}
