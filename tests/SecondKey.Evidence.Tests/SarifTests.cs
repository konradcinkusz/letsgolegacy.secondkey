using SecondKey.Artifacts.Validation;
using SecondKey.Evidence.Sarif;
using static SecondKey.Evidence.Tests.Fixtures;

namespace SecondKey.Evidence.Tests;

public class SarifLogTests
{
    private static SarifLog Log(string results, string rules = "[]", string driverExtra = "", string runExtra = "") => SarifLog.Parse(
        $$"""
        { "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "T"{{driverExtra}}, "rules": {{rules}} } }, "results": {{results}}{{runExtra}} } ] }
        """,
        "test.sarif");

    private static SarifFinding One(string result, string rules = "[]") => Assert.Single(Log($"[{result}]", rules).Findings);

    [Fact]
    public void The_sample_is_read_as_the_gate_needs_it()
    {
        var log = SarifLog.Read(SampleSarif);

        var run = Assert.Single(log.Runs);
        Assert.Equal("portcullis.sarif", log.Name);
        Assert.Equal(("Portcullis", "0.1.0"), (run.Tool, run.Version));
        Assert.Equal(4, run.Rules.Count);
        Assert.Equal("error", run.Rules.Single(r => r.Id == "PORTCULLIS_MIG_HTTPCONTEXT_CURRENT").DefaultLevel);
        Assert.Equal("Blocking on a task: .Result, .Wait() or GetAwaiter().GetResult()", run.Rules.Single(r => r.Id == "PORTCULLIS_MIG_SYNC_OVER_ASYNC").Description);
        Assert.EndsWith("MIGRATION.md", run.Rules[0].HelpUri, StringComparison.Ordinal);

        var findings = log.Findings.ToList();
        Assert.Equal(3, findings.Count);
        Assert.Equal("src/SampleShop/Checkout/OrderService.cs:42", findings[0].Location);
        Assert.Equal(("warning", "new", false), (findings[0].Level, findings[0].BaselineState, findings[0].Suppressed));
        Assert.True(findings[2].Suppressed);
        Assert.All(findings, f => Assert.False(f.Blocks));
    }

    [Theory]
    [InlineData("{ nope", "", "not valid JSON")]
    [InlineData("""{ "version": "2.0.0", "runs": [] }""", "/version", "not a SARIF 2.1.0 log")]
    [InlineData("""[ 1 ]""", "/version", "not a SARIF 2.1.0 log")]
    [InlineData("""{ "version": "2.1.0" }""", "/runs", "a SARIF log has a runs array")]
    [InlineData("""{ "version": "2.1.0", "runs": [ { "tool": { "driver": { } } } ] }""", "/runs/0/tool/driver/name", "every run names its tool")]
    [InlineData("""{ "version": "2.1.0", "runs": [ 7 ] }""", "/runs/0/tool/driver/name", "every run names its tool")]
    public void Anything_that_is_not_sarif_is_refused_never_read_as_no_findings(string json, string location, string message)
    {
        var ex = Assert.Throws<ArtifactValidationException>(() => SarifLog.Parse(json, "x.sarif"));

        var error = Assert.Single(ex.Report.Errors);
        Assert.Equal("x.sarif", ex.Report.Artifact);
        Assert.Equal(location, error.Location);
        Assert.StartsWith(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_level_comes_from_the_result_then_the_rule_then_is_a_warning()
    {
        const string Rules = """[ { "id": "E", "defaultConfiguration": { "level": "error" } }, { "id": "N" } ]""";

        Assert.Equal("note", One("""{ "ruleId": "E", "level": "note", "message": { "text": "m" } }""", Rules).Level);
        Assert.Equal("error", One("""{ "ruleId": "E", "message": { "text": "m" } }""", Rules).Level);
        Assert.Equal("warning", One("""{ "ruleId": "N", "message": { "text": "m" } }""", Rules).Level);
        Assert.Equal("warning", One("""{ "ruleId": "UNKNOWN", "message": { "text": "m" } }""", Rules).Level);
    }

    [Fact]
    public void A_result_that_is_not_a_failure_has_no_level()
    {
        Assert.Equal("none", One("""{ "ruleId": "E", "kind": "pass", "level": "error", "message": { "text": "m" } }""").Level);
        Assert.Equal("error", One("""{ "ruleId": "E", "kind": "fail", "level": "error", "message": { "text": "m" } }""").Level);
    }

    [Fact]
    public void The_rule_id_is_found_wherever_the_log_puts_it()
    {
        const string Rules = """[ { "id": "FIRST" }, { "id": "SECOND" } ]""";

        Assert.Equal("R", One("""{ "rule": { "id": "R" }, "message": { "text": "m" } }""").RuleId);
        Assert.Equal("SECOND", One("""{ "ruleIndex": 1, "message": { "text": "m" } }""", Rules).RuleId);
        Assert.Equal("(no rule)", One("""{ "ruleIndex": 2, "message": { "text": "m" } }""", Rules).RuleId);
        Assert.Equal("(no rule)", One("""{ "ruleIndex": -1, "message": { "text": "m" } }""", Rules).RuleId);
        Assert.Equal("(no rule)", One("""{ "message": { "text": "m" } }""").RuleId);
    }

    [Fact]
    public void A_message_without_text_falls_back_to_its_id()
    {
        Assert.Equal("msg-1", One("""{ "ruleId": "R", "message": { "id": "msg-1" } }""").Message);
        Assert.Equal("(no message)", One("""{ "ruleId": "R" }""").Message);
    }

    [Fact]
    public void Locations_are_shown_as_a_path_and_a_line()
    {
        string? Where(string locations) => One($$"""{ "ruleId": "R", "message": { "text": "m" }, "locations": {{locations}} }""").Location;

        Assert.Equal("src/My File.cs:3", Where("""[ { "physicalLocation": { "artifactLocation": { "uri": "src/My%20File.cs" }, "region": { "startLine": 3 } } } ]"""));
        Assert.Equal("/home/runner/work/a.cs:9", Where("""[ { "physicalLocation": { "artifactLocation": { "uri": "file:///home/runner/work/a.cs" }, "region": { "startLine": 9 } } } ]"""));
        Assert.Equal("src/a.cs", Where("""[ { "physicalLocation": { "artifactLocation": { "uri": "src/a.cs" } } } ]"""));
        Assert.Null(Where("[]"));
        Assert.Null(Where("""[ { "logicalLocations": [ { "name": "Program" } ] } ]"""));
        Assert.Null(One("""{ "ruleId": "R", "message": { "text": "m" } }""").Location);
    }

    [Theory]
    [InlineData("""[ { "kind": "inSource" } ]""", true)]
    [InlineData("""[ { "kind": "external", "status": "accepted" } ]""", true)]
    [InlineData("""[ { "kind": "external", "status": "underReview" } ]""", false)]
    [InlineData("""[ { "kind": "external", "status": "rejected" } ]""", false)]
    [InlineData("""[ { "status": "rejected" }, { "kind": "inSource" } ]""", true)]
    [InlineData("[]", false)]
    public void Only_an_accepted_suppression_suppresses(string suppressions, bool suppressed)
    {
        Assert.Equal(suppressed, One($$"""{ "ruleId": "R", "level": "error", "message": { "text": "m" }, "suppressions": {{suppressions}} }""").Suppressed);
    }

    [Fact]
    public void Rules_come_from_the_driver_and_its_extensions_once_each()
    {
        var log = SarifLog.Parse(
            """
            { "version": "2.1.0", "runs": [ { "tool": {
                "driver": { "name": "csc", "semanticVersion": "4.12.0", "rules": [ { "id": "A", "name": "RuleA" }, { "id": "A" }, { "name": "no id" } ] },
                "extensions": [ { "name": "Portcullis", "rules": [ { "id": "B", "shortDescription": { "text": "Rule B" } } ] }, { "name": "empty" } ] },
              "results": [] } ] }
            """,
            "csc.sarif");

        var run = Assert.Single(log.Runs);
        Assert.Equal("4.12.0", run.Version);
        Assert.Equal([("A", "RuleA"), ("B", "Rule B")], run.Rules.Select(r => (r.Id, r.Description)));
    }

    [Fact]
    public void A_run_without_results_or_rules_has_none()
    {
        var log = SarifLog.Parse("""{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "T" } } } ] }""", "t.sarif");

        Assert.Empty(log.Findings);
        Assert.Empty(log.Runs[0].Rules);
        Assert.Null(log.Runs[0].Version);
    }

    [Theory]
    [InlineData("error", null, false, true)]
    [InlineData("error", "new", false, true)]
    [InlineData("error", "updated", false, true)]
    [InlineData("error", "unchanged", false, false)]
    [InlineData("error", "absent", false, false)]
    [InlineData("error", "new", true, false)]
    [InlineData("warning", "new", false, false)]
    [InlineData("note", null, false, false)]
    public void An_error_blocks_unless_it_is_suppressed_or_known_from_the_baseline(string level, string? baselineState, bool suppressed, bool blocks)
    {
        var finding = new SarifFinding { RuleId = "R", Level = level, Message = "m", BaselineState = baselineState, Suppressed = suppressed };

        Assert.Equal(blocks, finding.Blocks);
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => SarifLog.Read(null!));
        Assert.Throws<ArgumentNullException>(() => SarifLog.Parse(null!, "x"));
        Assert.Throws<ArgumentNullException>(() => GateSummary.From(null!));
    }
}

public class GateSummaryTests
{
    private static SarifLog Log(string name, string results, string rules = "[]") => SarifLog.Parse(
        $$"""{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "Portcullis", "rules": {{rules}} } }, "results": {{results}} } ] }""",
        name);

    [Fact]
    public void The_sample_passes_with_two_warnings_and_a_suppressed_error()
    {
        var summary = GateSummary.From([SarifLog.Read(SampleSarif)]);

        Assert.True(summary.Passes);
        Assert.Empty(summary.Blocking);
        Assert.Equal(2, summary.Findings);
        Assert.Equal(4, summary.Rules.Count);
        var suppressed = summary.Rules.Single(r => r.RuleId == "PORTCULLIS_MIG_HTTPCONTEXT_CURRENT");
        Assert.Equal((0, 1, 0), (suppressed.Errors, suppressed.Suppressed, suppressed.Blocking));
        Assert.Equal(new RuleSummary("Portcullis", "PORTCULLIS_MIG_CONFIGURATION_MANAGER", "Static ConfigurationManager access survives the migration", suppressed.HelpUri, 0, 0, 0, 0, 0), summary.Rules[0]);
    }

    [Fact]
    public void Every_finding_is_counted_by_level_and_blocking_ones_are_listed_with_their_log()
    {
        var first = Log(
            "a.sarif",
            """
            [ { "ruleId": "E", "level": "error", "message": { "text": "one" }, "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "a.cs" }, "region": { "startLine": 1 } } } ] },
              { "ruleId": "E", "level": "error", "message": { "text": "known" }, "baselineState": "unchanged" },
              { "ruleId": "W", "level": "warning", "message": { "text": "w" } },
              { "ruleId": "N", "level": "note", "message": { "text": "n" } },
              { "ruleId": "P", "kind": "pass", "message": { "text": "p" } } ]
            """,
            """[ { "id": "E", "shortDescription": { "text": "Errors" } }, { "id": "QUIET" } ]""");
        var second = Log("b.sarif", """[ { "ruleId": "E", "level": "error", "message": { "text": "two" } } ]""");

        var summary = GateSummary.From([first, second]);

        Assert.False(summary.Passes);
        Assert.Equal(6, summary.Findings);
        Assert.Equal(
            [("a.sarif", "one", "a.cs:1"), ("b.sarif", "two", (string?)null)],
            summary.Blocking.Select(b => (b.Log, b.Finding.Message, b.Finding.Location)));
        Assert.All(summary.Blocking, b => Assert.Equal("Portcullis", b.Tool));
        Assert.Equal(["E", "E", "N", "P", "QUIET", "W"], summary.Rules.Select(r => r.RuleId));
        var errors = summary.Rules[0];
        Assert.Equal(("E", "Errors", 2, 0, 0, 0, 1), (errors.RuleId, errors.Description, errors.Errors, errors.Warnings, errors.Notes, errors.Suppressed, errors.Blocking));
        Assert.Equal((0, 1, 0), (summary.Rules.Single(r => r.RuleId == "W").Errors, summary.Rules.Single(r => r.RuleId == "W").Warnings, summary.Rules.Single(r => r.RuleId == "W").Notes));
        Assert.Equal(1, summary.Rules.Single(r => r.RuleId == "N").Notes);
        Assert.Equal(1, summary.Rules.Single(r => r.RuleId == "P").Notes);
        Assert.Equal((0, 0, 0, 0), (summary.Rules.Single(r => r.RuleId == "QUIET").Errors, summary.Rules.Single(r => r.RuleId == "QUIET").Warnings, summary.Rules.Single(r => r.RuleId == "QUIET").Notes, summary.Rules.Single(r => r.RuleId == "QUIET").Blocking));
        Assert.Null(summary.Rules.Single(r => r.RuleId == "W").Description);
        Assert.Equal(2, summary.Logs.Count);
    }

    [Fact]
    public void Rules_that_block_come_first_then_tools_and_rules_in_order()
    {
        SarifLog Tool(string tool, string results) => SarifLog.Parse($$"""{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "{{tool}}" } }, "results": {{results}} } ] }""", tool);

        var summary = GateSummary.From([
            Tool("Zeta", """[ { "ruleId": "B", "level": "warning", "message": { "text": "m" } }, { "ruleId": "A", "level": "warning", "message": { "text": "m" } } ]"""),
            Tool("Alpha", """[ { "ruleId": "C", "level": "warning", "message": { "text": "m" } }, { "ruleId": "Z", "level": "error", "message": { "text": "m" } } ]""")]);

        Assert.Equal([("Alpha", "Z"), ("Alpha", "C"), ("Zeta", "A"), ("Zeta", "B")], summary.Rules.Select(r => (r.Tool, r.RuleId)));
    }

    [Fact]
    public void No_logs_pass_with_nothing_found()
    {
        var summary = GateSummary.From([]);

        Assert.True(summary.Passes);
        Assert.Equal(0, summary.Findings);
        Assert.Empty(summary.Rules);
    }
}
