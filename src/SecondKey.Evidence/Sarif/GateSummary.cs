namespace SecondKey.Evidence.Sarif;

/// <summary>One rule of one tool: what it found, by level, and how much of that blocks.</summary>
public sealed record RuleSummary(
    string Tool,
    string RuleId,
    string? Description,
    string? HelpUri,
    int Errors,
    int Warnings,
    int Notes,
    int Suppressed,
    int Blocking);

/// <summary>A finding that blocks, with the log it came from.</summary>
public sealed record BlockingFinding(string Log, string Tool, SarifFinding Finding);

/// <summary>
/// The gate's SARIF, summarized per rule (C5, S14). Every rule a tool declares is listed,
/// including the ones that found nothing — the summary records what was checked, not only
/// what was found. The gate passes when nothing blocks (see <see cref="SarifFinding.Blocks"/>).
/// </summary>
public sealed class GateSummary
{
    private GateSummary(IReadOnlyList<SarifLog> logs, IReadOnlyList<RuleSummary> rules, IReadOnlyList<BlockingFinding> blocking)
    {
        Logs = logs;
        Rules = rules;
        Blocking = blocking;
    }

    public IReadOnlyList<SarifLog> Logs { get; }

    public IReadOnlyList<RuleSummary> Rules { get; }

    public IReadOnlyList<BlockingFinding> Blocking { get; }

    public bool Passes => Blocking.Count == 0;

    /// <summary>Findings that are not suppressed, at any level.</summary>
    public int Findings => Rules.Sum(r => r.Errors + r.Warnings + r.Notes);

    public static GateSummary From(IEnumerable<SarifLog> logs)
    {
        ArgumentNullException.ThrowIfNull(logs);
        var list = logs.ToList();
        var rules = new List<RuleSummary>();
        var blocking = new List<BlockingFinding>();
        foreach (var log in list)
        {
            foreach (var run in log.Runs)
            {
                var declared = run.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
                var ids = run.Rules.Select(r => r.Id).Union(run.Findings.Select(f => f.RuleId), StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    var findings = run.Findings.Where(f => f.RuleId == id).ToList();
                    var active = findings.Where(f => !f.Suppressed).ToList();
                    declared.TryGetValue(id, out var rule);
                    rules.Add(new RuleSummary(
                        run.Tool,
                        id,
                        rule?.Description,
                        rule?.HelpUri,
                        Errors: active.Count(f => f.Level == "error"),
                        Warnings: active.Count(f => f.Level == "warning"),
                        Notes: active.Count(f => f.Level is "note" or "none"),
                        Suppressed: findings.Count - active.Count,
                        Blocking: findings.Count(f => f.Blocks)));
                }

                blocking.AddRange(run.Findings.Where(f => f.Blocks).Select(f => new BlockingFinding(log.Name, run.Tool, f)));
            }
        }

        var ordered = rules
            .OrderByDescending(r => r.Blocking)
            .ThenBy(r => r.Tool, StringComparer.Ordinal)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();
        return new GateSummary(list, ordered, blocking);
    }
}
