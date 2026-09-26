using System.Text.Json.Nodes;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Runs;
using SecondKey.Artifacts.Verdicts;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class ContractEngineTests
{
    private static readonly ContractEngine Engine = ContractEngine.Compile(SampleContract().Document);

    private static ClauseSummary Summary(ContractReport report, string id) => report.Clauses.Single(c => c.Id == id);

    [Fact]
    public void The_sample_contract_against_the_sample_run_gives_the_expected_statuses()
    {
        var report = Engine.EvaluateRun(SampleRun());

        Assert.Equal(ClauseStatus.Held, Summary(report, "TOTAL-NEVER-NEGATIVE").Status);
        Assert.Equal(ClauseStatus.Held, Summary(report, "CART-TOTAL-MATCHES-LINES").Status);
        Assert.Equal(ClauseStatus.Fixed, Summary(report, "CHECKOUT-NEVER-5XX").Status);
        Assert.Equal(ClauseStatus.ViolatedBoth, Summary(report, "ORDER-HAS-LOCATION").Status);
        Assert.Equal(ClauseStatus.Held, Summary(report, "NO-CARD-NUMBER-IN-RESPONSES").Status);
        Assert.Equal(ClauseStatus.Held, Summary(report, "PRODUCTS-PRICED").Status);
    }

    [Fact]
    public void Tallies_count_each_side_separately_and_scope_limits_exercise()
    {
        var report = Engine.EvaluateRun(SampleRun());

        var checkout = Summary(report, "CHECKOUT-NEVER-5XX");
        Assert.Equal(2, checkout.Exercised);
        Assert.Equal(new Tally { Pass = 1, Fail = 1, Error = 0 }, checkout.Legacy);
        Assert.Equal(new Tally { Pass = 2, Fail = 0, Error = 0 }, checkout.Candidate);
        Assert.Equal(5, Summary(report, "TOTAL-NEVER-NEGATIVE").Exercised);
        Assert.Equal(1, Summary(report, "CART-TOTAL-MATCHES-LINES").Exercised);
        Assert.False(Summary(report, "PRODUCTS-PRICED").Accepted);
        Assert.True(Summary(report, "CHECKOUT-NEVER-5XX").Accepted);
        Assert.Equal("never", checkout.Kind);
        Assert.Equal("must", Summary(report, "ORDER-HAS-LOCATION").Kind);
    }

    [Fact]
    public void Every_exchange_and_side_has_one_outcome_per_clause()
    {
        var report = Engine.EvaluateRun(SampleRun());

        Assert.Equal(10, report.Outcomes.Count);
        Assert.All(report.Outcomes.Values, list => Assert.Equal(6, list.Count));
        Assert.Equal(ClauseOutcome.Fail, report.Outcomes[("ex-000005", Side.Legacy)][2].Outcome);
        Assert.Equal("response.status = 500", report.Outcomes[("ex-000005", Side.Legacy)][2].Detail);
        Assert.Equal(ClauseOutcome.NotApplicable, report.Outcomes[("ex-000001", Side.Legacy)][2].Outcome);
    }

    /// <summary>
    /// Deliberately broken candidates, each with the clause that must catch it — the
    /// BrokenAgents discipline from agent-eval-bench. A variant that survives is a missing
    /// clause, not a curiosity.
    /// </summary>
    public static TheoryData<string, string, Func<ExchangeResult, ExchangeResult>, string> BrokenCandidates => new()
    {
        {
            "a discount stacked past the price", "ex-000002",
            r => r with { Response = JsonResponse(200, """{ "items": [], "discount": 80, "total": -20.03 }""") },
            "TOTAL-NEVER-NEGATIVE"
        },
        {
            "the order is created but its location is lost", "ex-000004",
            r => r with { Response = r.Response! with { Headers = new Dictionary<string, string[]> { ["content-type"] = ["application/json"] } } },
            "ORDER-HAS-LOCATION"
        },
        {
            "checkout crashes", "ex-000004",
            r => r with { Response = new HttpResponseRecord { Status = 503, Headers = new Dictionary<string, string[]>() } },
            "CHECKOUT-NEVER-5XX"
        },
        {
            "a card number leaks into an order response", "ex-000004",
            r => r with { Response = JsonResponse(201, """{ "orderId": "x", "payment": { "cardNumber": "4111111111111111" } }""", new() { ["location"] = ["/orders/6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40"] }) },
            "NO-CARD-NUMBER-IN-RESPONSES"
        },
        {
            "the cart page loses its total", "ex-000003",
            r => r with { Response = HtmlResponse(200, "<table class=\"lines\"><tr><td class=\"line-total\">59.97</td></tr></table>") },
            "CART-TOTAL-MATCHES-LINES"
        },
        {
            "the candidate does not answer at all", "ex-000004",
            r => r with { Response = null, Error = new ExchangeError { Kind = "connection", Message = "refused" } },
            "ORDER-HAS-LOCATION"
        },
    };

    [Theory]
    [MemberData(nameof(BrokenCandidates))]
    public void Every_broken_candidate_is_caught_by_its_clause(string why, string exchange, Func<ExchangeResult, ExchangeResult> breakIt, string clause)
    {
        var report = Engine.EvaluateRun(WithCandidate(SampleRun(), exchange, breakIt));

        Assert.True(Summary(report, clause).Status == ClauseStatus.Regressed, $"{why}: {clause} is {Summary(report, clause).Status}");
    }

    [Fact]
    public void A_clause_whose_scope_never_matches_is_unexercised_not_passed()
    {
        var contract = SampleContract().Document;
        var clauses = contract.Clauses.Select(c => c.Id == "CART-TOTAL-MATCHES-LINES" ? c with { When = c.When! with { Path = "^/basket$" } } : c).ToList();

        var report = ContractEngine.Compile(contract with { Clauses = clauses }).EvaluateRun(SampleRun());

        var summary = Summary(report, "CART-TOTAL-MATCHES-LINES");
        Assert.Equal(ClauseStatus.Unexercised, summary.Status);
        Assert.Equal(0, summary.Exercised);
        Assert.Equal(new Tally { Pass = 0, Fail = 0, Error = 0 }, summary.Legacy);
    }

    [Fact]
    public void A_clause_broken_only_on_the_candidate_for_an_exchange_legacy_never_answered_is_a_regression()
    {
        var run = SampleRun();
        var trimmed = run with { Results = run.Results.Where(r => !(r.Side == Side.Legacy && r.Exchange == "ex-000002")).ToList() };
        var broken = WithCandidate(trimmed, "ex-000002", r => r with { Response = JsonResponse(200, """{ "total": -1 }""") });

        var report = Engine.EvaluateRun(broken);

        Assert.Equal(ClauseStatus.Regressed, Summary(report, "TOTAL-NEVER-NEGATIVE").Status);
    }

    [Fact]
    public void Evaluation_is_deterministic()
    {
        var first = Engine.EvaluateRun(SampleRun());
        var second = ContractEngine.Compile(SampleContract().Document).EvaluateRun(SampleRun());

        Assert.Equal(first.Clauses, second.Clauses);
    }

    [Fact]
    public void Compiling_null_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => ContractEngine.Compile(null!));
        Assert.Throws<ArgumentNullException>(() => Engine.EvaluateRun(null!));
    }
}
