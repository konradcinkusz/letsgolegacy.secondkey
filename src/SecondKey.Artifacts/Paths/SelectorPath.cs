using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SecondKey.Artifacts.Paths;

/// <summary>A step of a <see cref="SelectorPath"/>.</summary>
public abstract record PathSegment;

/// <summary><c>.name</c> or <c>["name.with.dots"]</c>.</summary>
public sealed record PropertySegment(string Name) : PathSegment;

/// <summary><c>[3]</c>.</summary>
public sealed record IndexSegment(int Index) : PathSegment;

/// <summary><c>[*]</c> — every element of an array, or every value of an object.</summary>
public sealed record WildcardSegment : PathSegment;

/// <summary><c>**</c> — the current node and every node below it, at any depth.</summary>
public sealed record DescendantSegment : PathSegment;

/// <summary>Why a path could not be parsed, and where.</summary>
public sealed class PathSyntaxException : FormatException
{
    public PathSyntaxException(string message)
        : base(message)
    {
    }

    public PathSyntaxException()
    {
    }

    public PathSyntaxException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A selector over an observation document — the one path language shared by clauses,
/// extract references and normalization rules:
/// <c>response.body.json.items[*].price</c>, <c>response.headers.location[0]</c>,
/// <c>db["dbo.Orders"].inserted</c>, <c>response.body.json.**.cardNumber</c>.
/// </summary>
public sealed class SelectorPath : IEquatable<SelectorPath>
{
    /// <summary>The roots of an observation document.</summary>
    public static readonly IReadOnlySet<string> Roots = new HashSet<string>(StringComparer.Ordinal)
    {
        "request", "response", "extract", "db", "outbound",
    };

    private SelectorPath(string text, IReadOnlyList<PathSegment> segments)
    {
        Text = text;
        Segments = segments;
    }

    public string Text { get; }

    /// <summary>Every segment, the root first (as a property of the observation document).</summary>
    public IReadOnlyList<PathSegment> Segments { get; }

    /// <summary>The first segment's name: the observation root, or the first name of a relative path.</summary>
    public string Root => ((PropertySegment)Segments[0]).Name;

    /// <summary>True when the path can select more than one value.</summary>
    public bool IsMultiValued => Segments.Any(s => s is WildcardSegment or DescendantSegment);

    public static SelectorPath Parse(string text) => Parse(text, relative: false);

    /// <summary>
    /// Parses a path relative to some element rather than to an observation — e.g. the key of
    /// a set rule (<c>sku</c>, <c>id.value</c>). Its first segment may be any name.
    /// </summary>
    public static SelectorPath ParseRelative(string text) => Parse(text, relative: true);

    private static SelectorPath Parse(string text, bool relative)
    {
        ArgumentNullException.ThrowIfNull(text);
        var segments = new List<PathSegment>();
        var i = 0;

        var root = ReadName(text, ref i);
        if (relative ? root.Length == 0 : !Roots.Contains(root))
        {
            throw new PathSyntaxException(relative
                ? $"'{text}': a relative path starts with a name"
                : $"'{text}': a path starts with one of {string.Join(", ", Roots.Order(StringComparer.Ordinal))}");
        }

        segments.Add(new PropertySegment(root));
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '.')
            {
                i++;
                if (i + 1 < text.Length + 1 && string.CompareOrdinal(text, i, "**", 0, 2) == 0)
                {
                    i += 2;
                    if (i >= text.Length || (text[i] != '.' && text[i] != '['))
                    {
                        throw new PathSyntaxException($"'{text}': ** must be followed by a segment, e.g. **.name");
                    }

                    segments.Add(new DescendantSegment());
                    continue;
                }

                var name = ReadName(text, ref i);
                if (name.Length == 0)
                {
                    throw new PathSyntaxException($"'{text}': expected a name at position {i}");
                }

                segments.Add(new PropertySegment(name));
            }
            else if (c == '[')
            {
                segments.Add(ReadBracket(text, ref i));
            }
            else
            {
                throw new PathSyntaxException($"'{text}': unexpected '{c}' at position {i}");
            }
        }

        return new SelectorPath(text, segments);
    }

    public static bool TryParse(string text, out SelectorPath? path, out string? error)
    {
        try
        {
            path = Parse(text);
            error = null;
            return true;
        }
        catch (PathSyntaxException ex)
        {
            path = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Every value the path reaches in <paramref name="document"/>. A missing property yields
    /// nothing (not null); a property that is present with a JSON null yields a null entry.
    /// </summary>
    public IReadOnlyList<JsonNode?> Select(JsonNode? document)
    {
        IEnumerable<JsonNode?> current = [document];
        foreach (var segment in Segments)
        {
            current = Step(current, segment).ToList();
        }

        return (IReadOnlyList<JsonNode?>)current;
    }

    /// <summary>True when <paramref name="concrete"/> (a path without wildcards) is one this path selects.</summary>
    public bool Matches(IReadOnlyList<PathSegment> concrete) => Matches(0, concrete, 0);

    public override string ToString() => Text;

    public bool Equals(SelectorPath? other) => other is not null && string.Equals(Text, other.Text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as SelectorPath);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>Renders concrete segments back to path text, quoting names that need it.</summary>
    public static string Format(IEnumerable<PathSegment> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case PropertySegment p when builder.Length == 0:
                    builder.Append(p.Name);
                    break;
                case PropertySegment p when IsPlainName(p.Name):
                    builder.Append('.').Append(p.Name);
                    break;
                case PropertySegment p:
                    builder.Append("[\"").Append(p.Name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append("\"]");
                    break;
                case IndexSegment ix:
                    builder.Append('[').Append(ix.Index.ToString(CultureInfo.InvariantCulture)).Append(']');
                    break;
                case WildcardSegment:
                    builder.Append("[*]");
                    break;
                case DescendantSegment:
                    builder.Append(".**");
                    break;
            }
        }

        return builder.ToString();
    }

    private bool Matches(int pi, IReadOnlyList<PathSegment> concrete, int ci)
    {
        while (true)
        {
            if (pi == Segments.Count)
            {
                return ci == concrete.Count;
            }

            var segment = Segments[pi];
            if (segment is DescendantSegment)
            {
                for (var skip = ci; skip <= concrete.Count; skip++)
                {
                    if (Matches(pi + 1, concrete, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (ci == concrete.Count)
            {
                return false;
            }

            var actual = concrete[ci];
            var ok = segment switch
            {
                PropertySegment p => actual is PropertySegment ap && string.Equals(p.Name, ap.Name, StringComparison.Ordinal),
                IndexSegment ix => actual is IndexSegment ai && ai.Index == ix.Index,
                WildcardSegment => actual is IndexSegment or PropertySegment,
                _ => false,
            };
            if (!ok)
            {
                return false;
            }

            pi++;
            ci++;
        }
    }

    private static IEnumerable<JsonNode?> Step(IEnumerable<JsonNode?> nodes, PathSegment segment)
    {
        foreach (var node in nodes)
        {
            switch (segment)
            {
                case PropertySegment p when node is JsonObject obj && obj.TryGetPropertyValue(p.Name, out var value):
                    yield return value;
                    break;
                case IndexSegment ix when node is JsonArray array && ix.Index < array.Count:
                    yield return array[ix.Index];
                    break;
                case WildcardSegment when node is JsonArray array:
                    foreach (var item in array)
                    {
                        yield return item;
                    }

                    break;
                case WildcardSegment when node is JsonObject obj:
                    foreach (var (_, item) in obj)
                    {
                        yield return item;
                    }

                    break;
                case DescendantSegment:
                    foreach (var item in SelfAndDescendants(node))
                    {
                        yield return item;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<JsonNode?> SelfAndDescendants(JsonNode? node)
    {
        yield return node;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    foreach (var d in SelfAndDescendants(child))
                    {
                        yield return d;
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    foreach (var d in SelfAndDescendants(child))
                    {
                        yield return d;
                    }
                }

                break;
        }
    }

    private static string ReadName(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && IsNameChar(text[i]))
        {
            i++;
        }

        return text[start..i];
    }

    private static PathSegment ReadBracket(string text, ref int i)
    {
        i++; // '['
        if (i >= text.Length)
        {
            throw new PathSyntaxException($"'{text}': unterminated '['");
        }

        PathSegment segment;
        if (text[i] == '*')
        {
            i++;
            segment = new WildcardSegment();
        }
        else if (text[i] == '"')
        {
            i++;
            var name = new StringBuilder();
            while (i < text.Length && text[i] != '"')
            {
                if (text[i] == '\\' && i + 1 < text.Length)
                {
                    i++;
                }

                name.Append(text[i]);
                i++;
            }

            if (i >= text.Length)
            {
                throw new PathSyntaxException($"'{text}': unterminated quoted name");
            }

            i++; // closing quote
            segment = new PropertySegment(name.ToString());
        }
        else if (char.IsAsciiDigit(text[i]))
        {
            var start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (!int.TryParse(text.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                throw new PathSyntaxException($"'{text}': index out of range at position {start}");
            }

            segment = new IndexSegment(index);
        }
        else
        {
            throw new PathSyntaxException($"'{text}': expected an index, * or a quoted name after '[' at position {i}");
        }

        if (i >= text.Length || text[i] != ']')
        {
            throw new PathSyntaxException($"'{text}': expected ']' at position {i}");
        }

        i++;
        return segment;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '$';

    private static bool IsPlainName(string name) => name.Length > 0 && name.All(IsNameChar);
}
