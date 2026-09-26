using SecondKey.Artifacts.Verdicts;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class ClauseTests
{
    private static CompiledClause Clause(string kind, string assert, string? when = null)
    {
        var contract = ContractOf($$"""
            apiVersion: secondkey/v1
            kind: Contract
            metadata: { name: x, revision: 1 }
            clauses:
              - id: UNDER-TEST
                kind: {{kind}}
                title: under test
                why: a clause under test, with a reason long enough
                {{(when is null ? "" : "when: " + when)}}
                assert: {{assert}}
                provenance: { origin: designed, status: accepted }
              - id: KEEP-VALID
                kind: never
                title: keeps the contract loadable
                why: a contract needs one never clause to load at all
                assert: [ { select: response.status, op: gte, value: 599 } ]
                provenance: { origin: designed, status: accepted }
            """);
        return ContractEngine.Compile(contract).Clauses[0];
    }

    private static readonly Observations.Observation Created = Observe(Request("POST", "/checkout", "?src=web"), JsonResponse(201, """{ "total": 10 }"""));

    [Fact]
    public void A_must_clause_passes_when_every_assertion_holds()
    {
        var clause = Clause("must", "[ { select: response.status, op: equals, value: 201 }, { select: response.body.json.total, op: gt, value: 0 } ]");

        Assert.Equal(new ClauseEvaluation(ClauseOutcome.Pass, null), clause.Evaluate(Created));
    }

    [Fact]
    public void A_must_clause_fails_with_the_first_assertion_that_does_not_hold()
    {
        var clause = Clause("must", "[ { select: response.status, op: equals, value: 201 }, { select: response.body.json.total, op: gt, value: 99 } ]");

        Assert.Equal(new ClauseEvaluation(ClauseOutcome.Fail, "response.body.json.total = 10"), clause.Evaluate(Created));
    }

    [Fact]
    public void A_never_clause_fails_only_when_all_its_assertions_hold_together()
    {
        var both = Clause("never", "[ { select: response.status, op: equals, value: 201 }, { select: response.body.json.total, op: lt, value: 100 } ]");
        var one = Clause("never", "[ { select: response.status, op: equals, value: 201 }, { select: response.body.json.total, op: lt, value: 1 } ]");

        Assert.Equal(new ClauseEvaluation(ClauseOutcome.Fail, "response.status = 201; response.body.json.total = 10"), both.Evaluate(Created));
        Assert.Equal(new ClauseEvaluation(ClauseOutcome.Pass, null), one.Evaluate(Created));
    }

    [Fact]
    public void A_never_clause_about_a_missing_value_passes()
    {
        var clause = Clause("never", "[ { select: response.body.json.discount, op: lt, value: 0 } ]");

        Assert.Equal(ClauseOutcome.Pass, clause.Evaluate(Created).Outcome);
    }

    [Fact]
    public void A_clause_out_of_scope_is_not_applicable()
    {
        Assert.Equal(ClauseEvaluation.NotApplicable, Clause("must", "[ { select: response.status, op: exists } ]", "{ method: GET }").Evaluate(Created));
        Assert.Equal(ClauseEvaluation.NotApplicable, Clause("must", "[ { select: response.status, op: exists } ]", "{ path: \"^/cart$\" }").Evaluate(Created));
        Assert.Equal(ClauseEvaluation.NotApplicable, Clause("must", "[ { select: response.status, op: exists } ]", "{ query: { src: \"^app$\" } }").Evaluate(Created));
        Assert.Equal(ClauseEvaluation.NotApplicable, Clause("must", "[ { select: response.status, op: exists } ]", "{ query: { missing: \".*\" } }").Evaluate(Created));
    }

    [Fact]
    public void A_clause_in_scope_is_evaluated()
    {
        var clause = Clause("must", "[ { select: response.status, op: exists } ]", "{ method: [GET, POST], path: \"^/check\", query: { src: \"^w\" } }");

        Assert.Equal(ClauseOutcome.Pass, clause.Evaluate(Created).Outcome);
    }

    [Fact]
    public void A_predicate_error_makes_the_clause_an_error_whatever_its_kind()
    {
        var must = Clause("must", "[ { select: response.body.json.total, op: equals, ref: response.body.json.none } ]");
        var never = Clause("never", "[ { select: response.body.json.total, op: equals, ref: response.body.json.none } ]");

        Assert.Equal(ClauseOutcome.Error, must.Evaluate(Created).Outcome);
        Assert.Equal(ClauseOutcome.Error, never.Evaluate(Created).Outcome);
        Assert.StartsWith("ref response.body.json.none selected 0 values", must.Evaluate(Created).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clause_exposes_its_id_scope_and_text()
    {
        var clause = Clause("never", "[ { select: response.status, op: gte, value: 500 } ]", "{ method: POST, path: \"^/checkout$\" }");

        Assert.Equal("UNDER-TEST", clause.Id);
        Assert.Equal("For POST /checkout: it never happens that response.status is at least 500.", clause.Text);
        Assert.Single(clause.Predicates);
        Assert.NotNull(clause.When.Source);
    }
}
