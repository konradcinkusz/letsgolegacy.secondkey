using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Validation;

namespace SecondKey.Artifacts.Tests;

public class ContractTests
{
    private static string SampleYaml => File.ReadAllText(RepoPaths.Sample("contract.yaml"));

    [Fact]
    public void The_sample_contract_validates()
    {
        var report = ContractFile.Validate(RepoPaths.Sample("contract.yaml"));

        Assert.True(report.IsValid, report.ToString());
    }

    [Fact]
    public void The_sample_contract_loads_with_its_digest()
    {
        var loaded = ContractFile.Load(RepoPaths.Sample("contract.yaml"));

        Assert.Equal("sample-shop", loaded.Document.Metadata.Name);
        Assert.Equal(6, loaded.Document.Clauses.Count);
        Assert.Equal(2, loaded.Document.Extract!.Count);
        Assert.Matches("^[a-f0-9]{64}$", loaded.Sha256);
        var checkout = loaded.Document.Clauses.Single(c => c.Id == "CHECKOUT-NEVER-5XX");
        Assert.Equal(ClauseKind.Never, checkout.Kind);
        Assert.Equal(["POST"], checkout.When!.Method);
        Assert.Equal(PredicateOp.Gte, checkout.Assert[0].Op);
        Assert.Equal(500, checkout.Assert[0].Value!.GetValue<long>());
        Assert.False(loaded.Document.Clauses.Single(c => c.Id == "PRODUCTS-PRICED").IsAccepted);
    }

    public static TheoryData<string, string, string, string, string> BrokenContracts => new()
    {
        { "an unknown top-level key", "kind: Contract", "kind: Contract\nsurprise: true", "/surprise", "'surprise' is not allowed here" },
        { "a predicate with both value and ref", "{ select: response.status, op: gte, value: 500 }", "{ select: response.status, op: gte, value: 500, ref: response.status }", "/clauses/2/assert/0", "" },
        { "approx without a tolerance", "{ select: response.body.json.total, op: lt, value: 0 }", "{ select: response.body.json.total, op: approx, value: 0 }", "/clauses/0/assert/0", "" },
        { "exists with a value", "{ select: extract.cartTotal, op: exists }", "{ select: extract.cartTotal, op: exists, value: 1 }", "/clauses/1/assert/0", "" },
        { "matches with a number", "op: matches, value: \"^/orders/[0-9a-f-]{36}$\"", "op: matches, value: 42", "/clauses/3/assert/1", "" },
        { "a tolerance with both bounds", "absolute: 0.01\n    - path: response.body.json.items", "absolute: 0.01\n      relative: 0.1\n    - path: response.body.json.items", "/normalize/tolerances/0", "" },
        { "a clause without why", "    why: A 5xx at checkout loses the sale and says nothing the customer can act on.\n", "", "/clauses/2", "why" },
        { "an unknown operator", "op: lt, value: 0", "op: lessThan, value: 0", "/clauses/0/assert/0/op", "" },
        { "a clause id in the wrong shape", "id: TOTAL-NEVER-NEGATIVE", "id: total-never-negative", "/clauses/0/id", "" },
        { "a duplicate clause id", "id: CHECKOUT-NEVER-5XX", "id: TOTAL-NEVER-NEGATIVE", "/clauses/2/id", "clause 'TOTAL-NEVER-NEGATIVE' is defined more than once" },
        { "a reference to an undefined extractor", "{ select: extract.cartTotal, op: exists }", "{ select: extract.grandTotal, op: exists }", "/clauses/1/assert/0/select", "'extract.grandTotal' refers to extractor 'grandTotal', which is not defined under extract:" },
        { "a ref to an undefined extractor", "{ select: extract.cartTotal, op: exists }", "{ select: extract.cartTotal, op: equals, ref: extract.nothing }", "/clauses/1/assert/0/ref", "which is not defined" },
        { "an extract path without a name", "{ select: extract.cartTotal, op: exists }", "{ select: extract, op: exists }", "/clauses/1/assert/0/select", "an extract path names the extractor" },
        { "a clause scope regex that does not compile", "when: { method: POST, path: \"^/checkout$\" }\n    assert:\n      - { select: response.status, op: gte", "when: { method: POST, path: \"^/checkout($\" }\n    assert:\n      - { select: response.status, op: gte", "/clauses/2/when/path", "not a valid regular expression" },
        { "a query regex that does not compile", "when: { method: POST, path: \"^/checkout$\" }\n    assert:\n      - { select: response.status, op: gte", "when: { method: POST, path: \"^/checkout$\", query: { page: \"(\" } }\n    assert:\n      - { select: response.status, op: gte", "/clauses/2/when/query/page", "not a valid regular expression" },
        { "a matches regex that does not compile", "op: matches, value: \"^/orders/[0-9a-f-]{36}$\"", "op: matches, value: \"(\"", "/clauses/3/assert/1/value", "not a valid regular expression" },
        { "a path that does not parse", "select: response.body.json.**.cardNumber", "select: response.body.json.**", "/clauses/4/assert/0/select", "** must be followed by a segment" },
        { "an ignore path that does not parse", "    - response.body.json.generatedAt", "    - response.body.json..generatedAt", "/normalize/ignore/0", "expected a name" },
        { "a tolerance path that does not parse", "    - path: response.body.json.total", "    - path: response.body.json.total[", "/normalize/tolerances/0/path", "unterminated" },
        { "between with min above max", "{ select: response.body.json.total, op: lt, value: 0 }", "{ select: response.body.json.total, op: between, min: 5, max: 1 }", "/clauses/0/assert/0", "between: min 5 is greater than max 1" },
        { "an unknown culture", "    as: decimal\n  - name: cartLineTotals", "    as: decimal\n    culture: xx-QQ\n  - name: cartLineTotals", "/extract/0/culture", "'xx-QQ' is not a culture this runtime knows" },
        { "an extractor text pattern without a group", "    from: html\n    selector: \".order-total\"", "    from: text\n    selector: \"Total: [0-9.]+\"", "/extract/0/selector", "needs a capture group" },
        { "an extractor regex without a group", "    selector: \".order-total\"", "    selector: \".order-total\"\n    regex: \"[0-9.]+\"", "/extract/0/regex", "needs a capture group" },
        { "an extractor scope regex that does not compile", "    when: { method: GET, path: \"^/cart$\" }\n    from: html\n    selector: \".order-total\"", "    when: { method: GET, path: \"^/cart($\" }\n    from: html\n    selector: \".order-total\"", "/extract/0/when/path", "not a valid regular expression" },
        { "a duplicate extractor name", "  - name: cartLineTotals", "  - name: cartTotal", "/extract/1/name", "extractor 'cartTotal' is defined more than once" },
        { "an attribute on a non-html extractor", "    from: html\n    selector: \".order-total\"", "    from: json\n    selector: total\n    attribute: href", "/extract/0", "" },
        { "a json extractor path that does not parse", "    from: html\n    selector: \".order-total\"", "    from: json\n    selector: \"items[\"", "/extract/0/selector", "unterminated" },
        { "a duplicate YAML key", "  revision: 1\n", "  revision: 1\n  revision: 2\n", "", "Duplicate key revision" },
        { "a comparison on a whole list", "{ select: extract.cartLineTotals, op: countAtLeast, value: 1 }", "{ select: extract.cartLineTotals, op: gt, value: 0 }", "/clauses/1/assert/1/select", "'extract.cartLineTotals' is the whole list that extractor 'cartLineTotals' collects (all: true), but 'gt' tests one value" },
        { "a path that does not parse, under a single-value operator", "{ select: response.body.json.total, op: lt, value: 0 }", "{ select: \"response.body.json.total[\", op: lt, value: 0 }", "/clauses/0/assert/0/select", "unterminated" },
        { "a reference to a whole list", "{ select: response.body.json.total, op: lt, value: 0 }", "{ select: response.body.json.total, op: lt, ref: extract.cartLineTotals }", "/clauses/0/assert/0/ref", "Test each value with 'extract.cartLineTotals[*]'" },
    };

    [Fact]
    public void A_contract_without_extractors_validates()
    {
        const string yaml = """
            apiVersion: secondkey/v1
            kind: Contract
            metadata:
              name: no-extractors
              revision: 1
            clauses:
              - id: CHECKOUT-NEVER-5XX
                kind: never
                title: Checkout never answers with a server error
                why: A 5xx at checkout loses the sale and says nothing the customer can act on.
                when: { method: POST, path: "^/checkout$" }
                assert:
                  - { select: response.status, op: gte, value: 500 }
                provenance: { origin: designed, status: accepted, author: sample, date: 2026-09-26 }
            """;

        var report = ContractFile.ValidateText(yaml, "no extractors");

        Assert.True(report.IsValid, report.ToString());
    }

    [Theory]
    [InlineData("lt", "value: 0")]
    [InlineData("lte", "value: 0")]
    [InlineData("gt", "value: 0")]
    [InlineData("gte", "value: 0")]
    [InlineData("between", "min: 0, max: 1")]
    [InlineData("approx", "value: 0, absolute: 1")]
    [InlineData("matches", "value: \"^-\"")]
    [InlineData("notMatches", "value: \"^-\"")]
    public void Every_single_value_operator_is_refused_on_a_whole_list(string op, string operand)
    {
        var yaml = SampleYaml.Replace("{ select: extract.cartLineTotals, op: countAtLeast, value: 1 }", $"{{ select: extract.cartLineTotals, op: {op}, {operand} }}", StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, op);

        var error = Assert.Single(report.Errors);
        Assert.Equal("/clauses/1/assert/1/select", error.Location);
        Assert.Contains($"but '{op}' tests one value", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ select: \"extract.cartLineTotals[*]\", op: gt, value: 0 }")]
    [InlineData("{ select: \"extract.cartLineTotals[*]\", op: between, min: 0, max: 1000 }")]
    [InlineData("{ select: \"extract.cartLineTotals[0]\", op: gte, value: 0 }")]
    [InlineData("{ select: extract.cartLineTotals, op: contains, value: 19.99 }")]
    [InlineData("{ select: extract.cartLineTotals, op: countAtMost, value: 50 }")]
    [InlineData("{ select: extract.cartTotal, op: gt, value: 0 }")]
    public void A_list_is_counted_searched_or_tested_value_by_value(string assertion)
    {
        var yaml = SampleYaml.Replace("{ select: extract.cartLineTotals, op: countAtLeast, value: 1 }", assertion, StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, assertion);

        Assert.True(report.IsValid, report.ToString());
    }

    [Theory]
    [MemberData(nameof(BrokenContracts))]
    public void A_broken_contract_is_refused_with_a_located_error(string why, string find, string replace, string location, string message)
    {
        Assert.Contains(find, SampleYaml, StringComparison.Ordinal);
        var yaml = SampleYaml.Replace(find, replace, StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, why);

        Assert.False(report.IsValid, why);
        Assert.Contains(report.Errors, e =>
            e.Location.StartsWith(location, StringComparison.Ordinal) && e.Message.Contains(message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_sets_path_and_a_mask_pattern_are_checked_too()
    {
        var yaml = SampleYaml.Replace(
            "  masks:\n",
            "  sets:\n    - { path: \"response.body.json.items[\", key: sku }\n    - { path: response.body.json.items, key: \"sku[\" }\n  masks:\n    patterns:\n      - { name: csrf, regex: \"(\", replacement: x }\n",
            StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, "sets and masks");

        Assert.Contains(report.Errors, e => e.Location == "/normalize/sets/0/path" && e.Message.Contains("unterminated", StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Location == "/normalize/sets/1/key" && e.Message.Contains("unterminated", StringComparison.Ordinal));
        Assert.Contains(report.Errors, e => e.Location == "/normalize/masks/patterns/0/regex" && e.Message.StartsWith("not a valid regular expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Loading_an_invalid_contract_file_throws_with_its_errors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sk-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, SampleYaml.Replace("kind: Contract", "kind: Agreement", StringComparison.Ordinal));
        try
        {
            var ex = Assert.Throws<ArtifactValidationException>(() => ContractFile.Load(path));

            Assert.Equal(path, ex.Report.Artifact);
            Assert.Contains(ex.Report.Errors, e => e.Location == "/kind");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Loading_from_text_computes_the_digest_of_the_text()
    {
        var fromText = ContractFile.LoadText(SampleYaml, "text");
        var fromFile = ContractFile.Load(RepoPaths.Sample("contract.yaml"));

        Assert.Equal(fromFile.Sha256, fromText.Sha256);
        Assert.Null(fromText.Path);
        Assert.Equal(RepoPaths.Sample("contract.yaml"), fromFile.Path);
    }

    [Fact]
    public void A_contract_without_an_accepted_never_clause_is_refused()
    {
        var yaml = SampleYaml.Replace("kind: never", "kind: must", StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, "no absence");

        Assert.Contains(report.Errors, e => e.Message.Contains("no accepted 'never' clause", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proposed_never_clause_does_not_satisfy_the_absence_rule()
    {
        var yaml = SampleYaml
            .Replace("kind: never", "kind: must", StringComparison.Ordinal)
            .Replace("kind: must\n    title: A cart or order total is never negative", "kind: never\n    title: A cart or order total is never negative", StringComparison.Ordinal)
            .Replace("provenance: { origin: designed, status: accepted, author: sample, date: 2026-09-26 }\n\n  - id: CART-SHOWS", "provenance: { origin: designed, status: proposed }\n\n  - id: CART-SHOWS", StringComparison.Ordinal);

        var report = ContractFile.ValidateText(yaml, "only a proposed absence");

        Assert.Contains(report.Errors, e => e.Message.Contains("no accepted 'never' clause", StringComparison.Ordinal));
    }

    [Fact]
    public void Loading_an_invalid_contract_throws()
    {
        Assert.Throws<ArtifactValidationException>(() => ContractFile.LoadText("apiVersion: secondkey/v1\n", "partial"));
    }

    [Fact]
    public void Text_that_is_not_a_mapping_is_refused()
    {
        var report = ContractFile.ValidateText("- just\n- a list\n", "list");

        Assert.Contains("a contract is a YAML mapping", report.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_yaml_syntax_error_is_reported_with_its_line()
    {
        var report = ContractFile.ValidateText("apiVersion: secondkey/v1\nkind: [unclosed\n", "syntax");

        Assert.False(report.IsValid);
        Assert.StartsWith("not valid YAML", report.Errors.Single().Message, StringComparison.Ordinal);
        Assert.NotNull(report.Errors.Single().Line);
    }
}
