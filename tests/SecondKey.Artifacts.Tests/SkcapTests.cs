using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Tests;

public class SkcapTests
{
    [Fact]
    public void The_sample_capture_validates()
    {
        var report = CaptureFile.Validate(RepoPaths.Sample("sample.skcap"));

        Assert.True(report.IsValid, report.ToString());
    }

    [Fact]
    public void The_sample_capture_reads_into_its_header_and_exchanges()
    {
        var capture = CaptureFile.Read(RepoPaths.Sample("sample.skcap"));

        Assert.Equal("cap-20260926-sample", capture.Header.CaptureId);
        Assert.Equal(["sk_session"], capture.Header.SessionKeys);
        Assert.Equal(5, capture.Exchanges.Count);
        Assert.Equal(["ex-000001", "ex-000002", "ex-000003", "ex-000004", "ex-000005"], capture.Exchanges.Select(e => e.Id));
        Assert.Equal(BodyEncoding.Json, capture.Exchanges[0].Response.Body!.Encoding);
        Assert.Equal(BodyEncoding.Text, capture.Exchanges[2].Response.Body!.Encoding);
    }

    public static TheoryData<string, Func<string[], string[]>, int?, string> BrokenCaptures => new()
    {
        { "a required field is missing", l => Edit(l, 2, n => n.AsObject().Remove("seq")), 2, "seq" },
        { "the method is not an HTTP method token", l => Edit(l, 2, n => n["request"]!["method"] = "get"), 2, "/request/method" },
        { "the status is not an HTTP status", l => Edit(l, 3, n => n["response"]!["status"] = 999), 3, "/response/status" },
        { "a body mixes encodings", l => Edit(l, 2, n => n["response"]!["body"]!["text"] = "also text"), 2, "/response/body" },
        { "an unknown key is present", l => Edit(l, 2, n => n["extra"] = true), 2, "extra" },
        { "the line is not JSON", l => Replace(l, 3, "{\"type\":\"http.exchange\","), 3, "not valid JSON" },
        { "the event type is unknown", l => Edit(l, 3, n => n["type"] = "sql.statement"), 3, "unknown event type 'sql.statement'" },
        { "the header is missing", l => l[1..], 1, "first line of a capture must be its capture.header" },
        { "a second header follows the first", l => [.. l[..3], l[0], .. l[3..]], 4, "exactly one capture.header" },
        { "an exchange id repeats", l => Edit(l, 3, n => n["id"] = "ex-000001"), 3, "appears more than once" },
        { "the seq goes backwards", l => Edit(l, 3, n => n["seq"] = 1), 3, "does not follow" },
        { "a blank line sits inside the file", l => [.. l[..2], "", .. l[2..]], 3, "blank line" },
    };

    [Theory]
    [MemberData(nameof(BrokenCaptures))]
    public void A_broken_line_is_rejected_with_its_line_number(string why, Func<string[], string[]> breakIt, int? line, string expected)
    {
        var lines = breakIt(RepoPaths.SampleLines("sample.skcap"));

        var report = CaptureFile.Validate(new StringReader(string.Join('\n', lines) + "\n"), why);

        Assert.False(report.IsValid, why);
        Assert.Contains(report.Errors, e => e.Line == line && (e.Message.Contains(expected, StringComparison.Ordinal) || e.Location.Contains(expected, StringComparison.Ordinal)));
    }

    [Fact]
    public void An_empty_file_is_rejected()
    {
        var report = CaptureFile.Validate(new StringReader(""), "empty");

        Assert.False(report.IsValid);
        Assert.Contains("empty capture", report.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reading_an_invalid_capture_throws_with_every_error()
    {
        var lines = RepoPaths.SampleLines("sample.skcap");
        lines[1] = "not json";
        lines[2] = "also not json";

        var ex = Assert.Throws<ArtifactValidationException>(() => CaptureFile.Read(new StringReader(string.Join('\n', lines)), "broken"));

        Assert.Equal(2, ex.Report.Errors.Count);
    }

    [Fact]
    public void Every_sample_event_survives_a_write_and_read_round_trip()
    {
        var capture = CaptureFile.Read(RepoPaths.Sample("sample.skcap"));
        var text = string.Join('\n', new CaptureEvent[] { capture.Header }.Concat(capture.Exchanges).Select(CaptureWriter.Serialize));

        var again = CaptureFile.Read(new StringReader(text), "round-trip");

        Assert.Equal(capture.Exchanges.Select(e => (e.Id, e.Seq, e.Response.Status)), again.Exchanges.Select(e => (e.Id, e.Seq, e.Response.Status)));
        Assert.Equal(capture.Exchanges[3].Response.Body!.Json!.ToJsonString(), again.Exchanges[3].Response.Body!.Json!.ToJsonString());
    }

    [Fact]
    public async Task Concurrent_writes_produce_a_valid_file_with_strictly_increasing_seq()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.skcap");
        try
        {
            var header = CaptureFile.Read(RepoPaths.Sample("sample.skcap")).Header;
            var template = CaptureFile.Read(RepoPaths.Sample("sample.skcap")).Exchanges[0];
            await using (var writer = await CaptureWriter.CreateAsync(path, header, CancellationToken.None))
            {
                await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => writer.WriteAsync(
                    seq => template with { Id = $"ex-{seq:D6}", Seq = seq },
                    CancellationToken.None)));
                Assert.Equal(25, writer.Count);
            }

            var capture = CaptureFile.Read(path);
            Assert.Equal(Enumerable.Range(1, 25).Select(i => (long)i), capture.Exchanges.Select(e => e.Seq));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string[] Edit(string[] lines, int line, Action<JsonNode> edit)
    {
        var copy = (string[])lines.Clone();
        var node = JsonNode.Parse(copy[line - 1])!;
        edit(node);
        copy[line - 1] = node.ToJsonString();
        return copy;
    }

    private static string[] Replace(string[] lines, int line, string text)
    {
        var copy = (string[])lines.Clone();
        copy[line - 1] = text;
        return copy;
    }

    [Fact]
    public void A_second_header_is_reported_on_its_own_line_with_the_rule()
    {
        var lines = RepoPaths.SampleLines("sample.skcap");
        var text = string.Join('\n', lines.Take(2).Append(lines[0]).Concat(lines.Skip(2)));

        var error = Assert.Single(CaptureFile.Validate(new StringReader(text), "two headers").Errors);

        Assert.Equal(new ArtifactError(3, "/type", "a capture has exactly one capture.header, on its first line"), error);
    }

    [Fact]
    public void The_first_blank_line_is_the_one_reported()
    {
        var lines = RepoPaths.SampleLines("sample.skcap");
        var text = string.Join('\n', lines.Take(2).Concat(["", ""]).Concat(lines.Skip(2)));

        var error = Assert.Single(CaptureFile.Validate(new StringReader(text), "blank").Errors);

        Assert.Equal(new ArtifactError(3, "", "blank line inside the file: every line must be one event"), error);
    }

    [Fact]
    public void Trailing_blank_lines_are_only_a_trailing_newline()
    {
        var text = File.ReadAllText(RepoPaths.Sample("sample.skcap")) + "\n\n";

        Assert.True(CaptureFile.Validate(new StringReader(text), "trailing").IsValid);
    }

    [Fact]
    public void Duplicate_ids_and_backwards_seq_are_reported_exactly()
    {
        var lines = RepoPaths.SampleLines("sample.skcap");
        lines[2] = lines[2].Replace("\"id\":\"ex-000002\",\"seq\":2", "\"id\":\"ex-000001\",\"seq\":1", StringComparison.Ordinal);

        var errors = CaptureFile.Validate(new StringReader(string.Join('\n', lines)), "dup").Errors;

        Assert.Contains(new ArtifactError(3, "/id", "exchange id 'ex-000001' appears more than once"), errors);
        Assert.Contains(new ArtifactError(3, "/seq", "seq 1 does not follow 1: exchanges must be in recorded order"), errors);
    }

    [Fact]
    public void An_exchange_on_the_first_line_is_reported_there()
    {
        var lines = RepoPaths.SampleLines("sample.skcap").Skip(1);

        var errors = CaptureFile.Validate(new StringReader(string.Join('\n', lines)), "headless").Errors;

        Assert.Equal([new ArtifactError(1, "/type", "the first line of a capture must be its capture.header")], errors);
    }

    [Fact]
    public void An_empty_capture_reports_one_error_without_a_line()
    {
        Assert.Equal(
            [new ArtifactError(null, "", "empty capture: the first line must be a capture.header")],
            CaptureFile.Validate(new StringReader(""), "empty").Errors);
    }

    [Fact]
    public async Task Each_line_is_on_disk_as_soon_as_it_is_written()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}", "nested");
        var path = Path.Combine(directory, "live.skcap");
        try
        {
            var sample = CaptureFile.Read(RepoPaths.Sample("sample.skcap"));
            await using var writer = await CaptureWriter.CreateAsync(path, sample.Header);
            await writer.WriteAsync(seq => sample.Exchanges[0] with { Seq = seq });

            // Read while the writer is still open: a proxy that crashes keeps what it recorded.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var capture = CaptureFile.Read(reader, path);

            Assert.Single(capture.Exchanges);
            Assert.Equal(1, capture.Exchanges[0].Seq);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(directory)!, recursive: true);
        }
    }

    [Fact]
    public async Task The_writer_refuses_null_arguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => CaptureWriter.CreateAsync((Stream)null!, CaptureFile.Read(RepoPaths.Sample("sample.skcap")).Header));
        await using var writer = await CaptureWriter.CreateAsync(new MemoryStream(), CaptureFile.Read(RepoPaths.Sample("sample.skcap")).Header);
        await Assert.ThrowsAsync<ArgumentNullException>(() => writer.WriteAsync(null!));
    }

    [Fact]
    public void Body_factories_set_the_encoding_and_only_mark_truncation_when_true()
    {
        Assert.Equal(BodyEncoding.Json, BodyRecord.FromJson(JsonNode.Parse("{}"), "application/json").Encoding);
        Assert.Null(BodyRecord.FromText("x", "text/plain").Truncated);
        Assert.True(BodyRecord.FromText("x", "text/plain", 10, truncated: true).Truncated);
        var binary = BodyRecord.FromBytes([1, 2, 3], "application/octet-stream", 3, truncated: false);
        Assert.Equal("AQID", binary.Base64);
        Assert.Null(binary.Truncated);
        Assert.True(BodyRecord.FromBytes([1], null, 9, truncated: true).Truncated);
    }
}
