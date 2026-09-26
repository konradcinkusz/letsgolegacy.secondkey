using System.Text.Json.Nodes;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Paths;
using static SecondKey.Compare.Tests.Fixtures;

namespace SecondKey.Compare.Tests;

/// <summary>S12: the differences that do not matter, and the rule that says so for each one.</summary>
public class NormalizerTests
{
    private static JsonObject Doc(string json) => (JsonObject)CanonicalJson.Canonicalize(JsonNode.Parse(json))!;

    /// <summary>Raw differences, each with the rule that explains it (null when none does), and whether normalization leaves any.</summary>
    private static (List<(string Path, string? Rule)> Explained, IReadOnlyList<Change> Remaining) Run(Normalizer normalizer, string legacy, string candidate)
    {
        var l = Doc(legacy);
        var c = Doc(candidate);
        var raw = JsonDiff.Compare(l, c);
        var remaining = JsonDiff.Compare(normalizer.Apply(l), normalizer.Apply(c), normalizer.NumbersEqual);
        return (raw.Select(r => (r.Difference.Path, normalizer.Explain(r, remaining))).ToList(), remaining);
    }

    [Fact]
    public void Without_rules_nothing_is_normalized_or_explained()
    {
        var (explained, remaining) = Run(Normalizer.None, """{ "a": 1 }""", """{ "a": 2 }""");

        Assert.Equal([("a", (string?)null)], explained);
        Assert.Single(remaining);
        Assert.Equal("""{"a":1}""", Normalizer.None.Apply(Doc("""{ "a": 1 }""")).ToJsonString());
        Assert.Equal("x 2026-09-26T10:00:00Z", Normalizer.None.MaskText("x 2026-09-26T10:00:00Z"));
    }

    [Fact]
    public void Apply_works_on_a_copy()
    {
        var normalizer = NormalizerOf("normalize: { ignore: [response.a], masks: { guids: true } }");
        var document = Doc("""{ "response": { "a": 1, "b": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40" } }""");

        var normalized = normalizer.Apply(document);

        Assert.Equal("""{"response":{"b":"<guid>"}}""", Text(normalized));
        Assert.Equal("""{"response":{"a":1,"b":"6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40"}}""", document.ToJsonString());
    }

    // ---- ignore -------------------------------------------------------------------------

    [Fact]
    public void Ignored_values_are_removed_wherever_the_path_reaches()
    {
        var normalizer = NormalizerOf("""
            normalize:
              ignore:
                - response.body.json.generatedAt
                - response.body.json.items[*].etag
                - response.body.json.**.debug
                - response.headers.x-trace[0]
            """);

        var normalized = normalizer.Apply(Doc("""
            { "response": {
                "headers": { "x-trace": ["a", "b"] },
                "body": { "json": {
                  "generatedAt": "now", "total": 1,
                  "items": [ { "sku": "A", "etag": "1" }, { "sku": "B", "etag": "2" } ],
                  "deep": { "deeper": { "debug": true, "keep": 1 } } } } } }
            """));

        Assert.Equal(
            """{"response":{"body":{"json":{"deep":{"deeper":{"keep":1}},"items":[{"sku":"A"},{"sku":"B"}],"total":1}},"headers":{"x-trace":["b"]}}}""",
            normalized.ToJsonString());
    }

    [Fact]
    public void Ignoring_array_elements_removes_each_one_it_names_and_keeps_the_rest_in_order()
    {
        var normalizer = NormalizerOf("normalize: { ignore: [\"response.list[0]\", \"response.list[2]\"] }");

        Assert.Equal("""{"response":{"list":["b","d"]}}""", normalizer.Apply(Doc("""{ "response": { "list": ["a", "b", "c", "d"] } }""")).ToJsonString());
    }

    [Fact]
    public void A_difference_at_or_below_an_ignored_path_is_explained_by_that_rule()
    {
        var normalizer = NormalizerOf("normalize: { ignore: [response.meta, response.body.json.generatedAt] }");

        var (explained, remaining) = Run(
            normalizer,
            """{ "response": { "meta": { "v": 1 }, "body": { "json": { "generatedAt": "a" } } } }""",
            """{ "response": { "meta": { "v": 2 }, "body": { "json": { } } } }""");

        Assert.Empty(remaining);
        Assert.Equal(
            [("response.body.json.generatedAt", "ignore: response.body.json.generatedAt"), ("response.meta.v", "ignore: response.meta")],
            explained);
    }

    [Fact]
    public void Ignoring_a_child_does_not_explain_its_parent_appearing()
    {
        var normalizer = NormalizerOf("normalize: { ignore: [response.meta.version] }");

        var (explained, remaining) = Run(normalizer, """{ "response": { } }""", """{ "response": { "meta": { "version": 2 } } }""");

        Assert.Equal([("response.meta", (string?)null)], explained);
        Assert.Equal("response.meta", Assert.Single(remaining).Difference.Path);
    }

    // ---- masks --------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-09-26T09:00:04.505Z")]
    [InlineData("2026-09-26T09:00:04Z")]
    [InlineData("2026-09-26T09:00Z")]
    [InlineData("2026-09-26T09:00:04.5050000+02:00")]
    [InlineData("2026-09-26T09:00:04-0500")]
    [InlineData("2026-09-26 09:00:04")]
    [InlineData("2026-09-26t09:00:04,5z")]
    public void Iso_date_times_become_one_token(string timestamp)
    {
        var normalizer = NormalizerOf("normalize: { masks: { timestamps: true } }");

        Assert.Equal("created <timestamp> by x", normalizer.MaskText($"created {timestamp} by x"));
    }

    [Theory]
    [InlineData("2026-09-26")]
    [InlineData("/Date(1411725600000)/")]
    [InlineData("26.09.2026 09:00")]
    [InlineData("2026-09-26T0900")]
    public void Other_date_forms_are_not_timestamps_so_a_format_change_is_still_seen(string text)
    {
        var normalizer = NormalizerOf("normalize: { masks: { timestamps: true } }");

        Assert.Equal(text, normalizer.MaskText(text));
    }

    [Theory]
    [InlineData("6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "<guid>")]
    [InlineData("{6F1C2B9E-4A53-4C8E-9D3A-2B7F5E8A1C40}", "{<guid>}")]
    [InlineData("/orders/6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40/lines", "/orders/<guid>/lines")]
    [InlineData("a6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "a6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40")]
    [InlineData("6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40f", "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40f")]
    [InlineData("6f1c2b9e4a534c8e9d3a2b7f5e8a1c40", "6f1c2b9e4a534c8e9d3a2b7f5e8a1c40")]
    public void Guids_become_one_token_only_when_they_stand_alone(string text, string masked)
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");

        Assert.Equal(masked, normalizer.MaskText(text));
    }

    [Fact]
    public void Patterns_replace_what_they_match_with_their_replacement_in_order()
    {
        var normalizer = NormalizerOf("""
            normalize:
              masks:
                patterns:
                  - { name: csrf, regex: 'value="[^"]{20,}"', replacement: 'value="<token>"' }
                  - { name: build, regex: 'build (\d+)', replacement: 'build <$1-digits>' }
            """);

        Assert.Equal(
            """<input value="<token>"> build <1234-digits>""",
            normalizer.MaskText("""<input value="abcdefghijklmnopqrstuvwxyz"> build 1234"""));
        Assert.Equal("""<input value="short">""", normalizer.MaskText("""<input value="short">"""));
    }

    [Fact]
    public void Masks_apply_to_every_string_but_not_to_keys_or_numbers()
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");
        const string Guid = "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40";

        var normalized = normalizer.Apply(Doc($$"""{ "{{Guid}}": ["{{Guid}}", 1, true, null], "text": "id={{Guid}}" }"""));

        Assert.Equal($$"""{"{{Guid}}":["<guid>",1,true,null],"text":"id=<guid>"}""", Text(normalized));
    }

    [Fact]
    public void A_string_difference_the_masks_account_for_names_every_mask_that_fired()
    {
        var normalizer = NormalizerOf("""
            normalize:
              masks:
                timestamps: true
                guids: true
                patterns: [ { name: host, regex: 'node-[0-9]+', replacement: 'node-N' } ]
            """);

        var (explained, remaining) = Run(
            normalizer,
            """{ "a": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "b": "at 2026-09-26T09:00:00Z on node-1 for 6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "c": "node-1" }""",
            """{ "a": "0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b", "b": "at 2026-09-26T10:00:00Z on node-2 for 0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b", "c": "node-2" }""");

        Assert.Empty(remaining);
        Assert.Equal(
            [("a", "masks.guids"), ("b", "masks.timestamps, masks.guids, masks.patterns.host"), ("c", "masks.patterns.host")],
            explained);
    }

    [Fact]
    public void A_mask_that_fires_on_one_side_only_still_explains_the_difference()
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");

        var (explained, remaining) = Run(normalizer, """{ "m": "id <guid>" }""", """{ "m": "id 6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40" }""");

        Assert.Empty(remaining);
        Assert.Equal([("m", "masks.guids")], explained);
    }

    [Fact]
    public void A_mask_that_fires_but_leaves_a_difference_explains_nothing()
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");

        var (explained, remaining) = Run(
            normalizer,
            """{ "m": "order 6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40 created" }""",
            """{ "m": "order 0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b saved" }""");

        Assert.Equal([("m", (string?)null)], explained);
        Assert.Equal("order <guid> saved", Assert.Single(remaining).Difference.Candidate!.GetValue<string>());
    }

    [Fact]
    public void A_string_difference_no_mask_touches_is_not_explained_by_masks()
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");

        Assert.Equal([("m", (string?)null)], Run(normalizer, """{ "m": "a" }""", """{ "m": "b" }""").Explained);
    }

    [Fact]
    public void Masks_do_not_explain_a_value_that_changed_type()
    {
        var normalizer = NormalizerOf("normalize: { masks: { guids: true } }");

        Assert.Equal([("m", (string?)null)], Run(normalizer, """{ "m": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40" }""", """{ "m": 1 }""").Explained);
        Assert.Equal([("m", (string?)null)], Run(normalizer, """{ "m": 1 }""", """{ "m": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40" }""").Explained);
    }

    // ---- sets ---------------------------------------------------------------------------

    [Fact]
    public void A_set_with_a_key_is_ordered_by_that_key()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items, key: sku } ] }");

        var normalized = normalizer.Apply(Doc("""{ "response": { "items": [ { "sku": "C", "n": 1 }, { "sku": "A", "n": 2 }, { "sku": "B", "n": 3 } ] } }"""));

        Assert.Equal("""{"response":{"items":[{"n":2,"sku":"A"},{"n":3,"sku":"B"},{"n":1,"sku":"C"}]}}""", normalized.ToJsonString());
    }

    [Fact]
    public void Elements_with_the_same_key_are_ordered_by_their_whole_value()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items, key: sku } ] }");

        var normalized = normalizer.Apply(Doc("""{ "response": { "items": [ { "sku": "A", "n": 2 }, { "n": 0 }, { "sku": "A", "n": 1 } ] } }"""));

        Assert.Equal("""{"response":{"items":[{"n":0},{"n":1,"sku":"A"},{"n":2,"sku":"A"}]}}""", normalized.ToJsonString());
    }

    [Fact]
    public void A_set_without_a_key_is_ordered_by_whole_values()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: \"response.tags\" } ] }");

        Assert.Equal("""{"response":{"tags":["a","b","c"]}}""", normalizer.Apply(Doc("""{ "response": { "tags": ["c", "a", "b"] } }""")).ToJsonString());
    }

    [Fact]
    public void A_key_path_can_reach_into_the_element()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.rows, key: id.value } ] }");

        var normalized = normalizer.Apply(Doc("""{ "response": { "rows": [ { "id": { "value": 2 } }, { "id": { "value": 1 } } ] } }"""));

        Assert.Equal("""{"response":{"rows":[{"id":{"value":1}},{"id":{"value":2}}]}}""", normalized.ToJsonString());
    }

    [Fact]
    public void Nested_sets_are_ordered_from_the_inside_out()
    {
        // The outer set is ordered by whole values, so the inner arrays must already be in order.
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.groups }, { path: \"response.groups[*].tags\" } ] }");

        var legacy = normalizer.Apply(Doc("""{ "response": { "groups": [ { "tags": ["c", "a"] }, { "tags": ["b"] } ] } }"""));
        var candidate = normalizer.Apply(Doc("""{ "response": { "groups": [ { "tags": ["b"] }, { "tags": ["a", "c"] } ] } }"""));

        Assert.Equal(legacy.ToJsonString(), candidate.ToJsonString());
        Assert.Equal("""{"response":{"groups":[{"tags":["a","c"]},{"tags":["b"]}]}}""", legacy.ToJsonString());
    }

    [Fact]
    public void A_set_path_that_reaches_no_array_changes_nothing()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items } ] }");

        Assert.Equal("""{"response":{"items":{"b":1,"a":2}}}""", normalizer.Apply((JsonObject)JsonNode.Parse("""{ "response": { "items": { "b": 1, "a": 2 } } }""")!).ToJsonString());
    }

    [Fact]
    public void An_order_change_is_explained_by_the_set_rule_when_the_set_is_otherwise_equal()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items, key: sku } ] }");

        var (explained, remaining) = Run(
            normalizer,
            """{ "response": { "items": [ { "sku": "A" }, { "sku": "B" } ], "total": 1 } }""",
            """{ "response": { "items": [ { "sku": "B" }, { "sku": "A" } ], "total": 1 } }""");

        Assert.Empty(remaining);
        Assert.Equal(
            [("response.items[0].sku", "sets: response.items"), ("response.items[1].sku", "sets: response.items")],
            explained);
    }

    [Fact]
    public void A_set_that_still_differs_after_ordering_explains_none_of_its_differences()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items, key: sku } ] }");

        var (explained, remaining) = Run(
            normalizer,
            """{ "response": { "items": [ { "sku": "A", "q": 1 }, { "sku": "B", "q": 1 } ] } }""",
            """{ "response": { "items": [ { "sku": "B", "q": 1 }, { "sku": "A", "q": 2 } ] } }""");

        Assert.Equal("response.items[0].q", Assert.Single(remaining).Difference.Path);
        Assert.All(explained, e => Assert.Null(e.Rule));
    }

    [Fact]
    public void A_set_does_not_explain_a_difference_outside_it()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items } ] }");

        var (explained, _) = Run(normalizer, """{ "response": { "items": [1, 2], "total": 3 } }""", """{ "response": { "items": [2, 1], "total": 4 } }""");

        Assert.Equal(
            [("response.items[0]", "sets: response.items"), ("response.items[1]", "sets: response.items"), ("response.total", (string?)null)],
            explained);
    }

    [Fact]
    public void A_set_of_different_length_is_a_difference()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.items } ] }");

        var (explained, remaining) = Run(normalizer, """{ "response": { "items": [1, 2] } }""", """{ "response": { "items": [2, 1, 3] } }""");

        Assert.Equal("response.items[2]", Assert.Single(remaining).Difference.Path);
        Assert.All(explained, e => Assert.Null(e.Rule));
    }

    [Fact]
    public void The_innermost_clean_set_explains_a_difference_inside_nested_sets()
    {
        var normalizer = NormalizerOf("normalize: { sets: [ { path: response.groups, key: id }, { path: \"response.groups[*].tags\" } ] }");

        var (explained, remaining) = Run(
            normalizer,
            """{ "response": { "groups": [ { "id": 1, "tags": ["a", "b"] } ] } }""",
            """{ "response": { "groups": [ { "id": 1, "tags": ["b", "a"] } ] } }""");

        Assert.Empty(remaining);
        Assert.All(explained, e => Assert.Equal("sets: response.groups[*].tags", e.Rule));
    }

    // ---- tolerances ---------------------------------------------------------------------

    [Theory]
    [InlineData("9.3", "9.300000000000001", true)]
    [InlineData("0.30", "0.31", true)]
    [InlineData("0.30", "0.29", true)]
    [InlineData("0.30", "0.311", false)]
    [InlineData("5", "5", true)]
    public void An_absolute_tolerance_bounds_the_difference(string legacy, string candidate, bool equal)
    {
        var normalizer = NormalizerOf("normalize: { tolerances: [ { path: response.total, absolute: 0.01 } ] }");

        var (_, remaining) = Run(normalizer, $$"""{ "response": { "total": {{legacy}} } }""", $$"""{ "response": { "total": {{candidate}} } }""");

        Assert.Equal(equal, remaining.Count == 0);
    }

    [Theory]
    [InlineData("100", "101", true)]
    [InlineData("101", "100", true)]
    [InlineData("-100", "-101", true)]
    [InlineData("100", "101.03", false)]
    [InlineData("100", "101.005", true)]
    [InlineData("0", "0.0001", false)]
    public void A_relative_tolerance_is_a_share_of_the_larger_magnitude(string legacy, string candidate, bool equal)
    {
        var normalizer = NormalizerOf("normalize: { tolerances: [ { path: \"response.lines[*].amount\", relative: 0.01 } ] }");

        var (_, remaining) = Run(normalizer, $$"""{ "response": { "lines": [ { "amount": {{legacy}} } ] } }""", $$"""{ "response": { "lines": [ { "amount": {{candidate}} } ] } }""");

        Assert.Equal(equal, remaining.Count == 0);
    }

    [Fact]
    public void A_tolerance_includes_its_bound()
    {
        var absolute = NormalizerOf("normalize: { tolerances: [ { path: response.total, absolute: 0.5 } ] }");
        var relative = NormalizerOf("normalize: { tolerances: [ { path: response.total, relative: 0.5 } ] }");
        var path = new List<PathSegment> { new PropertySegment("response"), new PropertySegment("total") };

        Assert.True(absolute.NumbersEqual(path, 1, 1.5m));
        Assert.False(absolute.NumbersEqual(path, 1, 1.51m));
        Assert.True(relative.NumbersEqual(path, 1, 2));
        Assert.False(relative.NumbersEqual(path, 1, 2.01m));
    }

    [Fact]
    public void A_tolerance_applies_only_where_its_path_reaches()
    {
        var normalizer = NormalizerOf("normalize: { tolerances: [ { path: response.total, absolute: 1 } ] }");

        var (explained, remaining) = Run(normalizer, """{ "response": { "total": 1, "tax": 1 } }""", """{ "response": { "total": 1.5, "tax": 1.5 } }""");

        Assert.Equal("response.tax", Assert.Single(remaining).Difference.Path);
        Assert.Equal([("response.tax", (string?)null), ("response.total", "tolerances: response.total")], explained);
    }

    [Fact]
    public void A_tolerance_does_not_make_a_number_equal_to_a_string()
    {
        var normalizer = NormalizerOf("normalize: { tolerances: [ { path: response.total, absolute: 1 } ] }");

        var (explained, remaining) = Run(normalizer, """{ "response": { "total": 1 } }""", """{ "response": { "total": "1" } }""");

        Assert.Single(remaining);
        Assert.Equal([("response.total", (string?)null)], explained);
    }

    [Fact]
    public void Numbers_too_far_apart_to_subtract_are_not_within_any_tolerance()
    {
        var normalizer = NormalizerOf("normalize: { tolerances: [ { path: response.total, relative: 1 } ] }");
        var path = new List<PathSegment> { new PropertySegment("response"), new PropertySegment("total") };

        Assert.False(normalizer.NumbersEqual(path, decimal.MaxValue, decimal.MinValue));
        Assert.True(normalizer.NumbersEqual(path, decimal.MaxValue, decimal.MaxValue));
        Assert.True(normalizer.NumbersEqual(path, 1, 2));
    }

    [Fact]
    public void A_tolerance_with_no_bound_allows_nothing()
    {
        var normalizer = new Normalizer(new NormalizeSettings { Tolerances = [new ToleranceRule { Path = "response.total" }] });
        var path = new List<PathSegment> { new PropertySegment("response"), new PropertySegment("total") };

        Assert.False(normalizer.NumbersEqual(path, 1, 1.0001m));
        Assert.True(normalizer.NumbersEqual(path, 1, 1));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        var change = new Change([], new Artifacts.Verdicts.Difference { Path = "", Kind = Artifacts.Verdicts.DifferenceKind.Changed });

        Assert.Throws<ArgumentNullException>(() => Normalizer.None.Apply(null!));
        Assert.Throws<ArgumentNullException>(() => Normalizer.None.Explain(null!, []));
        Assert.Throws<ArgumentNullException>(() => Normalizer.None.Explain(change, null!));
        Assert.Throws<ArgumentNullException>(() => change.IsAtOrBelow(null!));
    }

    // ---- all together -------------------------------------------------------------------

    [Fact]
    public void The_sample_contracts_rules_explain_every_difference_of_an_order_created_twice()
    {
        var normalizer = new Normalizer(SampleContract().Document.Normalize);

        var (explained, remaining) = Run(
            normalizer,
            """{ "response": { "status": 201, "body": { "json": { "orderId": "6f1c2b9e-4a53-4c8e-9d3a-2b7f5e8a1c40", "createdAt": "2026-09-26T09:00:04.505Z", "generatedAt": "x", "total": 27.9, "items": [ { "lineTotal": 9.3 } ] } } } }""",
            """{ "response": { "status": 201, "body": { "json": { "orderId": "0b8e4f7a-1d2c-4e5f-8a9b-3c4d5e6f7a8b", "createdAt": "2026-09-26T10:00:09.012Z", "total": 27.900000000000002, "items": [ { "lineTotal": 9.300000000000001 } ] } } } }""");

        Assert.Empty(remaining);
        Assert.Equal(
            [
                ("response.body.json.createdAt", "masks.timestamps"),
                ("response.body.json.generatedAt", "ignore: response.body.json.generatedAt"),
                ("response.body.json.items[0].lineTotal", "tolerances: response.body.json.items[*].lineTotal"),
                ("response.body.json.orderId", "masks.guids"),
                ("response.body.json.total", "tolerances: response.body.json.total"),
            ],
            explained);
    }
}
