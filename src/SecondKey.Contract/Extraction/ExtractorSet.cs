using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using SecondKey.Artifacts.Capture;
using SecondKey.Artifacts.Contracts;
using SecondKey.Artifacts.Paths;
using SecondKey.Contract.Observations;

namespace SecondKey.Contract.Extraction;

/// <summary>
/// The contract's extractors, compiled: named values pulled out of a response so that no
/// clause ever reads prose (ADR 0002). An extractor that finds nothing leaves its value
/// absent; one that finds something it cannot convert records an extraction error.
/// </summary>
public sealed class ExtractorSet
{
    private static readonly HtmlParser Html = new();
    private readonly IReadOnlyList<CompiledExtractor> _extractors;

    public ExtractorSet(IEnumerable<ExtractorDefinition>? definitions)
    {
        _extractors = (definitions ?? []).Select(d => new CompiledExtractor(d)).ToList();
    }

    public static ExtractorSet None { get; } = new(null);

    public (JsonObject Values, IReadOnlyDictionary<string, string> Errors) Extract(HttpRequestRecord request, HttpResponseRecord? response)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = new JsonObject();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response is null || _extractors.Count == 0)
        {
            return (values, errors);
        }

        var query = QueryString.Parse(request.Query);
        AngleSharp.Html.Dom.IHtmlDocument? html = null;
        foreach (var extractor in _extractors)
        {
            if (!extractor.When.Matches(request.Method, request.Path, query))
            {
                continue;
            }

            var raw = extractor.Definition.From switch
            {
                ExtractSource.Html => FromHtml(extractor, response, ref html),
                ExtractSource.Json => FromJson(extractor, response),
                ExtractSource.Header => FromHeader(extractor, response),
                ExtractSource.Text => FromText(extractor, response),
                _ => throw new InvalidOperationException($"unknown extractor source {extractor.Definition.From}"),
            };

            var converted = new JsonArray();
            string? error = null;
            foreach (var text in raw.Select(extractor.ApplyRegex).OfType<string>())
            {
                if (extractor.TryConvert(text, out var value, out var message))
                {
                    converted.Add(value);
                }
                else
                {
                    error ??= message;
                }
            }

            if (error is not null)
            {
                errors[extractor.Definition.Name] = error;
            }
            else if (extractor.Definition.All == true)
            {
                values[extractor.Definition.Name] = converted;
            }
            else if (converted.Count > 0)
            {
                values[extractor.Definition.Name] = converted[0]!.DeepClone();
            }
        }

        return (values, errors);
    }

    private static IEnumerable<string> FromHtml(CompiledExtractor extractor, HttpResponseRecord response, ref AngleSharp.Html.Dom.IHtmlDocument? html)
    {
        if (response.Body?.Text is not { } text)
        {
            return [];
        }

        html ??= Html.ParseDocument(text);
        var elements = html.QuerySelectorAll(extractor.Definition.Selector);
        return extractor.Definition.Attribute is { } attribute
            ? elements.Select(e => e.GetAttribute(attribute)).OfType<string>().ToList()
            : elements.Select(e => CollapseWhitespace(e.TextContent)).ToList();
    }

    private static IEnumerable<string> FromJson(CompiledExtractor extractor, HttpResponseRecord response)
    {
        if (response.Body?.Json is not { } json)
        {
            return [];
        }

        return extractor.JsonPath!.Select(json)
            .Where(v => v is not null)
            .Select(v => v is JsonValue value && value.TryGetValue<string>(out var s) ? s : v!.ToJsonString())
            .ToList();
    }

    private static IEnumerable<string> FromHeader(CompiledExtractor extractor, HttpResponseRecord response) =>
        response.Headers
            .Where(h => string.Equals(h.Key, extractor.Definition.Selector, StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();

    private static IEnumerable<string> FromText(CompiledExtractor extractor, HttpResponseRecord response)
    {
        var text = response.Body?.Text;
        if (text is null)
        {
            return [];
        }

        return extractor.TextPattern!.Matches(text).Select(m => m.Groups[1].Value).ToList();
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class CompiledExtractor
    {
        public CompiledExtractor(ExtractorDefinition definition)
        {
            Definition = definition;
            When = new RequestMatcher(definition.When);
            Culture = definition.Culture is { } name ? CultureInfo.GetCultureInfo(name, predefinedOnly: true) : CultureInfo.InvariantCulture;
            if (definition.Regex is { } regex)
            {
                PostFilter = new Regex(regex, RegexOptions.CultureInvariant, RequestMatcher.RegexTimeout);
            }

            if (definition.From == ExtractSource.Text)
            {
                TextPattern = new Regex(definition.Selector, RegexOptions.CultureInvariant, RequestMatcher.RegexTimeout);
            }

            if (definition.From == ExtractSource.Json)
            {
                JsonPath = SelectorPath.ParseRelative(definition.Selector);
            }
        }

        public ExtractorDefinition Definition { get; }

        public RequestMatcher When { get; }

        public CultureInfo Culture { get; }

        public Regex? PostFilter { get; }

        public Regex? TextPattern { get; }

        public SelectorPath? JsonPath { get; }

        /// <summary>Applies the post-filter regex; null when it does not match (the value is absent).</summary>
        public string? ApplyRegex(string text)
        {
            if (PostFilter is null)
            {
                return text;
            }

            var match = PostFilter.Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }

        public bool TryConvert(string text, out JsonNode? value, out string error)
        {
            var trimmed = text.Trim();
            error = string.Empty;
            switch (Definition.As ?? ExtractType.String)
            {
                case ExtractType.Decimal when decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, Culture, out var number):
                    value = JsonValue.Create(number);
                    return true;
                case ExtractType.Integer when long.TryParse(trimmed, NumberStyles.Integer | NumberStyles.AllowThousands, Culture, out var integer):
                    value = JsonValue.Create(integer);
                    return true;
                case ExtractType.Boolean when bool.TryParse(trimmed, out var boolean):
                    value = JsonValue.Create(boolean);
                    return true;
                case ExtractType.String:
                    value = JsonValue.Create(trimmed);
                    return true;
                default:
                    value = null;
                    error = $"extractor '{Definition.Name}' found \"{trimmed}\", which cannot be read as {(Definition.As ?? ExtractType.String).ToString().ToLowerInvariant()} in culture '{(Culture.Name.Length == 0 ? "invariant" : Culture.Name)}'";
                    return false;
            }
        }
    }
}
