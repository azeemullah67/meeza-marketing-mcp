using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

public class TaskStoreTests
{
    private static TaskSeed Seed(string key, string title = "CPI increased 40%", string priority = "high") =>
        new(key, title, "Check the campaigns.", priority, "acquisition", """{"id":"x","evidence":[]}""");

    [Fact]
    public void Manual_tasks_are_validated_edited_closed_and_deleted()
    {
        using var t = new TestDb();
        var tasks = new TaskStore(t.Db);
        var p = t.NewProject();

        Assert.Throws<ArgumentException>(() => tasks.Create(p.Id, "  ", null, null, null));
        Assert.Throws<ArgumentException>(() => tasks.Create(p.Id, "X", null, "urgent", null));
        Assert.Throws<ArgumentException>(() => tasks.Create(p.Id, "X", null, null, "tomorrow"));
        Assert.Throws<ArgumentException>(() => tasks.Create(p.Id, new string('x', 201), null, null, null));

        var task = tasks.Create(p.Id, "  Record new video ads ", " Three hooks ", null, "2026-12-01");
        Assert.Equal(("Record new video ads", "Three hooks", "medium", "open", "manual", "2026-12-01"),
            (task.Title, task.Details, task.Priority, task.Status, task.Source, task.DueDate));

        // Null leaves a field alone; an empty string clears details and the due date.
        var edited = tasks.Update(p.Id, task.Id, new TaskPatch { Priority = "high", Details = "", DueDate = "" });
        Assert.Equal(("Record new video ads", null, "high", null), (edited.Title, edited.Details, edited.Priority, edited.DueDate));
        Assert.Throws<ArgumentException>(() => tasks.Update(p.Id, task.Id, new TaskPatch { Status = "archived" }));

        var done = tasks.Update(p.Id, task.Id, new TaskPatch { Status = "done" });
        Assert.Equal(("done", "user"), (done.Status, done.ClosedBy));
        Assert.NotNull(done.CompletedAt);
        Assert.Equal(new TaskCounts(0, 0, 1, 0), tasks.Counts(p.Id));

        var reopened = tasks.Update(p.Id, task.Id, new TaskPatch { Status = "in_progress" });
        Assert.Equal(("in_progress", null, null), (reopened.Status, reopened.ClosedBy, reopened.CompletedAt));
        Assert.Equal(1, tasks.ActiveCounts()[p.Id]);

        Assert.True(tasks.Delete(p.Id, task.Id));
        Assert.False(tasks.Delete(p.Id, task.Id));
        Assert.Throws<KeyNotFoundException>(() => tasks.Update(p.Id, task.Id, new TaskPatch { Status = "done" }));
    }

    [Fact]
    public void Tasks_belong_to_one_project_and_go_with_it()
    {
        using var t = new TestDb();
        var tasks = new TaskStore(t.Db);
        var a = t.NewProject("A");
        var b = t.NewProject("B");
        var task = tasks.Create(a.Id, "Only in A", null, null, null);
        tasks.ApplyFindings(a.Id, [Seed("cpi-up")]);

        Assert.Empty(tasks.List(b.Id));
        Assert.Null(tasks.Get(b.Id, task.Id));
        Assert.Throws<KeyNotFoundException>(() => tasks.Update(b.Id, task.Id, new TaskPatch { Status = "done" }));
        Assert.False(tasks.Delete(b.Id, task.Id));
        Assert.Equal(2, tasks.List(a.Id).Count);

        t.Projects.Delete(a.Id);
        Assert.Empty(tasks.List(a.Id));
        Assert.Empty(tasks.ActiveCounts());
    }

    [Fact]
    public void A_problem_has_one_task_that_follows_it_until_it_is_gone()
    {
        using var t = new TestDb();
        var tasks = new TaskStore(t.Db);
        var p = t.NewProject();
        var t0 = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

        // Found: one open task.
        Assert.Equal(new TaskChanges(1, 0, 0, 0), tasks.ApplyFindings(p.Id, [Seed("cpi-up")], t0));
        var task = tasks.List(p.Id).Single();
        Assert.Equal(("open", "analysis", "cpi-up", true, "high"), (task.Status, task.Source, task.FindingKey, task.Detected, task.Priority));
        Assert.NotNull(task.Finding);

        // Still found an hour later: the same task, refreshed. Work in progress is not reset.
        tasks.Update(p.Id, task.Id, new TaskPatch { Status = "in_progress" });
        Assert.Equal(new TaskChanges(0, 1, 0, 0), tasks.ApplyFindings(p.Id, [Seed("cpi-up", "CPI increased 55%", "medium")], t0.AddHours(1)));
        task = tasks.List(p.Id).Single();
        Assert.Equal(("in_progress", "CPI increased 55%", "medium"), (task.Status, task.Title, task.Priority));

        // No longer found: the check closes it and says so.
        Assert.Equal(new TaskChanges(0, 0, 0, 1), tasks.ApplyFindings(p.Id, [], t0.AddHours(2)));
        task = tasks.List(p.Id).Single();
        Assert.Equal(("done", "analysis", false), (task.Status, task.ClosedBy, task.Detected));

        // Back within a week (a metric hovering around its threshold): the same task reopens.
        Assert.Equal(new TaskChanges(0, 0, 1, 0), tasks.ApplyFindings(p.Id, [Seed("cpi-up")], t0.AddDays(2)));
        task = tasks.List(p.Id).Single();
        Assert.Equal(("open", null, null, true), (task.Status, task.ClosedBy, task.CompletedAt, task.Detected));

        // Gone, then back weeks later: a new occurrence gets its own task; the old one stays as history.
        tasks.ApplyFindings(p.Id, [], t0.AddDays(3));
        Assert.Equal(new TaskChanges(1, 0, 0, 0), tasks.ApplyFindings(p.Id, [Seed("cpi-up")], t0.AddDays(30)));
        Assert.Equal(["open", "done"], tasks.List(p.Id).Select(x => x.Status));
    }

    [Fact]
    public void A_task_the_user_closed_stays_closed_while_the_problem_lasts()
    {
        using var t = new TestDb();
        var tasks = new TaskStore(t.Db);
        var p = t.NewProject();
        var t0 = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);
        tasks.ApplyFindings(p.Id, [Seed("wasted-spend"), Seed("cpi-up"), Seed("cpi-up")], t0); // a repeated key counts once
        Assert.Equal(2, tasks.List(p.Id).Count);
        var task = tasks.List(p.Id).Single(x => x.FindingKey == "wasted-spend");

        // The check owns the text of its tasks; the user owns the status.
        Assert.Throws<InvalidOperationException>(() => tasks.Update(p.Id, task.Id, new TaskPatch { Title = "Mine now" }));
        tasks.Update(p.Id, task.Id, new TaskPatch { Status = "dismissed", DueDate = "2026-12-01" });

        // Still found: no new task, still dismissed, and it cannot be deleted (it would come straight back).
        Assert.Equal(new TaskChanges(0, 2, 0, 0), tasks.ApplyFindings(p.Id, [Seed("wasted-spend"), Seed("cpi-up")], t0.AddHours(1)));
        task = tasks.Get(p.Id, task.Id)!;
        Assert.Equal(("dismissed", "user", true, "2026-12-01"), (task.Status, task.ClosedBy, task.Detected, task.DueDate));
        Assert.Throws<InvalidOperationException>(() => tasks.Delete(p.Id, task.Id));

        // Gone: nothing to close (the user already did), and now it can be deleted.
        Assert.Equal(new TaskChanges(0, 1, 0, 0), tasks.ApplyFindings(p.Id, [Seed("cpi-up")], t0.AddHours(2)));
        Assert.False(tasks.Get(p.Id, task.Id)!.Detected);

        // Back after the user dismissed the earlier occurrence: it is a new problem, so it is raised again.
        Assert.Equal(1, tasks.ApplyFindings(p.Id, [Seed("wasted-spend"), Seed("cpi-up")], t0.AddHours(3)).Created);
        Assert.True(tasks.Delete(p.Id, task.Id));
    }

    [Fact]
    public void The_latest_check_replaces_the_previous_one()
    {
        using var t = new TestDb();
        var tasks = new TaskStore(t.Db);
        var p = t.NewProject();
        Assert.Null(tasks.LatestCheck(p.Id));
        tasks.SaveCheck(p.Id, "schedule", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 30), "[]");
        tasks.SaveCheck(p.Id, "manual", new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 31), """[{"id":"cpi-up"}]""");
        var check = tasks.LatestCheck(p.Id)!;
        Assert.Equal(("manual", "2026-10-02", "2026-10-31"), (check.Trigger, check.RangeStart, check.RangeEnd));
        Assert.Contains("cpi-up", check.FindingsJson);
    }
}

public class ProblemCheckTests
{
    /// <summary>60 days of ads: a healthy campaign and (optionally) one that spends without a single install.</summary>
    private static void SeedAds(TestDb t, Project p, DateOnly today, bool leaky)
    {
        var rows = new List<AdDailyRow>();
        for (var i = 1; i <= 60; i++)
        {
            rows.Add(TestDb.Ad(today.AddDays(-i), "c1", "a1", 200, 10_000, 200, 10, name: "Good campaign"));
            if (leaky) rows.Add(TestDb.Ad(today.AddDays(-i), "c2", "a2", 300, 5_000, 50, 0, name: "Leaky campaign"));
        }
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1234567890"), "date", today.AddDays(-60), today, rows);
    }

    private static (ProblemCheck Check, TaskStore Tasks) Build(TestDb t)
    {
        var tasks = new TaskStore(t.Db);
        var engine = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        return (new ProblemCheck(engine, t.Projects, tasks), tasks);
    }

    [Fact]
    public void The_check_covers_the_last_30_complete_days()
    {
        var w = ProblemCheck.Window(new DateOnly(2026, 11, 10));
        Assert.Equal((new DateOnly(2026, 10, 11), new DateOnly(2026, 11, 9), 30), (w.Start, w.End, w.Days));
        Assert.Equal(["high", "high", "medium", "low", "low"], new[] { "critical", "high", "medium", "low", "info" }.Select(ProblemCheck.Priority));
    }

    [Fact]
    public void Problems_are_stored_become_tasks_and_close_when_fixed()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", Currency = "PKR", Connections = [TestDb.GoogleConn] });
        var today = TimeZones.Today("UTC");
        SeedAds(t, p, today, leaky: true);
        t.Syncs.FinishRun(t.Syncs.StartRun(p.Id, Integrations.GoogleAds, "1234567890", "incremental", "t", null, null), "success", 1, today, today.AddDays(-60), null, [], 1);
        var (check, tasks) = Build(t);

        Assert.Null(check.Latest(p.Id).AnalyzedAt); // nothing until the first check
        var first = check.Run(p.Id, "schedule");
        Assert.True(first.Created >= 1);

        var report = check.Latest(p.Id);
        Assert.NotNull(report.AnalyzedAt);
        Assert.Equal(("schedule", $"{Db.Iso(today.AddDays(-30))}..{Db.Iso(today.AddDays(-1))}", 30, 1), (report.Trigger, report.Range, report.WindowDays, report.Sources));
        var wasted = report.Problems.Single(x => x.Finding.GetProperty("id").GetString() == "wasted-spend");
        Assert.Equal("open", wasted.Task!.Status);
        Assert.Equal(first.Created, report.Tasks.Active);
        Assert.All(report.Opportunities, o => Assert.Equal("info", o.GetProperty("severity").GetString()));

        // The task carries the problem, the action and the evidence it is based on.
        var task = tasks.Get(p.Id, wasted.Task.Id)!;
        Assert.Equal(("analysis", "high", "budget", true), (task.Source, task.Priority, task.Category, task.Detected));
        Assert.Contains("spent without tracked installs", task.Title);
        Assert.Contains("install tracking", task.Details);
        Assert.Contains(task.Finding!.Value.GetProperty("evidence").EnumerateArray(), e => e.GetProperty("metric").GetString() == "Leaky campaign");

        // The next sync finds the same problems: no duplicates.
        Assert.Equal(0, check.Run(p.Id, "schedule").Created);
        Assert.Equal(first.Created, tasks.List(p.Id).Count);

        // The campaign is gone from the data: the problem disappears from the report and its task closes itself.
        SeedAds(t, p, today, leaky: false);
        Assert.True(check.Run(p.Id, "manual").Resolved >= 1);
        Assert.DoesNotContain(check.Latest(p.Id).Problems, x => x.Finding.GetProperty("id").GetString() == "wasted-spend");
        task = tasks.Get(p.Id, wasted.Task.Id)!;
        Assert.Equal(("done", "analysis", false), (task.Status, task.ClosedBy, task.Detected));
    }

    [Fact]
    public async Task After_a_sync_a_source_that_failed_is_reported_instead_of_shown_as_syncing()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 10, Connections = [TestDb.GoogleConn] });
        var tasks = new TaskStore(t.Db);
        ProblemCheck? check = null;
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts) { FailOn = (_, _) => new Meeza.Marketing.Shared.Utilities.PlatformApiException("google_ads", "HTTP 401 token expired") };
        var sync = new SyncEngine(t.Projects, t.Syncs, [fake], NullLogger<SyncEngine>.Instance, (id, trigger) => check!.Run(id, trigger, afterSync: true));
        check = new ProblemCheck(new AnalyticsEngine(t.Projects, t.Syncs, t.Data, sync), t.Projects, tasks);

        // Started the way the dashboard does, so the project counts as "syncing" while the check runs.
        Assert.Equal("failed", (await sync.StartProjectSync(p.Id, "manual")).Single().Status);

        var problem = check.Latest(p.Id).Problems.Single();
        Assert.Equal("data-google_ads-1234567890", problem.Finding.GetProperty("id").GetString());
        Assert.Equal("data_quality", problem.Finding.GetProperty("category").GetString());
        var task = tasks.List(p.Id).Single();
        Assert.Equal(("open", "high"), (task.Status, task.Priority));
        Assert.Contains("Google Ads data is missing", task.Title);
    }
}

public class AfterSyncHookTests
{
    private static FakeSyncer Fake(TestDb t) => new(Integrations.GoogleAds, t.Facts) { Rows = (_, to) => [TestDb.Ad(to, "c", "a", 10, 100, 1, 1)] };

    [Fact]
    public async Task The_hook_runs_once_per_project_sync_with_the_trigger()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 10, Connections = [TestDb.GoogleConn] });
        var calls = new List<(string, string)>();
        var sync = new SyncEngine(t.Projects, t.Syncs, [Fake(t)], NullLogger<SyncEngine>.Instance, (id, trigger) => calls.Add((id, trigger)));
        await sync.SyncProjectAsync(p.Id, "schedule", null, default);
        Assert.Equal([(p.Id, "schedule")], calls);

        // A project without anything to synchronize has nothing new to analyse.
        var empty = t.Projects.Create(new Project { Name = "Empty" });
        await sync.SyncProjectAsync(empty.Id, "schedule", null, default);
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_failing_hook_never_fails_the_sync()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 10, Connections = [TestDb.GoogleConn] });
        var sync = new SyncEngine(t.Projects, t.Syncs, [Fake(t)], NullLogger<SyncEngine>.Instance, (_, _) => throw new InvalidOperationException("analysis broke"));
        var outcome = (await sync.SyncProjectAsync(p.Id, "manual", null, default)).Single();
        Assert.Equal("success", outcome.Status);
        Assert.NotNull(t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!.LastSuccessAt);
    }

    [Fact]
    public async Task The_hook_is_left_to_the_process_that_is_actually_syncing()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 10, Connections = [TestDb.GoogleConn] });
        var calls = 0;
        var sync = new SyncEngine(t.Projects, t.Syncs, [Fake(t)], NullLogger<SyncEngine>.Instance, (_, _) => calls++);
        // Another engine in this process holds the account's lease, as a second dashboard or the MCP server would.
        Assert.True(t.Syncs.TryAcquire($"{p.Id}|{Integrations.GoogleAds}|1234567890", $"{Environment.ProcessId}-other", TimeSpan.FromMinutes(5)));
        Assert.Equal("skipped", (await sync.SyncProjectAsync(p.Id, "schedule", null, default)).Single().Status);
        Assert.Equal(0, calls);
    }
}
