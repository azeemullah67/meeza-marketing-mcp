using System.Text.Json;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>The task that tracks a problem.</summary>
public sealed record CheckTask(long Id, string Status);

/// <summary>A problem from the latest check and its task (null when the task was deleted).</summary>
public sealed record CheckProblem(JsonElement Finding, CheckTask? Task);

/// <summary>
/// The latest automatic check of a project. <see cref="AnalyzedAt"/> is null when no check has run yet.
/// <see cref="Sources"/> is the number of connected data sources: with none, "no problems" only means "nothing to check".
/// </summary>
public sealed record CheckReport(
    string? AnalyzedAt, string? Trigger, string? Range, int WindowDays, int Sources,
    IReadOnlyList<CheckProblem> Problems, IReadOnlyList<JsonElement> Opportunities, TaskCounts Tasks);

/// <summary>
/// The check that runs after every sync: the analyst's rules over the last 30 complete days. Its findings are stored
/// (so Overview can state the problems without recomputing) and every problem becomes a task, kept up to date by later
/// checks and closed when the problem is gone. Opportunities (severity "info") are reported but never become tasks.
/// Deterministic and local: it reads the project's stored data only.
/// </summary>
public sealed class ProblemCheck(AnalyticsEngine engine, ProjectStore projects, TaskStore tasks)
{
    public const int WindowDays = 30;

    /// <summary>The last 30 complete days. Today is left out: every platform reports it only partially.</summary>
    public static Filters Window(DateOnly today) => new(today.AddDays(-WindowDays), today.AddDays(-1));

    public static bool IsProblem(Finding f) => f.Severity != "info";

    public static string Priority(string severity) => severity switch { "critical" or "high" => "high", "medium" => "medium", _ => "low" };

    /// <summary>Analyses the last 30 days now, stores the result and brings the task list in line with it.</summary>
    /// <param name="afterSync">True when called at the end of a sync (see <see cref="AnalyticsEngine.Findings"/>).</param>
    public TaskChanges Run(string projectId, string trigger, bool afterSync = false)
    {
        var p = projects.Require(projectId);
        var window = Window(AnalyticsEngine.Today(p));
        var findings = engine.Findings(projectId, window, afterSync);
        var seeds = findings.Where(IsProblem)
            .Select(f => new TaskSeed(f.Id, Cut(f.Problem, 200), Cut(f.Action, 4000), Priority(f.Severity), f.Category, Json.Serialize(f)))
            .ToArray();
        var changes = tasks.ApplyFindings(projectId, seeds);
        tasks.SaveCheck(projectId, trigger, window.Start, window.End, Json.Serialize(findings));
        return changes;
    }

    /// <summary>The stored result of the latest check, with each problem linked to its task.</summary>
    public CheckReport Latest(string projectId)
    {
        var p = projects.Require(projectId);
        var sources = p.Connections.Count(c => Integrations.Synced.Contains(c.Integration));
        var counts = tasks.Counts(projectId);
        var check = tasks.LatestCheck(projectId);
        if (check is null) return new CheckReport(null, null, null, WindowDays, sources, [], [], counts);

        var byProblem = tasks.List(projectId).Where(t => t.FindingKey is not null)
            .GroupBy(t => t.FindingKey!).ToDictionary(g => g.Key, g => g.MaxBy(t => t.Id)!);
        using var doc = JsonDocument.Parse(check.FindingsJson);
        var all = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return new CheckReport(check.AnalyzedAt, check.Trigger, $"{check.RangeStart}..{check.RangeEnd}", WindowDays, sources,
            all.Where(e => Text(e, "severity") != "info")
                .Select(e => new CheckProblem(e, Text(e, "id") is { } id && byProblem.TryGetValue(id, out var t) ? new CheckTask(t.Id, t.Status) : null)).ToArray(),
            all.Where(e => Text(e, "severity") == "info").ToArray(),
            counts);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
