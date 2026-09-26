using System.Text.Json.Nodes;
using SecondKey.Artifacts.Paths;
using SecondKey.Artifacts.Verdicts;

namespace SecondKey.Compare.Tests;

public class CanonicalJsonTests
{
    [Fact]
    public void Keys_are_ordered_ordinally_at_every_depth()
    {
        var text = CanonicalJson.ToText(JsonNode.Parse("""{ "b": 1, "a": { "z": 1, "Y": 2 }, "C": [ { "q": 1, "p": 2 } ] }"""));

        Assert.Equal("""{"C":[{"p":2,"q":1}],"a":{"Y":2,"z":1},"b":1}""", text);
    }

    [Theory]
    [InlineData("1", "1.0")]
    [InlineData("12.5", "12.50")]
    [InlineData("0", "0.000")]
    [InlineData("100", "1E2")]
    public void Numbers_are_written_in_one_form(string a, string b)
    {
        Assert.Equal(CanonicalJson.ToText(JsonNode.Parse(a)), CanonicalJson.ToText(JsonNode.Parse(b)));
    }

    [Fact]
    public void The_shortest_exact_decimal_is_kept()
    {
        Assert.Equal("12.5", CanonicalJson.ToText(JsonNode.Parse("12.50")));
        Assert.Equal("9.300000000000001", CanonicalJson.ToText(JsonNode.Parse("9.300000000000001")));
    }

    [Fact]
    public void Strings_booleans_and_nulls_are_kept_as_they_are()
    {
        Assert.Equal("""["1.0",true,null]""", CanonicalJson.ToText(JsonNode.Parse("""[ "1.0", true, null ]""")));
        Assert.Equal("null", CanonicalJson.ToText(null));
        Assert.Null(CanonicalJson.Canonicalize(null));
    }

    [Fact]
    public void The_input_is_not_changed()
    {
        var node = JsonNode.Parse("""{ "b": 1.50, "a": 2 }""")!;

        _ = CanonicalJson.Canonicalize(node);

        Assert.Equal("""{"b":1.50,"a":2}""", node.ToJsonString());
    }
}

public class JsonDiffTests
{
    private static IReadOnlyList<Change> Diff(string legacy, string candidate, Func<IReadOnlyList<PathSegment>, decimal, decimal, bool>? equalNumbers = null) =>
        JsonDiff.Compare(JsonNode.Parse(legacy), JsonNode.Parse(candidate), equalNumbers);

    [Fact]
    public void Identical_documents_have_no_differences()
    {
        Assert.Empty(Diff("""{ "a": [1, { "b": "x" }], "c": null }""", """{ "c": null, "a": [1.0, { "b": "x" }] }"""));
    }

    [Fact]
    public void A_changed_value_is_located_by_a_concrete_path()
    {
        var change = Assert.Single(Diff("""{ "response": { "items": [ { "name": "a" } ] } }""", """{ "response": { "items": [ { "name": "b" } ] } }"""));

        Assert.Equal("response.items[0].name", change.Difference.Path);
        Assert.Equal(DifferenceKind.Changed, change.Difference.Kind);
        Assert.Equal("a", change.Difference.Legacy!.GetValue<string>());
        Assert.Equal("b", change.Difference.Candidate!.GetValue<string>());
        Assert.Equal([new PropertySegment("response"), new PropertySegment("items"), new IndexSegment(0), new PropertySegment("name")], change.Path);
    }

    [Fact]
    public void Keys_on_one_side_only_are_added_or_removed()
    {
        var changes = Diff("""{ "gone": 1, "same": 2 }""", """{ "same": 2, "new": { "x": 3 } }""");

        Assert.Collection(
            changes,
            c =>
            {
                Assert.Equal("gone", c.Difference.Path);
                Assert.Equal(DifferenceKind.Removed, c.Difference.Kind);
                Assert.Equal(1, c.Difference.Legacy!.GetValue<int>());
                Assert.Null(c.Difference.Candidate);
            },
            c =>
            {
                Assert.Equal("new", c.Difference.Path);
                Assert.Equal(DifferenceKind.Added, c.Difference.Kind);
                Assert.Null(c.Difference.Legacy);
                Assert.Equal(3, c.Difference.Candidate!["x"]!.GetValue<int>());
            });
    }

    [Fact]
    public void Arrays_are_compared_by_position_and_extra_elements_are_added_or_removed()
    {
        var longer = Diff("[1, 2]", "[1, 3, 4]");
        var shorter = Diff("[1, 2, 5]", "[1]");

        Assert.Equal(["[1] changed", "[2] added"], longer.Select(c => $"{c.Difference.Path} {Kind(c)}"));
        Assert.Equal(["[1] removed", "[2] removed"], shorter.Select(c => $"{c.Difference.Path} {Kind(c)}"));
        Assert.Equal(5, shorter[1].Difference.Legacy!.GetValue<int>());
    }

    [Fact]
    public void A_change_of_type_is_one_difference_at_that_node()
    {
        var change = Assert.Single(Diff("""{ "json": { "a": 1 } }""", """{ "json": "text" }"""));

        Assert.Equal("json", change.Difference.Path);
        Assert.Equal(DifferenceKind.Changed, change.Difference.Kind);
    }

    [Fact]
    public void Null_and_absent_are_different()
    {
        var change = Assert.Single(Diff("""{ "a": null }""", "{}"));

        Assert.Equal(DifferenceKind.Removed, change.Difference.Kind);
        Assert.Null(change.Difference.Legacy);
    }

    [Fact]
    public void Names_that_need_quoting_are_quoted()
    {
        var change = Assert.Single(Diff("""{ "db": { "dbo.Orders": 1 } }""", """{ "db": { "dbo.Orders": 2 } }"""));

        Assert.Equal("db[\"dbo.Orders\"]", change.Difference.Path);
    }

    [Fact]
    public void Numbers_are_equal_when_the_callback_says_so_and_only_then()
    {
        var seen = new List<string>();
        bool Tolerant(IReadOnlyList<PathSegment> path, decimal x, decimal y)
        {
            seen.Add(SelectorPath.Format(path));
            return Math.Abs(x - y) <= 0.01m;
        }

        Assert.Empty(Diff("""{ "t": 9.3 }""", """{ "t": 9.300000000000001 }""", Tolerant));
        Assert.Single(Diff("""{ "t": 9.3 }""", """{ "t": 9.4 }""", Tolerant));
        Assert.Single(Diff("""{ "t": 9.3 }""", """{ "t": "9.3" }""", Tolerant));
        Assert.Equal(["t", "t"], seen);
    }

    [Fact]
    public void Without_a_callback_numbers_must_be_exactly_equal()
    {
        Assert.Single(Diff("""{ "t": 9.3 }""", """{ "t": 9.300000000000001 }"""));
    }

    [Fact]
    public void Values_in_a_difference_are_copies()
    {
        var legacy = JsonNode.Parse("""{ "a": { "b": 1 } }""")!;
        var change = Assert.Single(JsonDiff.Compare(legacy, JsonNode.Parse("{}")));

        Assert.Null(change.Difference.Legacy!.Parent);
        Assert.NotSame(legacy["a"], change.Difference.Legacy);
    }

    [Fact]
    public void A_change_knows_whether_it_is_at_or_below_a_location()
    {
        var change = Assert.Single(Diff("""{ "a": { "b": [ 1 ] } }""", """{ "a": { "b": [ 2 ] } }"""));
        PathSegment a = new PropertySegment("a"), b = new PropertySegment("b");

        Assert.True(change.IsAtOrBelow([a]));
        Assert.True(change.IsAtOrBelow([a, b]));
        Assert.True(change.IsAtOrBelow([a, b, new IndexSegment(0)]));
        Assert.False(change.IsAtOrBelow([a, b, new IndexSegment(1)]));
        Assert.False(change.IsAtOrBelow([b]));
        Assert.False(change.IsAtOrBelow([a, b, new IndexSegment(0), new PropertySegment("c")]));
        Assert.True(change.IsAtOrBelow([]));
    }

    private static string Kind(Change change) => change.Difference.Kind.ToString().ToLowerInvariant();
}
