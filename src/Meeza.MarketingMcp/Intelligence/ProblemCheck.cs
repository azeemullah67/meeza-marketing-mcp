using System.Text.Json;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>The task that tracks a problem.</summary>
public sealed record CheckTask(long Id, string Status);

/// <summary>A confirmed problem from the latest check and its task (null when it has none).</summary>
public sealed record CheckProblem(JsonElement Finding, CheckTask? Task);

/// <summary>
/// The latest automatic check of a project. <see cref="AnalyzedAt"/> is null when no check has run yet.
/// <see cref="Problems"/> are confirmed and have tasks; <see cref="Unconfirmed"/> appear in the 30 days but not in the
/// most recent days, so they get no task. <see cref="Sources"/> is the number of connected data sources.
/// </summary>
public sealed record CheckReport(
    string? AnalyzedAt, string? Trigger, string? Range, int WindowDays, int ConfirmDays, int Sources,
    IReadOnlyList<CheckProblem> Problems, IReadOnlyList<JsonElement> Unconfirmed, IReadOnlyList<JsonElement> Opportunities, TaskCounts Tasks);

/// <summary>
/// The check that runs after every sync: the analyst's rules over the last 30 complete days. Before a problem becomes
/// a task it is validated: the same rule must also find it in the most recent days. A 30-day total can be dominated
/// by older data (a test period before launch, an old build, a week that is already fixed); a problem that is not in
/// the recent data is shown as "not confirmed" and gets no task. Opportunities (severity "info") never become tasks.
/// Deterministic and local: it reads the project's stored data only.
/// </summary>
public sealed class ProblemCheck(AnalyticsEngine engine, ProjectStore projects, TaskStore tasks)
{
    public const int WindowDays = 30;

    /// <summary>Recent days a problem must also appear in before it becomes a task.</summary>
    public const int ConfirmDays = 7;

    /// <summary>
    /// Problems measured on cohorts need a longer recent window: D7 retention only exists for players who installed
    /// at least 7 days ago, so the last 7 days alone can never show it.
    /// </summary>
    public static int ConfirmDaysFor(string findingId) => findingId is "retention-d7" or "roas-d7" ? ConfirmDays + 7 : ConfirmDays;

    /// <summary>The last N complete days. Today is left out: every platform reports it only partially.</summary>
    public static Filters Window(DateOnly today, int days = WindowDays) => new(today.AddDays(-days), today.AddDays(-1));

    public static bool IsProblem(Finding f) => f.Severity != "info";

    public static string Priority(string severity) => severity switch { "critical" or "high" => "high", "medium" => "medium", _ => "low" };

    /// <summary>Analyses the last 30 days now, validates each problem against recent data, stores the result and brings the task list in line with it.</summary>
    /// <param name="afterSync">True when called at the end of a sync (see <see cref="AnalyticsEngine.Findings"/>).</param>
    public TaskChanges Run(string projectId, string trigger, bool afterSync = false)
    {
        var p = projects.Require(projectId);
        var today = AnalyticsEngine.Today(p);
        var window = Window(today);
        var findings = engine.Findings(projectId, window, afterSync);

        // Recent findings per confirmation window, computed once each.
        var recent = new Dictionary<int, Dictionary<string, Finding>>();
        Finding? Recent(Finding f)
        {
            var days = ConfirmDaysFor(f.Id);
            if (!recent.TryGetValue(days, out var byId))
                recent[days] = byId = engine.Findings(projectId, Window(today, days), afterSync).Where(IsProblem)
                    .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
            return byId.GetValueOrDefault(f.Id);
        }

        var validated = findings.Select(f =>
        {
            if (!IsProblem(f)) return f;
            var days = ConfirmDaysFor(f.Id);
            var range = Window(today, days);
            // A state ("nothing is tracked") is confirmed by checking that the state still holds. Re-running its rule
            // on a short window would fail on the rule's minimum sample, not because the problem is gone.
            var state = ConfirmState(projectId, f, range);
            if (state is { } s)
                return s.Holds
                    ? f with { Confirmed = true, Validated = $"Confirmed: {s.Detail} in the last {days} days ({range.Range}).", Evidence = [.. f.Evidence, new Evidence($"Last {days} days", s.Detail, range.Range, "Validation")] }
                    : f with { Confirmed = false, Validated = $"Not confirmed: {s.Detail} in the last {days} days ({range.Range}), so no task was created." };
            var r = Recent(f);
            return r is null
                ? f with { Confirmed = false, Validated = $"Not confirmed: the last {days} days ({range.Range}) do not show this problem, or have too little data to judge it, so no task was created." }
                : f with
                {
                    Confirmed = true,
                    Validated = $"Confirmed: also found in the last {days} days ({range.Range}).",
                    Evidence = [.. f.Evidence, new Evidence($"Last {days} days", r.Problem, range.Range, "Validation")],
                };
        }).ToArray();

        var seeds = validated.Where(f => f.Confirmed == true)
            // The task is the action to take; the problem it fixes is its "why".
            .Select(f => new TaskSeed(f.Id, Cut(f.Action, 200), Cut(f.Problem, 4000), Priority(f.Severity), f.Category, Json.Serialize(f)))
            .ToArray();
        var changes = tasks.ApplyFindings(projectId, seeds);
        tasks.SaveCheck(projectId, trigger, window.Start, window.End, Json.Serialize(validated));
        return changes;
    }

    /// <summary>The stored result of the latest check, with each confirmed problem linked to its task.</summary>
    public CheckReport Latest(string projectId)
    {
        var p = projects.Require(projectId);
        var sources = p.Connections.Count(c => Integrations.Synced.Contains(c.Integration));
        var counts = tasks.Counts(projectId);
        var check = tasks.LatestCheck(projectId);
        if (check is null) return new CheckReport(null, null, null, WindowDays, ConfirmDays, sources, [], [], [], counts);

        var byProblem = tasks.List(projectId).Where(t => t.FindingKey is not null)
            .GroupBy(t => t.FindingKey!).ToDictionary(g => g.Key, g => g.MaxBy(t => t.Id)!);
        using var doc = JsonDocument.Parse(check.FindingsJson);
        var all = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        // A check stored before validation existed has no "confirmed" flag: those problems already had tasks.
        static bool Confirmed(JsonElement e) => !e.TryGetProperty("confirmed", out var v) || v.ValueKind != JsonValueKind.False;
        var problems = all.Where(e => Text(e, "severity") != "info").ToArray();
        return new CheckReport(check.AnalyzedAt, check.Trigger, $"{check.RangeStart}..{check.RangeEnd}", WindowDays, ConfirmDays, sources,
            problems.Where(Confirmed)
                .Select(e => new CheckProblem(e, Text(e, "id") is { } id && byProblem.TryGetValue(id, out var t) ? new CheckTask(t.Id, t.Status) : null)).ToArray(),
            problems.Where(e => !Confirmed(e)).ToArray(),
            all.Where(e => Text(e, "severity") == "info").ToArray(),
            counts);
    }

    /// <summary>Direct check of the recent state for problems that describe a state rather than a rate. Null = validate by re-running the rule.</summary>
    private (bool Holds, string Detail)? ConfirmState(string projectId, Finding f, Filters range)
    {
        if (f.Id != "revenue-missing") return null;
        var days = engine.Data.Ga4Daily(projectId, range.Start, range.End);
        var userDays = days.Sum(d => d.ActiveUsers ?? 0);
        var revenue = days.Sum(d => d.TotalRevenue ?? 0);
        if (userDays == 0) return (false, "there is no GA4 activity to check");
        return revenue == 0 ? (true, $"still no revenue across {userDays:N0} user-days") : (false, $"revenue is now recorded ({revenue:0.##})");
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
