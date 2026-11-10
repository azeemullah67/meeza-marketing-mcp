using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Meeza.MarketingMcp.Storage;

/// <summary>One item on a project's to-do list. <see cref="Finding"/> is the problem's evidence for tasks the check created.</summary>
public sealed record ProjectTask(
    long Id, string ProjectId, string Title, string? Details, string Status, string Priority, string Source,
    string? FindingKey, string? Category, JsonElement? Finding, bool Detected, string? ClosedBy, string? DueDate,
    string CreatedAt, string UpdatedAt, string? CompletedAt, string? LastSeenAt);

/// <summary>A problem found by the check, in the shape a task is made from. <see cref="Key"/> is the problem's stable id.</summary>
public sealed record TaskSeed(string Key, string Title, string? Details, string Priority, string? Category, string? FindingJson);

/// <summary>What one check did to the task list.</summary>
public sealed record TaskChanges(int Created, int Updated, int Reopened, int Resolved);

public sealed record TaskCounts(int Open, int InProgress, int Done, int Dismissed)
{
    /// <summary>Tasks that still need work.</summary>
    public int Active => Open + InProgress;
}

public sealed record ProjectCheck(string ProjectId, string AnalyzedAt, string Trigger, string RangeStart, string RangeEnd, string FindingsJson);

/// <summary>
/// Project to-do lists and the latest automatic check. Tasks are written only by the check that runs after every
/// sync: each problem it finds becomes the action to take. People update a task's status, nothing else.
/// A problem owns at most one live task: the check
/// keeps it up to date while the problem lasts and closes it when the problem is gone, so the list never fills with
/// duplicates of the same finding. Every query is scoped by project id.
/// </summary>
public sealed class TaskStore(MarketingDb db)
{
    public static readonly string[] Statuses = ["open", "in_progress", "done", "dismissed"];
    public static readonly string[] Priorities = ["high", "medium", "low"];

    /// <summary>
    /// A problem that comes back within this many days of the check closing its task reopens that task instead of
    /// starting a new one, so a metric hovering around a threshold does not produce a pile of closed tasks.
    /// </summary>
    public const int ReopenWithinDays = 7;

    private const string Order = """
         ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'open' THEN 1 WHEN 'done' THEN 2 ELSE 3 END,
                  CASE priority WHEN 'high' THEN 0 WHEN 'medium' THEN 1 ELSE 2 END, id DESC
        """;

    public IReadOnlyList<ProjectTask> List(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM tasks WHERE project_id=@projectId" + Order, new { projectId }, Map);
    }

    public ProjectTask? Get(string projectId, long id)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM tasks WHERE project_id=@projectId AND id=@id", new { projectId, id }, Map).FirstOrDefault();
    }

    public ProjectTask Require(string projectId, long id) => Get(projectId, id) ?? throw new KeyNotFoundException($"Task {id} does not exist in this project.");

    public TaskCounts Counts(string projectId)
    {
        using var c = db.Open();
        var by = c.Query("SELECT status, COUNT(*) FROM tasks WHERE project_id=@projectId GROUP BY status", new { projectId }, r => (r.GetString(0), r.GetInt32(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
        return new TaskCounts(by.GetValueOrDefault("open"), by.GetValueOrDefault("in_progress"), by.GetValueOrDefault("done"), by.GetValueOrDefault("dismissed"));
    }

    /// <summary>Tasks that still need work, per project (for the navigation badge).</summary>
    public IReadOnlyDictionary<string, int> ActiveCounts()
    {
        using var c = db.Open();
        return c.Query("SELECT project_id, COUNT(*) FROM tasks WHERE status IN ('open','in_progress') GROUP BY project_id", null, r => (r.GetString(0), r.GetInt32(1)))
            .ToDictionary(x => x.Item1, x => x.Item2);
    }

    /// <summary>
    /// The only change a person can make to a task: its status. Tasks are written by the analysis, which owns their
    /// text and priority; there is no way to add, edit or delete one by hand.
    /// </summary>
    public ProjectTask SetStatus(string projectId, long id, string? status)
    {
        var current = Require(projectId, id);
        var s = (status ?? "").Trim().ToLowerInvariant();
        if (!Statuses.Contains(s)) throw new ArgumentException($"Status must be one of: {string.Join(", ", Statuses)}.");
        if (s == current.Status) return current;

        var now = Db.Now();
        var closed = s is "done" or "dismissed";
        var wasClosed = current.Status is "done" or "dismissed";
        // Closing stamps who closed it and when; reopening clears both. Moving between done and dismissed keeps the date.
        var completedAt = closed ? (wasClosed ? current.CompletedAt : now) : null;
        var closedBy = closed ? "user" : null;
        using var c = db.Open();
        c.Exec("UPDATE tasks SET status=@s, completed_at=@completedAt, closed_by=@closedBy, updated_at=@now WHERE project_id=@projectId AND id=@id",
            new { s, completedAt, closedBy, now, projectId, id });
        return Require(projectId, id);
    }

    /// <summary>
    /// Brings the task list in line with the problems the check just found, in one transaction:
    /// a new problem gets a task; a problem that already has a live task refreshes it (status untouched, so work in
    /// progress and tasks the user closed stay as they are); a problem that is no longer found closes its open task.
    /// </summary>
    public TaskChanges ApplyFindings(string projectId, IReadOnlyList<TaskSeed> problems, DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var now = when.ToString("O", CultureInfo.InvariantCulture);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        // The newest task per problem is the live one; older tasks for the same problem are history.
        var latest = c.Query("SELECT * FROM tasks WHERE project_id=@projectId AND source='analysis' AND finding_key IS NOT NULL ORDER BY id", new { projectId }, Map, tx)
            .GroupBy(t => t.FindingKey!).ToDictionary(g => g.Key, g => g.Last());
        int created = 0, updated = 0, reopened = 0, resolved = 0;
        var found = new HashSet<string>();
        foreach (var s in problems)
        {
            if (!found.Add(s.Key)) continue;
            var priority = CleanPriority(s.Priority) ?? "medium";
            var title = CleanTitle(s.Title);
            var details = CleanDetails(s.Details);
            latest.TryGetValue(s.Key, out var t);
            var closed = t?.Status is "done" or "dismissed";
            var recentlyAutoClosed = t is { ClosedBy: "analysis", CompletedAt: not null }
                && when - DateTimeOffset.Parse(t.CompletedAt, CultureInfo.InvariantCulture) <= TimeSpan.FromDays(ReopenWithinDays);
            if (t is null || closed && !t.Detected && !recentlyAutoClosed)
            {
                c.Exec("""
                    INSERT INTO tasks(project_id, title, details, status, priority, source, finding_key, category, finding_json, detected, created_at, updated_at, last_seen_at)
                    VALUES(@projectId, @title, @details, 'open', @priority, 'analysis', @key, @category, @json, 1, @now, @now, @now)
                    """, new { projectId, title, details, priority, key = s.Key, category = s.Category, json = s.FindingJson, now }, tx);
                created++;
                continue;
            }
            var reopen = closed && !t.Detected; // the check closed it a few days ago and the problem is back
            var changed = reopen || t.Title != title || t.Details != details || t.Priority != priority;
            c.Exec("""
                UPDATE tasks SET title=@title, details=@details, priority=@priority, category=@category, finding_json=@json, detected=1, last_seen_at=@now,
                                 status = CASE WHEN @reopen=1 THEN 'open' ELSE status END,
                                 closed_by = CASE WHEN @reopen=1 THEN NULL ELSE closed_by END,
                                 completed_at = CASE WHEN @reopen=1 THEN NULL ELSE completed_at END,
                                 updated_at = CASE WHEN @changed=1 THEN @now ELSE updated_at END
                WHERE id=@id
                """, new { title, details, priority, category = s.Category, json = s.FindingJson, now, reopen, changed, id = t.Id }, tx);
            if (reopen) reopened++; else updated++;
        }
        foreach (var t in latest.Values.Where(t => t.Detected && !found.Contains(t.FindingKey!)))
        {
            if (t.Status is "open" or "in_progress")
            {
                c.Exec("UPDATE tasks SET status='done', closed_by='analysis', completed_at=@now, updated_at=@now, detected=0 WHERE id=@id", new { now, id = t.Id }, tx);
                resolved++;
            }
            else c.Exec("UPDATE tasks SET detected=0 WHERE id=@id", new { id = t.Id }, tx);
        }
        tx.Commit();
        return new TaskChanges(created, updated, reopened, resolved);
    }

    // ---------------------------------------------------------------- latest check

    public void SaveCheck(string projectId, string trigger, DateOnly rangeStart, DateOnly rangeEnd, string findingsJson, DateTimeOffset? at = null)
    {
        using var c = db.Open();
        c.Exec("""
            INSERT INTO project_checks(project_id, analyzed_at, trigger, range_start, range_end, findings_json)
            VALUES(@projectId, @now, @trigger, @rangeStart, @rangeEnd, @findingsJson)
            ON CONFLICT(project_id) DO UPDATE SET analyzed_at=excluded.analyzed_at, trigger=excluded.trigger,
                range_start=excluded.range_start, range_end=excluded.range_end, findings_json=excluded.findings_json
            """, new { projectId, now = (at ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture), trigger, rangeStart, rangeEnd, findingsJson });
    }

    public ProjectCheck? LatestCheck(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM project_checks WHERE project_id=@projectId", new { projectId }, r => new ProjectCheck(
            r.Str("project_id")!, r.Str("analyzed_at")!, r.Str("trigger")!, r.Str("range_start")!, r.Str("range_end")!, r.Str("findings_json")!)).FirstOrDefault();
    }

    // ---------------------------------------------------------------- validation & mapping

    private static string CleanTitle(string? title)
    {
        var t = (title ?? "").Trim();
        if (t.Length == 0) throw new ArgumentException("A task needs a title.");
        return t.Length > 200 ? throw new ArgumentException("Task titles can be at most 200 characters.") : t;
    }

    private static string? CleanDetails(string? details)
    {
        var d = (details ?? "").Trim();
        if (d.Length > 4000) throw new ArgumentException("Task details can be at most 4000 characters.");
        return d.Length == 0 ? null : d;
    }

    private static string? CleanPriority(string? priority)
    {
        if (string.IsNullOrWhiteSpace(priority)) return null;
        var p = priority.Trim().ToLowerInvariant();
        return Priorities.Contains(p) ? p : throw new ArgumentException($"Priority must be one of: {string.Join(", ", Priorities)}.");
    }

    private static ProjectTask Map(SqliteDataReader r) => new(
        r.Long("id")!.Value, r.Str("project_id")!, r.Str("title")!, r.Str("details"), r.Str("status")!, r.Str("priority")!, r.Str("source")!,
        r.Str("finding_key"), r.Str("category"), ParseJson(r.Str("finding_json")), r.Long("detected") == 1, r.Str("closed_by"), r.Str("due_date"),
        r.Str("created_at")!, r.Str("updated_at")!, r.Str("completed_at"), r.Str("last_seen_at"));

    private static JsonElement? ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }
}
