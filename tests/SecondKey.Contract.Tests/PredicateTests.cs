using SecondKey.Contract.Predicates;
using static SecondKey.Contract.Tests.Fixtures;

namespace SecondKey.Contract.Tests;

public class PredicateTests
{
    private static readonly Observations.Observation Order = Observe(
        Request("POST", "/checkout"),
        JsonResponse(201, """
            { "total": 59.97, "discount": 0, "currency": "PLN", "tags": ["gift", "express"],
              "items": [ { "sku": "A", "price": 19.99 }, { "sku": "B", "price": 0 } ],
              "customer": { "email": "Buyer@Example.com", "note": null } }
            """,
            new() { ["content-type"] = ["application/json"], ["location"] = ["/orders/42"] }));

    public static TheoryData<string, PredicateState> Cases => new()
    {
        { "{ select: response.status, op: equals, value: 201 }", PredicateState.Holds },
        { "{ select: response.status, op: equals, value: 201.0 }", PredicateState.Holds },
        { "{ select: response.status, op: notEquals, value: 201 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: lt, value: 60 }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: lt, value: 59.97 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: lte, value: 59.97 }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: gt, value: 59.97 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: gte, value: 59.97 }", PredicateState.Holds },
        { "{ select: response.body.json.currency, op: gt, value: 1 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: between, min: 50, max: 60 }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: between, min: 60, max: 70 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: approx, value: 60, absolute: 0.05 }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: approx, value: 60, absolute: 0.01 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: approx, value: 60, relative: 0.001 }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: approx, value: 60, relative: 0.0001 }", PredicateState.DoesNotHold },
        { "{ select: \"response.headers.location[0]\", op: matches, value: \"^/orders/[0-9]+$\" }", PredicateState.Holds },
        { "{ select: \"response.headers.location[0]\", op: notMatches, value: \"^/orders/[0-9]+$\" }", PredicateState.DoesNotHold },
        { "{ select: response.status, op: matches, value: \"^2\" }", PredicateState.Holds },
        { "{ select: response.body.json.customer.email, op: matches, value: \"^buyer@\" }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.customer.email, op: matches, value: \"^buyer@\", ignoreCase: true }", PredicateState.Holds },
        { "{ select: response.body.json.customer.email, op: contains, value: \"@Example\" }", PredicateState.Holds },
        { "{ select: response.body.json.customer.email, op: contains, value: \"@example\", ignoreCase: true }", PredicateState.Holds },
        { "{ select: response.body.json.customer.email, op: notContains, value: \"@example\" }", PredicateState.Holds },
        { "{ select: response.body.json.tags, op: contains, value: gift }", PredicateState.Holds },
        { "{ select: response.body.json.tags, op: notContains, value: gift }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.currency, op: in, value: [PLN, EUR] }", PredicateState.Holds },
        { "{ select: response.body.json.currency, op: in, value: [pln], ignoreCase: true }", PredicateState.Holds },
        { "{ select: response.body.json.currency, op: notIn, value: [PLN, EUR] }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.currency, op: notIn, value: [USD] }", PredicateState.Holds },
        { "{ select: response.body.json.customer, op: exists }", PredicateState.Holds },
        { "{ select: response.body.json.customer.note, op: exists }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.customer.note, op: absent }", PredicateState.Holds },
        { "{ select: response.body.json.password, op: absent }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: absent }", PredicateState.DoesNotHold },
        { "{ select: \"response.body.json.items[*]\", op: countEquals, value: 2 }", PredicateState.Holds },
        { "{ select: \"response.body.json.items[*]\", op: countAtLeast, value: 3 }", PredicateState.DoesNotHold },
        { "{ select: \"response.body.json.items[*]\", op: countAtMost, value: 2 }", PredicateState.Holds },
        { "{ select: \"response.body.json.items[*]\", op: countAtMost, value: 1 }", PredicateState.DoesNotHold },
        { "{ select: \"response.body.json.items[*].price\", op: gt, value: 0 }", PredicateState.DoesNotHold },
        { "{ select: \"response.body.json.items[*].price\", op: gt, value: 0, quantifier: any }", PredicateState.Holds },
        { "{ select: \"response.body.json.items[*].price\", op: gt, value: 100, quantifier: none }", PredicateState.Holds },
        { "{ select: \"response.body.json.items[*].price\", op: gt, value: 0, quantifier: none }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.missing, op: gt, value: 0 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.missing, op: gt, value: 0, quantifier: none }", PredicateState.Holds },
        { "{ select: response.body.json.missing, op: gt, value: 0, quantifier: any }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: equals, ref: response.body.json.total }", PredicateState.Holds },
        { "{ select: response.body.json.discount, op: lt, ref: response.body.json.total }", PredicateState.Holds },
        { "{ select: response.body.json.total, op: approx, ref: response.body.json.discount, absolute: 1 }", PredicateState.DoesNotHold },
        { "{ select: response.body.json.total, op: equals, ref: \"response.body.json.items[*].price\" }", PredicateState.Error },
        { "{ select: response.body.json.total, op: equals, ref: response.body.json.nothing }", PredicateState.Error },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void An_operator_holds_exactly_when_it_should(string yaml, PredicateState expected)
    {
        var predicate = new CompiledPredicate(Predicate(yaml));

        Assert.Equal(expected, predicate.Evaluate(Order).State);
    }

    [Theory]
    [InlineData("[]", "countAtLeast", 1, PredicateState.DoesNotHold)]
    [InlineData("[1, 2]", "countEquals", 2, PredicateState.Holds)]
    [InlineData("[1, 2]", "countAtMost", 1, PredicateState.DoesNotHold)]
    public void Counting_a_single_array_counts_its_elements(string array, string op, int value, PredicateState expected)
    {
        var observation = Observe(Request(), JsonResponse(200, $$"""{ "lines": {{array}} }"""));

        var result = new CompiledPredicate(Predicate($"{{ select: response.body.json.lines, op: {op}, value: {value} }}")).Evaluate(observation);

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Counting_through_a_wildcard_counts_what_the_wildcard_selects()
    {
        var observation = Observe(Request(), JsonResponse(200, """{ "groups": [ [1, 2], [3] ] }"""));

        var result = new CompiledPredicate(Predicate("{ select: \"response.body.json.groups[*]\", op: countEquals, value: 2 }")).Evaluate(observation);

        Assert.Equal(PredicateState.Holds, result.State);
    }

    [Fact]
    public void The_detail_shows_what_was_found()
    {
        var result = new CompiledPredicate(Predicate("{ select: response.status, op: gte, value: 500 }")).Evaluate(Order);

        Assert.Equal("response.status = 201", result.Detail);
    }

    [Fact]
    public void The_detail_of_an_empty_selection_says_so_and_long_selections_are_shortened()
    {
        var empty = new CompiledPredicate(Predicate("{ select: response.body.json.none, op: gt, value: 0 }")).Evaluate(Order);
        var observation = Observe(Request(), JsonResponse(200, """{ "a": [1, 2, 3, 4, 5] }"""));
        var many = new CompiledPredicate(Predicate("{ select: \"response.body.json.a[*]\", op: gt, value: 9 }")).Evaluate(observation);

        Assert.Equal("response.body.json.none = no value", empty.Detail);
        Assert.Equal("response.body.json.a[*] = 1, 2, 3, ...", many.Detail);
    }

    [Fact]
    public void Counting_reports_the_count()
    {
        var result = new CompiledPredicate(Predicate("{ select: \"response.body.json.items[*]\", op: countEquals, value: 5 }")).Evaluate(Order);

        Assert.Equal("response.body.json.items[*] has 2 value(s)", result.Detail);
    }

    [Fact]
    public void An_absent_value_reports_what_was_present()
    {
        var result = new CompiledPredicate(Predicate("{ select: response.body.json.total, op: absent }")).Evaluate(Order);

        Assert.Equal("response.body.json.total is present: 59.97", result.Detail);
    }

    [Fact]
    public void A_value_that_failed_to_extract_is_an_error_not_an_absence()
    {
        var contract = ContractOf(File.ReadAllText(Sample("contract.yaml")).Replace("    as: decimal\n  - name: cartLineTotals", "    as: integer\n  - name: cartLineTotals", StringComparison.Ordinal));
        var observation = Observe(Request("GET", "/cart"), HtmlResponse(200, "<p><span class=\"order-total\">59.97</span></p>"), contract: contract);

        var result = new CompiledPredicate(Predicate("{ select: extract.cartTotal, op: exists }")).Evaluate(observation);

        Assert.Equal(PredicateState.Error, result.State);
        Assert.Contains("\"59.97\", which cannot be read as integer in culture 'invariant'", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ref_to_a_value_that_failed_to_extract_is_an_error()
    {
        var contract = ContractOf(File.ReadAllText(Sample("contract.yaml")).Replace("    as: decimal\n  - name: cartLineTotals", "    as: boolean\n  - name: cartLineTotals", StringComparison.Ordinal));
        var observation = Observe(Request("GET", "/cart"), HtmlResponse(200, "<p><span class=\"order-total\">59.97</span></p>"), contract: contract);

        var result = new CompiledPredicate(Predicate("{ select: response.status, op: equals, ref: extract.cartTotal }")).Evaluate(observation);

        Assert.Equal(PredicateState.Error, result.State);
    }

    [Fact]
    public void A_null_argument_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new CompiledPredicate(null!));
        Assert.Throws<ArgumentNullException>(() => new CompiledPredicate(Predicate("{ select: response.status, op: exists }")).Evaluate(null!));
    }
}
