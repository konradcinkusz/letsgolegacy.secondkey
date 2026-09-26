using System.Text.RegularExpressions;
using SecondKey.Artifacts.Contracts;

namespace SecondKey.Contract;

/// <summary>
/// A compiled <see cref="RequestMatch"/>: which requests a clause or an extractor applies
/// to. No match block means every request. Regexes are culture-invariant and time-boxed.
/// </summary>
public sealed class RequestMatcher
{
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly HashSet<string>? _methods;
    private readonly Regex? _path;
    private readonly IReadOnlyList<(string Name, Regex Pattern)> _query;

    public RequestMatcher(RequestMatch? match)
    {
        Source = match;
        if (match?.Method is { Count: > 0 } methods)
        {
            _methods = new HashSet<string>(methods, StringComparer.Ordinal);
        }

        if (match?.Path is { } path)
        {
            _path = new Regex(path, RegexOptions.CultureInvariant, RegexTimeout);
        }

        _query = (match?.Query ?? new Dictionary<string, string>())
            .Select(q => (q.Key, new Regex(q.Value, RegexOptions.CultureInvariant, RegexTimeout)))
            .ToList();
    }

    public static RequestMatcher Everything { get; } = new(null);

    public RequestMatch? Source { get; }

    public bool Matches(string method, string path, IReadOnlyDictionary<string, IReadOnlyList<string>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (_methods is not null && !_methods.Contains(method))
        {
            return false;
        }

        if (_path is not null && !_path.IsMatch(path))
        {
            return false;
        }

        foreach (var (name, pattern) in _query)
        {
            if (!query.TryGetValue(name, out var values) || !values.Any(pattern.IsMatch))
            {
                return false;
            }
        }

        return true;
    }
}
