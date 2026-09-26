using System.Text.Json.Nodes;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using SecondKey.Contract;
using static SecondKey.Compare.Tests.Fixtures;

namespace SecondKey.Compare.Tests;

/// <summary>S13: every exchange in exactly one of four classes, in the order ADR 0003 fixes.</summary>
public class ComparatorTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 10, 5, 0, TimeSpan.Zero);

    /// <summary>A contract for the tests below: a total that must not go negative, a checkout that must not fail, and some normalization.</summary>
    private static readonly ContractDocument Shop = ContractOf("""
        apiVersion: secondkey/v1
        kind: Contract
        metadata: { name: shop, revision: 3 }
        normalize:
          ignore: [response.body.json.generatedAt]
          masks: { guids: true }
        clauses:
          - id: TOTAL-NEVER-NEGATIVE
            kind: never
            title: A total is never negative
            why: The shop would pay the customer.
            assert:
              - { select: response.body.json.total, op: lt, value: 0 }
            provenance: { origin: designed, status: accepted }
          - id: CHECKOUT-NEVER-5XX
            kind: never
            title: Checkout never fails
            why: A failed checkout loses the sale.
            when: { method: POST, path: "^/checkout$" }
            assert:
              - { select: response.status, op: gte, value: 500 }
            provenance: { origin: incident, status: accepted }
          - id: PAGE-SAYS-THANK-YOU
            kind: must
            title: The receipt thanks the customer
            why: Politeness is a requirement here.
            when: { method: POST, path: "^/checkout$" }
            assert:
              - { select: response.body.text, op: contains, value: "thank you" }
            provenance: { origin: mined, status: proposed }
        """);

    private static VerdictDocument Compare(RunDocument run, ContractDocument? contract = null) =>
        Comparator.Compare(InputsOf(contract ?? Shop, run), At);

    private static ExchangeVerdict Only(RunDocument run, ContractDocument? contract = null) => Assert.Single(Compare(run, contract).Exchanges);

    private static readonly Artifacts.Capture.HttpRequestRecord Checkout = Request("POST", "/checkout");

    // ---- the sample: the done-when of S13 ------------------------------------------------

    [Fact]
    public void The_sample_contract_and_run_give_the_sample_verdict()
    {
        var sample = VerdictFile.Read(Sample("verdict.json"));
        var contract = SampleContract();

        var verdict = Comparator.Compare(
            new ComparisonInputs
            {
                Contract = contract.Document,
                ContractSha256 = sample.Inputs.Contract.Sha256,
                ContractPath = "contract.yaml",
                Run = SampleRun(),
                RunSha256 = sample.Inputs.Run.Sha256,
                RunPath = "sample.skrun",
            },
            sample.CreatedAt) with { Tool = sample.Tool };

        Assert.Equal(File.ReadAllText(Sample("verdict.json")), VerdictFile.Serialize(verdict));
    }

    [Fact]
    public void The_sample_run_has_a_regression_and_a_fix_candidate()
    {
        var verdict = Comparator.Compare(InputsOf(SampleContract().Document, SampleRun()), At);

        var products = verdict.Exchanges.Single(e => e.Exchange == "ex-000001");
        var emptyCheckout = verdict.Exchanges.Single(e => e.Exchange == "ex-000005");
        Assert.Equal(ExchangeClass.Regression, products.Class);
        Assert.Equal(["uncovered-difference"], products.Reasons);
        Assert.Equal(ExchangeClass.FixCandidate, emptyCheckout.Class);
        Assert.Equal(["clause-fixed: CHECKOUT-NEVER-5XX"], emptyCheckout.Reasons);
        Assert.Equal(VerdictOutcome.Fail, verdict.Outcome);
        Assert.True(VerdictFile.Validate(VerdictFile.Serialize(verdict), "computed").IsValid);
    }

    // ---- the four classes and their order -------------------------------------------------

    [Fact]
    public void Identical_answers_are_equal()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(200, """{ "total": 1.50 }"""), JsonResponse(200, """{ "total": 1.5 }"""))));

        Assert.Equal(ExchangeClass.Equal, exchange.Class);
        Assert.Equal(["identical"], exchange.Reasons);
        Assert.Empty(exchange.Diffs);
    }

    [Fact]
    public void Answers_equal_once_the_contracts_rules_apply_are_equal_under_contract_with_each_rule_named_once()
    {
        var exchange = Only(RunOf(Pair(
            "ex-1",
            JsonResponse(200, """{ "id": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "ref": "0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b", "generatedAt": "a", "total": 1 }"""),
            JsonResponse(200, """{ "id": "0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b", "ref": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "generatedAt": "b", "total": 1 }"""))));

        Assert.Equal(ExchangeClass.EqualUnderContract, exchange.Class);
        Assert.Equal(["normalized: ignore: response.body.json.generatedAt", "normalized: masks.guids"], exchange.Reasons);
        Assert.Equal(3, exchange.Diffs.Count);
        Assert.All(exchange.Diffs, d => Assert.NotNull(d.NormalizedBy));
    }

    [Fact]
    public void A_difference_nothing_explains_is_a_regression_and_fails_closed()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(200, """{ "total": 1 }"""), JsonResponse(200, """{ "total": 2 }"""))));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["uncovered-difference"], exchange.Reasons);
        var difference = Assert.Single(exchange.Diffs);
        Assert.Equal("response.body.json.total", difference.Path);
        Assert.Null(difference.NormalizedBy);
    }

    [Fact]
    public void A_clause_kept_by_legacy_and_broken_by_the_candidate_is_a_regression_whatever_else_is_true()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(200, """{ "total": 1 }"""), JsonResponse(200, """{ "total": -1 }"""))));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["clause-regression: TOTAL-NEVER-NEGATIVE"], exchange.Reasons);
        var clause = exchange.Clauses.Single(c => c.Id == "TOTAL-NEVER-NEGATIVE");
        Assert.Equal((ClauseOutcome.Pass, ClauseOutcome.Fail), (clause.Legacy, clause.Candidate));
    }

    /// <summary>An accepted (or proposed) clause on a header the comparison does not look at.</summary>
    private static ContractDocument FramingContract(string status = "accepted") => ContractWith(extraClauses: $$"""
          - id: NO-FRAMING
            kind: must
            title: Pages refuse to be framed
            why: Clickjacking.
            assert:
              - { select: "response.headers.x-frame-options[0]", op: equals, value: DENY }
            provenance: { origin: review, status: {{status}} }
        """);

    private static Artifacts.Capture.HttpResponseRecord Framed(bool deny) =>
        JsonResponse(200, "{}", deny ? new Dictionary<string, string[]> { ["x-frame-options"] = ["DENY"] } : new Dictionary<string, string[]>());

    [Fact]
    public void A_clause_regression_is_found_even_when_the_compared_documents_are_identical()
    {
        var exchange = Only(RunOf(Pair("ex-1", Framed(deny: true), Framed(deny: false))), FramingContract());

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["clause-regression: NO-FRAMING"], exchange.Reasons);
        Assert.Empty(exchange.Diffs);
    }

    [Fact]
    public void A_clause_broken_by_legacy_and_kept_by_the_candidate_is_a_fix_candidate()
    {
        var exchange = Only(RunOf(Pair("ex-1", TextResponse(500, "boom"), JsonResponse(400, """{ "error": "cart-empty" }"""), Checkout)));

        Assert.Equal(ExchangeClass.FixCandidate, exchange.Class);
        Assert.Equal(["clause-fixed: CHECKOUT-NEVER-5XX"], exchange.Reasons);
        Assert.NotEmpty(exchange.Diffs);
    }

    [Fact]
    public void A_fix_is_put_to_a_person_even_when_the_compared_documents_are_identical()
    {
        // ADR 0003, amended by S13: a clause can read what the comparison does not (here a header
        // outside compare.headers); its flip must still reach a person rather than pass as equal.
        var verdict = Compare(RunOf(Pair("ex-1", Framed(deny: false), Framed(deny: true))), FramingContract());

        var exchange = Assert.Single(verdict.Exchanges);
        Assert.Equal(ExchangeClass.FixCandidate, exchange.Class);
        Assert.Equal(["clause-fixed: NO-FRAMING"], exchange.Reasons);
        Assert.Empty(exchange.Diffs);
        Assert.Equal(VerdictOutcome.Review, verdict.Outcome);
    }

    [Fact]
    public void A_regression_outranks_a_fix_in_the_same_exchange()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(500, """{ "total": 1 }"""), JsonResponse(200, """{ "total": -1 }"""), Checkout)));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["clause-regression: TOTAL-NEVER-NEGATIVE"], exchange.Reasons);
    }

    [Fact]
    public void A_clause_broken_on_both_sides_decides_nothing()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(500, """{ "total": -1 }"""), JsonResponse(500, """{ "total": -1 }"""), Checkout)));

        Assert.Equal(ExchangeClass.Equal, exchange.Class);
        Assert.All(exchange.Clauses.Where(c => c.Id != "PAGE-SAYS-THANK-YOU"), c => Assert.Equal((ClauseOutcome.Fail, ClauseOutcome.Fail), (c.Legacy, c.Candidate)));
    }

    [Fact]
    public void A_clause_that_cannot_be_evaluated_on_the_candidate_counts_against_it()
    {
        var contract = ContractWith(
            """
            extract:
              - { name: total, from: json, selector: total, as: decimal }
            """,
            """
              - id: CART-TOTAL-PRESENT
                kind: must
                title: The cart shows a total
                why: Customers must see what they pay.
                assert:
                  - { select: extract.total, op: gte, value: 0 }
                provenance: { origin: designed, status: accepted }
            """);
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(200, """{ "total": "1.00" }"""), JsonResponse(200, """{ "total": "one" }"""))), contract);

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["clause-regression: CART-TOTAL-PRESENT"], exchange.Reasons);
        Assert.Equal(ClauseOutcome.Error, exchange.Clauses.Single(c => c.Id == "CART-TOTAL-PRESENT").Candidate);
    }

    [Fact]
    public void Proposed_clauses_are_reported_and_decide_nothing()
    {
        var exchange = Only(RunOf(Pair("ex-1", TextResponse(200, "thank you"), TextResponse(200, "bye"), Checkout)));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["uncovered-difference"], exchange.Reasons);
        var proposed = exchange.Clauses.Single(c => c.Id == "PAGE-SAYS-THANK-YOU");
        Assert.Equal((ClauseOutcome.Pass, ClauseOutcome.Fail), (proposed.Legacy, proposed.Candidate));
    }

    [Fact]
    public void Proposed_fixes_decide_nothing_either()
    {
        var exchange = Only(RunOf(Pair("ex-1", Framed(deny: false), Framed(deny: true))), FramingContract("proposed"));

        Assert.Equal(ExchangeClass.Equal, exchange.Class);
        Assert.Equal((ClauseOutcome.Fail, ClauseOutcome.Pass), (exchange.Clauses.Single(c => c.Id == "NO-FRAMING").Legacy, exchange.Clauses.Single(c => c.Id == "NO-FRAMING").Candidate));
    }

    // ---- sides without an answer ----------------------------------------------------------

    [Theory]
    [InlineData(true, false, "missing-result: candidate (connection)")]
    [InlineData(false, true, "missing-result: legacy (timeout)")]
    public void A_side_without_an_answer_is_a_regression(bool legacyAnswers, bool candidateAnswers, string reason)
    {
        var answer = JsonResponse(200, """{ "total": 1 }""");
        var exchange = Only(RunOf(Pair("ex-1", legacyAnswers ? answer : null, candidateAnswers ? answer : null)));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal([reason], exchange.Reasons);
        Assert.Contains(exchange.Diffs, d => d.Path == "error");
    }

    [Fact]
    public void Two_sides_that_both_failed_to_answer_are_not_equal()
    {
        var exchange = Only(RunOf(Pair("ex-1", null, null)));

        Assert.Equal(ExchangeClass.Regression, exchange.Class);
        Assert.Equal(["missing-result: legacy (timeout)", "missing-result: candidate (connection)"], exchange.Reasons);
    }

    [Fact]
    public void A_failure_without_a_kind_is_still_named()
    {
        var run = RunOf(Result("ex-1", Side.Legacy, JsonResponse(200, "{}")), Result("ex-1", Side.Candidate, null));

        Assert.Equal(["missing-result: candidate (no answer)"], Only(run).Reasons);
    }

    [Fact]
    public void A_side_that_was_never_replayed_is_a_regression()
    {
        var legacyOnly = RunOf(Result("ex-1", Side.Legacy, JsonResponse(200, "{}")));
        var candidateOnly = RunOf(Result("ex-1", Side.Candidate, JsonResponse(200, "{}"), request: Request("GET", "/x")));

        var first = Only(legacyOnly);
        var second = Only(candidateOnly);

        Assert.Equal(["missing-result: candidate (not replayed)"], first.Reasons);
        Assert.Equal((ClauseOutcome.Pass, ClauseOutcome.NotApplicable), (first.Clauses[0].Legacy, first.Clauses[0].Candidate));
        Assert.Equal(["missing-result: legacy (not replayed)"], second.Reasons);
        Assert.Equal("/x", second.Request.Path);
        Assert.Contains(first.Diffs, d => d.Path == "response" && d.Kind == DifferenceKind.Removed);
    }

    // ---- what each exchange reports ---------------------------------------------------------

    [Fact]
    public void Only_clauses_in_scope_on_some_side_are_listed_in_contract_order_with_their_details()
    {
        var exchange = Only(RunOf(Pair("ex-1", TextResponse(500, "boom"), JsonResponse(400, """{ "total": -2 }"""), Checkout)));

        Assert.Equal(["TOTAL-NEVER-NEGATIVE", "CHECKOUT-NEVER-5XX", "PAGE-SAYS-THANK-YOU"], exchange.Clauses.Select(c => c.Id));
        Assert.Equal("candidate: response.body.json.total = -2", exchange.Clauses[0].Detail);
        Assert.Equal("legacy: response.status = 500", exchange.Clauses[1].Detail);
        Assert.StartsWith("legacy: ", exchange.Clauses[2].Detail, StringComparison.Ordinal);
        Assert.Contains("; candidate: ", exchange.Clauses[2].Detail, StringComparison.Ordinal);

        var get = Only(RunOf(Pair("ex-2", JsonResponse(200, "{}"), JsonResponse(200, "{}"))));
        Assert.Equal(["TOTAL-NEVER-NEGATIVE"], get.Clauses.Select(c => c.Id));
        Assert.Null(get.Clauses[0].Detail);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("x", null, "legacy: x")]
    [InlineData(null, "y", "candidate: y")]
    [InlineData("x", "x", "both: x")]
    [InlineData("x", "y", "legacy: x; candidate: y")]
    public void A_clause_detail_says_which_side_it_is_about(string? legacy, string? candidate, string? detail)
    {
        Assert.Equal(detail, Comparator.Detail(new ClauseEvaluation(ClauseOutcome.Fail, legacy), new ClauseEvaluation(ClauseOutcome.Fail, candidate)));
    }

    [Fact]
    public void The_request_line_comes_from_the_capture_and_an_empty_query_is_left_out()
    {
        var withQuery = Only(RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"), Request("GET", "/products", "?sort=name"), scenario: "s-9")));
        var emptyQuery = Only(RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"), Request("GET", "/products", ""))));

        Assert.Equal(new RequestLine { Method = "GET", Path = "/products", Query = "?sort=name" }, withQuery.Request);
        Assert.Equal("s-9", withQuery.Scenario);
        Assert.Null(emptyQuery.Request.Query);
    }

    [Fact]
    public void Exchanges_keep_the_order_of_the_run()
    {
        var run = RunOf(Pair("ex-2", JsonResponse(200, "{}"), JsonResponse(200, "{}")).Concat(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"))));

        Assert.Equal(["ex-2", "ex-1"], Compare(run).Exchanges.Select(e => e.Exchange));
    }

    // ---- the run as a whole -----------------------------------------------------------------

    [Fact]
    public void The_summary_counts_every_class_scenarios_and_clauses()
    {
        var run = RunOf(
            Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"), scenario: "s-1")
                .Concat(Pair("ex-2", JsonResponse(200, """{ "generatedAt": 1 }"""), JsonResponse(200, """{ "generatedAt": 2 }"""), scenario: "s-1"))
                .Concat(Pair("ex-3", JsonResponse(200, """{ "total": 1 }"""), JsonResponse(200, """{ "total": 2 }"""), scenario: "s-2"))
                .Concat(Pair("ex-4", TextResponse(500, "x"), TextResponse(400, "x"), Checkout, scenario: "s-2")));

        var verdict = Compare(run);

        Assert.Equal(
            new VerdictSummary
            {
                Scenarios = 2,
                Exchanges = 4,
                Equal = 1,
                EqualUnderContract = 1,
                Regression = 1,
                FixCandidate = 1,
                Clauses = new ClauseCounts { Total = 3, Accepted = 2, Exercised = 2, Unexercised = 0, AbsenceShare = 1 },
            },
            verdict.Summary);
        Assert.Equal(VerdictOutcome.Fail, verdict.Outcome);
        Assert.Equal(["TOTAL-NEVER-NEGATIVE", "CHECKOUT-NEVER-5XX", "PAGE-SAYS-THANK-YOU"], verdict.Clauses.Select(c => c.Id));
    }

    [Fact]
    public void An_accepted_clause_no_request_reached_is_unexercised_and_the_absence_share_is_rounded()
    {
        var contract = ContractWith(extraClauses: """
              - id: MUST-A
                kind: must
                title: Page A answers
                why: Page A is where orders start.
                when: { path: "^/a$" }
                assert: [ { select: response.status, op: equals, value: 200 } ]
                provenance: { origin: designed, status: accepted }
              - id: MUST-B
                kind: must
                title: Page B answers
                why: Page B is where orders end.
                when: { path: "^/b$" }
                assert: [ { select: response.status, op: equals, value: 200 } ]
                provenance: { origin: designed, status: accepted }
            """);

        var verdict = Compare(RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"), Request("GET", "/a"))), contract);

        Assert.Equal(new ClauseCounts { Total = 3, Accepted = 3, Exercised = 2, Unexercised = 1, AbsenceShare = 0.3333 }, verdict.Summary.Clauses);
        Assert.Equal(ClauseStatus.Unexercised, verdict.Clauses.Single(c => c.Id == "MUST-B").Status);
    }

    [Fact]
    public void Without_accepted_clauses_there_is_no_absence_share()
    {
        var contract = Shop with { Clauses = Shop.Clauses.Where(c => !c.IsAccepted).ToList() };

        var verdict = Compare(RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"))), contract);

        Assert.Null(verdict.Summary.Clauses.AbsenceShare);
        Assert.Equal(0, verdict.Summary.Clauses.Accepted);
    }

    [Theory]
    [InlineData(0, 0, VerdictOutcome.Pass)]
    [InlineData(0, 1, VerdictOutcome.Review)]
    [InlineData(1, 0, VerdictOutcome.Fail)]
    [InlineData(1, 1, VerdictOutcome.Fail)]
    public void The_outcome_is_fail_on_any_regression_then_review_on_any_fix(int regressions, int fixes, VerdictOutcome outcome)
    {
        var summary = new VerdictSummary
        {
            Exchanges = 5,
            Equal = 5 - regressions - fixes,
            EqualUnderContract = 0,
            Regression = regressions,
            FixCandidate = fixes,
            Clauses = new ClauseCounts { Total = 1, Accepted = 1, Exercised = 1, Unexercised = 0 },
        };

        Assert.Equal(outcome, Comparator.Outcome(summary));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => Comparator.Compare(null!, At));
        Assert.Throws<ArgumentNullException>(() => Comparator.Outcome(null!));
    }

    [Fact]
    public void A_run_where_everything_matches_passes()
    {
        var verdict = Compare(RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}"))));

        Assert.Equal(VerdictOutcome.Pass, verdict.Outcome);
    }

    [Fact]
    public void The_verdict_names_its_inputs_and_the_time_and_tool_that_made_it()
    {
        var run = RunOf(Pair("ex-1", JsonResponse(200, "{}"), JsonResponse(200, "{}")));
        var inputs = InputsOf(Shop, run) with { ContractPath = "contract.yaml", RunPath = "run.skrun" };

        var verdict = Comparator.Compare(inputs, At);

        Assert.Equal(At, verdict.CreatedAt);
        Assert.Equal(Artifacts.ToolInfo.Current, verdict.Tool);
        Assert.Equal(new ContractInput { Name = "shop", Revision = 3, Sha256 = new string('a', 64), Path = "contract.yaml" }, verdict.Inputs.Contract);
        Assert.Equal(new RunInput { RunId = "run-test", Sha256 = new string('b', 64), Path = "run.skrun" }, verdict.Inputs.Run);
        Assert.Null(verdict.Mutation);
    }

    [Fact]
    public void The_same_inputs_always_give_the_same_verdict()
    {
        var first = VerdictFile.Serialize(Comparator.Compare(InputsOf(SampleContract().Document, SampleRun()), At));
        var second = VerdictFile.Serialize(Comparator.Compare(InputsOf(SampleContract().Document, SampleRun()), At));

        Assert.Equal(first, second);
    }

    [Fact]
    public void The_address_each_side_ran_at_is_not_a_difference()
    {
        var legacy = JsonResponse(201, $$"""{ "self": "{{LegacyUrl}}/orders/7" }""", new Dictionary<string, string[]> { ["location"] = [$"{LegacyUrl}/orders/7"] });
        var candidate = JsonResponse(201, $$"""{ "self": "{{CandidateUrl}}/orders/7" }""", new Dictionary<string, string[]> { ["location"] = [$"{CandidateUrl}/orders/7"] });

        Assert.Equal(ExchangeClass.Equal, Only(RunOf(Pair("ex-1", legacy, candidate))).Class);
    }

    // ---- Classify directly, for the branches the fixtures above reach only together ----------

    [Fact]
    public void Classify_reports_every_regressed_clause_and_every_fixed_clause()
    {
        static ClauseResultPair P(string id, ClauseOutcome l, ClauseOutcome c) => new() { Id = id, Legacy = l, Candidate = c };

        var regressions = Comparator.Classify([], [P("A", ClauseOutcome.Pass, ClauseOutcome.Fail), P("B", ClauseOutcome.Pass, ClauseOutcome.Error), P("C", ClauseOutcome.Fail, ClauseOutcome.Pass)], [], true);
        var fixes = Comparator.Classify([], [P("A", ClauseOutcome.Fail, ClauseOutcome.Pass), P("B", ClauseOutcome.Error, ClauseOutcome.Pass), P("C", ClauseOutcome.NotApplicable, ClauseOutcome.Pass)], [], true);
        var neither = Comparator.Classify([], [P("A", ClauseOutcome.NotApplicable, ClauseOutcome.Fail), P("B", ClauseOutcome.Error, ClauseOutcome.Fail), P("C", ClauseOutcome.Pass, ClauseOutcome.NotApplicable)], [], true);

        Assert.Equal(ExchangeClass.Regression, regressions.Class);
        Assert.Equal(["clause-regression: A", "clause-regression: B"], regressions.Reasons);
        Assert.Equal(ExchangeClass.FixCandidate, fixes.Class);
        Assert.Equal(["clause-fixed: A", "clause-fixed: B"], fixes.Reasons);
        Assert.Equal(ExchangeClass.Equal, neither.Class);
        Assert.Equal(["identical"], neither.Reasons);
    }

    [Fact]
    public void Classify_lists_each_normalization_rule_once_in_order()
    {
        Difference D(string? rule) => new() { Path = "x", Kind = DifferenceKind.Changed, NormalizedBy = rule };

        var (@class, reasons) = Comparator.Classify([], [], [D("masks.timestamps, masks.guids"), D("ignore: a"), D("masks.guids"), D(null)], true);

        Assert.Equal(ExchangeClass.EqualUnderContract, @class);
        Assert.Equal(["normalized: ignore: a", "normalized: masks.guids", "normalized: masks.timestamps"], reasons);
    }

    [Fact]
    public void Classify_puts_a_missing_side_before_everything_else()
    {
        var pair = new ClauseResultPair { Id = "A", Legacy = ClauseOutcome.Pass, Candidate = ClauseOutcome.Fail };

        var (@class, reasons) = Comparator.Classify(["candidate (timeout)"], [pair], [], true);

        Assert.Equal(ExchangeClass.Regression, @class);
        Assert.Equal(["missing-result: candidate (timeout)"], reasons);
    }

    [Fact]
    public void Every_difference_of_an_exchange_equal_under_contract_names_the_rule_that_explains_it()
    {
        var verdict = Comparator.Compare(InputsOf(SampleContract().Document, SampleRun()), At);

        Assert.All(
            verdict.Exchanges.Where(e => e.Class == ExchangeClass.EqualUnderContract).SelectMany(e => e.Diffs),
            d => Assert.False(string.IsNullOrEmpty(d.NormalizedBy), d.Path));
        Assert.All(
            verdict.Exchanges.Where(e => e.Reasons.Contains("uncovered-difference")),
            e => Assert.Contains(e.Diffs, d => d.NormalizedBy is null));
    }

    [Fact]
    public void Values_in_the_verdict_are_as_each_side_answered()
    {
        var exchange = Only(RunOf(Pair("ex-1", JsonResponse(200, """{ "name": "Zebra mug" }"""), JsonResponse(200, """{ "name": "éclair box" }"""))));

        var difference = Assert.Single(exchange.Diffs);
        Assert.Equal(JsonValue.Create("Zebra mug").ToJsonString(), difference.Legacy!.ToJsonString());
        Assert.Equal("éclair box", difference.Candidate!.GetValue<string>());
    }
}
