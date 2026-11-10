using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Meeza.Marketing.Shared.Security;
using Meeza.MarketingMcp.Configuration;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Meeza.MarketingMcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.Tests;

// ======================================================================== fixtures

internal sealed class TestDb : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"meeza-test-{Guid.NewGuid():N}.db");
    public MarketingDb Db { get; }
    public ProjectStore Projects { get; }
    public FactStore Facts { get; }
    public SyncStore Syncs { get; }
    public ProjectData Data { get; }

    public TestDb()
    {
        Db = new MarketingDb(Path);
        Projects = new ProjectStore(Db);
        Facts = new FactStore(Db);
        Syncs = new SyncStore(Db);
        Data = new ProjectData(Db);
    }

    public Project NewProject(string name = "Gapsy", string? tz = "UTC", string? currency = "PKR", params ProjectConnection[] conns) =>
        Projects.Create(new Project { Name = name, TimeZone = tz, Currency = currency, Connections = conns.ToList() });

    public static readonly ProjectConnection GoogleConn = new(Integrations.GoogleAds, "1234567890", "Gapsy", "PKR", "UTC");
    public static readonly ProjectConnection MetaConn = new(Integrations.Meta, "act_1000000000000001", "Meta", "PKR", "UTC");
    public static readonly ProjectConnection Ga4Conn = new(Integrations.Ga4, "123456789", "GA4", "PKR", "UTC");

    public static Dictionary<string, object?> Scope(string pid, string platform, string account) =>
        new() { ["project_id"] = pid, ["platform"] = platform, ["account_id"] = account };

    public static AdDailyRow Ad(DateOnly d, string campaign, string ad, decimal spend, long imps, long clicks, decimal? installs, string currency = "PKR", string adset = "g1", string? name = null) =>
        new(d, campaign, name ?? $"Campaign {campaign}", "ENABLED", "MULTI_CHANNEL", adset, "Group", "ENABLED", ad, $"Ad {ad}", "ENABLED", currency,
            spend, imps, null, clicks, installs, installs, installs is null ? null : "test", null);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { Path, Path + "-wal", Path + "-shm" })
            try { File.Delete(f); } catch { /* best effort */ }
    }
}

internal sealed class FakeSyncer(string integration, FactStore facts) : IIntegrationSyncer
{
    public Func<DateOnly, DateOnly, IReadOnlyList<AdDailyRow>> Rows { get; set; } = (_, _) => [];
    public Func<DateOnly, DateOnly, Exception?> FailOn { get; set; } = (_, _) => null;
    public List<(DateOnly From, DateOnly To)> Calls { get; } = [];
    public string? TimeZone { get; set; } = "UTC";
    public string Integration => integration;
    public bool IsConfigured { get; set; } = true;
    public string ConfigurationHint => "configure me";
    public int LookbackDays { get; set; } = 3;
    public int ChunkDays { get; set; } = 10;

    public Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct) => Task.FromResult(new AccountInfo(externalId, "Fake", "PKR", TimeZone));

    public Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct)
    {
        Calls.Add((from, to));
        if (FailOn(from, to) is { } ex) throw ex;
        var rows = Rows(from, to).Where(r => r.Date >= from && r.Date <= to).ToArray();
        var result = new WindowResult();
        result.Add(facts.ReplaceWindow("ad_daily", TestDb.Scope(projectId, integration, account.ExternalId), "date", from, to, rows), "ads");
        foreach (var r in rows.Where(r => r.Spend > 0)) result.Seen(r.Date);
        return Task.FromResult(result);
    }
}

// ======================================================================== storage & isolation

public class StorageTests
{
    [Fact]
    public void Migrations_create_schema_and_are_idempotent()
    {
        using var t = new TestDb();
        var latest = Migrations.All.Max(m => m.Version);
        Assert.Equal(latest, t.Db.SchemaVersion);
        var again = new MarketingDb(t.Path); // re-open runs migrations again: must be a no-op
        Assert.Equal(latest, again.SchemaVersion);
    }

    [Fact]
    public void Project_validation_rejects_bad_input()
    {
        using var t = new TestDb();
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "" }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", TargetCountries = "USA" }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", AndroidStoreUrl = "http://insecure.example" }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", TimeZone = "Mars/Olympus" }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", Connections = [new(Integrations.GoogleAds, "abc", null, null, null)] }));
        var ok = t.Projects.Create(new Project { Name = " Gapsy ", TargetCountries = "ph, us,PH", Platforms = "Android", Currency = "pkr" });
        Assert.Equal("Gapsy", ok.Name);
        Assert.Equal("PH,US", ok.TargetCountries);
        Assert.Equal("android", ok.Platforms);
        Assert.Equal("PKR", ok.Currency);
    }

    [Fact]
    public void Connection_ids_are_normalized_so_an_account_is_never_linked_twice()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: [new(Integrations.GoogleAds, "123-456-7890", null, null, null), new(Integrations.Meta, "1000000000000001", null, null, null)]);
        var p2 = t.Projects.SetConnections(p.Id, [new(Integrations.GoogleAds, "1234567890", null, null, null), new(Integrations.Meta, "act_1000000000000001", null, null, null)]);
        Assert.Equal(2, p2.Connections.Count);
        Assert.Contains(p2.Connections, c => c.ExternalId == "1234567890");
        Assert.Contains(p2.Connections, c => c.ExternalId == "act_1000000000000001");
    }

    [Fact]
    public void Projects_are_isolated_and_deleting_one_removes_only_its_data()
    {
        using var t = new TestDb();
        var a = t.NewProject("A", conns: TestDb.GoogleConn);
        var b = t.NewProject("B", conns: TestDb.GoogleConn); // same account linked to two projects
        var d = new DateOnly(2026, 11, 1);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(a.Id, "google_ads", "1234567890"), "date", d, d, [TestDb.Ad(d, "c1", "ad1", 100, 1000, 10, 5)]);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(b.Id, "google_ads", "1234567890"), "date", d, d, [TestDb.Ad(d, "c9", "ad9", 7, 70, 1, 1)]);
        var f = new Filters(d, d);
        Assert.Equal(100m, t.Data.Ads(a.Id, f).Sum(x => x.Spend));
        Assert.Equal(7m, t.Data.Ads(b.Id, f).Sum(x => x.Spend));
        Assert.True(t.Projects.Delete(a.Id));
        Assert.Empty(t.Data.Ads(a.Id, f));
        Assert.Single(t.Data.Ads(b.Id, f));
    }

    [Fact]
    public void Unlinking_an_account_purges_its_data_from_the_project()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: [TestDb.GoogleConn, TestDb.MetaConn]);
        var d = new DateOnly(2026, 11, 1);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1234567890"), "date", d, d, [TestDb.Ad(d, "c1", "a1", 10, 100, 1, 1)]);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "meta", "act_1000000000000001"), "date", d, d, [TestDb.Ad(d, "m1", "m1", 20, 200, 2, 2)]);
        t.Projects.SetConnections(p.Id, [TestDb.GoogleConn]);
        var ads = t.Data.Ads(p.Id, new Filters(d, d));
        Assert.Single(ads);
        Assert.Equal("google_ads", ads[0].Platform);
    }

    [Fact]
    public void Targets_upsert_by_scope_and_validate_metric()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        t.Projects.UpsertTarget(p.Id, "cpi", "all", null, 25, null);
        t.Projects.UpsertTarget(p.Id, "cpi", "all", null, 22, "revised");
        t.Projects.UpsertTarget(p.Id, "cpi", "country", "ph", 18, null);
        var targets = t.Projects.Targets(p.Id);
        Assert.Equal(2, targets.Count);
        Assert.Equal(22m, targets.Single(x => x.ScopeType == "all").Target);
        Assert.Equal("PH", targets.Single(x => x.ScopeType == "country").ScopeValue);
        Assert.Throws<ArgumentException>(() => t.Projects.UpsertTarget(p.Id, "vibes", "all", null, 1, null));
        Assert.Throws<ArgumentException>(() => t.Projects.UpsertTarget(p.Id, "cpi", "country", "", 1, null));
        // Most specific scope wins.
        Assert.Equal(18m, AnalyticsEngine.TargetFor(targets, "cpi", new Filters(default, default, Country: "PH"))!.Target);
        Assert.Equal(22m, AnalyticsEngine.TargetFor(targets, "cpi", new Filters(default, default, Country: "US"))!.Target);
    }

    [Theory]
    [InlineData("AdsetId", "adset_id")]
    [InlineData("DayN", "day_n")]
    [InlineData("Active7d", "active_7d")]
    [InlineData("Active28d", "active_28d")]
    [InlineData("VideoP25", "video_p25")]
    [InlineData("VideoP100", "video_p100")]
    [InlineData("CohortDate", "cohort_date")]
    public void Record_properties_map_to_snake_case_columns(string prop, string column) => Assert.Equal(column, FactStore.Snake(prop));
}

public class FactStoreTests
{
    private static readonly DateOnly D1 = new(2026, 11, 1), D2 = new(2026, 11, 2), D3 = new(2026, 11, 3);

    [Fact]
    public void Replacing_the_same_window_twice_never_duplicates_rows()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        var rows = new[] { TestDb.Ad(D1, "c", "a", 10, 100, 1, 1), TestDb.Ad(D2, "c", "a", 20, 200, 2, 2) };
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1"), "date", D1, D2, rows);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1"), "date", D1, D2, rows);
        Assert.Equal(2, t.Data.Ads(p.Id, new Filters(D1, D3)).Count);
    }

    [Fact]
    public void Restated_values_overwrite_and_rows_outside_the_window_are_untouched()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        var scope = TestDb.Scope(p.Id, "google_ads", "1");
        t.Facts.ReplaceWindow("ad_daily", scope, "date", D1, D3, [TestDb.Ad(D1, "c", "a", 10, 100, 1, 1), TestDb.Ad(D3, "c", "a", 30, 300, 3, 3)]);
        // Later sync of D3 only, with a delayed conversion added.
        t.Facts.ReplaceWindow("ad_daily", scope, "date", D3, D3, [TestDb.Ad(D3, "c", "a", 30, 300, 3, 4)]);
        var ads = t.Data.Ads(p.Id, new Filters(D1, D3));
        Assert.Equal(1m, ads.Single(a => a.Date == D1).Installs);
        Assert.Equal(4m, ads.Single(a => a.Date == D3).Installs);
    }

    [Fact]
    public void An_empty_response_never_wipes_previously_stored_data()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        var scope = TestDb.Scope(p.Id, "google_ads", "1");
        t.Facts.ReplaceWindow("ad_daily", scope, "date", D1, D2, [TestDb.Ad(D1, "c", "a", 10, 100, 1, 1)]);
        var r = t.Facts.ReplaceWindow("ad_daily", scope, "date", D1, D2, Array.Empty<AdDailyRow>());
        Assert.True(r.Skipped);
        Assert.NotNull(r.Warning);
        Assert.Single(t.Data.Ads(p.Id, new Filters(D1, D2)));
        // Explicitly confirmed emptiness may replace.
        t.Facts.ReplaceWindow("ad_daily", scope, "date", D1, D2, Array.Empty<AdDailyRow>(), allowEmptyReplace: true);
        Assert.Empty(t.Data.Ads(p.Id, new Filters(D1, D2)));
    }

    [Fact]
    public void Null_installs_stay_null_and_are_not_turned_into_zero()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1"), "date", D1, D1, [TestDb.Ad(D1, "c", "a", 10, 100, 1, null)]);
        var perf = Perf.Of(t.Data.Ads(p.Id, new Filters(D1, D1)));
        Assert.Null(perf.Installs);
        Assert.Null(perf.Cpi);
        Assert.Null(perf.Ipm);
    }
}

// ======================================================================== synchronization

public class SyncPlanTests
{
    private static readonly DateOnly Today = new(2026, 11, 9);

    [Fact]
    public void First_run_fetches_recent_days_first_then_backfills_history_in_chunks()
    {
        var plan = SyncEngine.Plan(Today, backfillDays: 30, lookbackDays: 3, chunkDays: 10, latestDataDate: null, backfilledFrom: null);
        Assert.False(plan[0].Backfill);
        Assert.Equal(Today, plan[0].To);
        Assert.Equal(Today.AddDays(-3), plan[0].From);
        Assert.All(plan.Skip(1), w => Assert.True(w.Backfill));
        Assert.Equal(Today.AddDays(-29), plan.Min(w => w.From));
        // Contiguous, non-overlapping, newest first.
        var days = plan.SelectMany(w => Enumerable.Range(0, w.To.DayNumber - w.From.DayNumber + 1).Select(i => w.From.AddDays(i))).ToArray();
        Assert.Equal(days.Length, days.Distinct().Count());
        Assert.Equal(30, days.Length);
        Assert.All(plan, w => Assert.True(w.To.DayNumber - w.From.DayNumber < 10));
    }

    [Fact]
    public void Incremental_run_refetches_the_lookback_for_delayed_conversions()
    {
        var plan = SyncEngine.Plan(Today, 30, 14, 31, latestDataDate: "2026-11-09", backfilledFrom: "2026-10-11");
        Assert.Single(plan);
        Assert.Equal(new DateOnly(2026, 10, 26), plan[0].From);
        Assert.Equal(Today, plan[0].To);
    }

    [Fact]
    public void A_gap_since_the_last_sync_is_covered()
    {
        var plan = SyncEngine.Plan(Today, 90, 3, 31, latestDataDate: "2026-10-20", backfilledFrom: "2026-08-12");
        Assert.Equal(new DateOnly(2026, 10, 17), plan.Min(w => w.From));
        Assert.All(plan, w => Assert.False(w.Backfill));
    }

    [Fact]
    public void An_interrupted_backfill_resumes_where_it_stopped()
    {
        var plan = SyncEngine.Plan(Today, 90, 3, 31, latestDataDate: "2026-11-09", backfilledFrom: "2026-10-01");
        var back = plan.Where(w => w.Backfill).ToArray();
        Assert.NotEmpty(back);
        Assert.Equal(new DateOnly(2026, 9, 30), back.Max(w => w.To));
        Assert.Equal(Today.AddDays(-89), back.Min(w => w.From));
    }

    [Fact]
    public void Today_respects_the_account_time_zone()
    {
        var utc = new DateTimeOffset(2026, 11, 9, 21, 0, 0, TimeSpan.Zero); // 02:00 next day in Karachi, 13:00 in Los Angeles
        Assert.Equal(new DateOnly(2026, 11, 10), TimeZones.Today("Asia/Karachi", utc));
        Assert.Equal(new DateOnly(2026, 11, 9), TimeZones.Today("America/Los_Angeles", utc));
    }
}

public class SyncEngineTests
{
    private static SyncEngine Engine(TestDb t, params IIntegrationSyncer[] s) => new(t.Projects, t.Syncs, s, NullLogger<SyncEngine>.Instance);

    [Fact]
    public async Task Backfill_and_incremental_store_data_and_report_status()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 20, Connections = [TestDb.GoogleConn] });
        var today = TimeZones.Today("UTC");
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts)
        {
            Rows = (from, to) => Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => TestDb.Ad(from.AddDays(i), "c", "a", 10, 100, 2, 1)).ToArray(),
        };
        var outcome = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("success", outcome.Status);
        Assert.Equal(today, outcome.LatestDataDate);
        Assert.Equal(20, t.Data.Ads(p.Id, new Filters(today.AddDays(-40), today)).Count);
        var state = t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!;
        Assert.Equal(Db.Iso(today.AddDays(-19)), state.BackfilledFrom);
        Assert.NotNull(state.LastSuccessAt);
        var run = t.Syncs.Runs(p.Id).First();
        Assert.Equal("backfill", run.Kind);
        Assert.True(run.Records >= 20);

        // Second run: incremental only, still exactly one row per day.
        fake.Calls.Clear();
        var second = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("success", second.Status);
        Assert.Single(fake.Calls);
        Assert.Equal("incremental", t.Syncs.Runs(p.Id).First().Kind);
        Assert.Equal(20, t.Data.Ads(p.Id, new Filters(today.AddDays(-40), today)).Count);
    }

    [Fact]
    public async Task A_failed_sync_keeps_previous_data_and_does_not_advance_freshness()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 10, Connections = [TestDb.GoogleConn] });
        var today = TimeZones.Today("UTC");
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts) { Rows = (from, to) => [TestDb.Ad(to, "c", "a", 10, 100, 1, 1)] };
        await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default);
        var before = t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!;

        fake.FailOn = (_, _) => new Meeza.Marketing.Shared.Utilities.PlatformApiException("google_ads", "HTTP 503 backend unavailable");
        var failed = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("failed", failed.Status);
        Assert.Contains("503", failed.Error);
        var after = t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!;
        Assert.Equal(before.LatestDataDate, after.LatestDataDate);
        Assert.Equal(before.LastSuccessAt, after.LastSuccessAt);
        Assert.Equal(1, after.ConsecutiveFailures);
        Assert.NotEmpty(t.Data.Ads(p.Id, new Filters(today.AddDays(-10), today)));
        var status = Freshness.Evaluate(TestDb.GoogleConn, after, 60, false, DateTimeOffset.UtcNow);
        Assert.Equal("error", status.Freshness);
    }

    [Fact]
    public async Task Partial_failure_keeps_completed_windows_and_resumes_later()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 30, Connections = [TestDb.GoogleConn] });
        var today = TimeZones.Today("UTC");
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts)
        {
            Rows = (from, to) => Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => TestDb.Ad(from.AddDays(i), "c", "a", 1, 10, 1, 1)).ToArray(),
            FailOn = (from, _) => from < today.AddDays(-15) ? new HttpRequestException("rate limited") : null,
        };
        var o = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("partial", o.Status);
        var state = t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!;
        Assert.True(Db.Date(state.BackfilledFrom!) > today.AddDays(-29));

        fake.FailOn = (_, _) => null;
        fake.Calls.Clear();
        var resumed = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("success", resumed.Status);
        Assert.Contains(fake.Calls, c => c.From == today.AddDays(-29));
        Assert.Equal(Db.Iso(today.AddDays(-29)), t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!.BackfilledFrom);
    }

    [Fact]
    public async Task Unconfigured_credentials_are_reported_not_hidden()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", Connections = [TestDb.MetaConn] });
        var fake = new FakeSyncer(Integrations.Meta, t.Facts) { IsConfigured = false };
        var o = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("failed", o.Status);
        Assert.Contains("not configured", o.Error);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Lease_prevents_two_processes_syncing_the_same_account()
    {
        using var t = new TestDb();
        Assert.True(t.Syncs.TryAcquire("k", "a", TimeSpan.FromMinutes(5)));
        Assert.False(t.Syncs.TryAcquire("k", "b", TimeSpan.FromMinutes(5)));
        Assert.True(t.Syncs.TryAcquire("k", "a", TimeSpan.FromMinutes(5))); // re-entrant for the owner
        t.Syncs.Release("k", "a");
        Assert.True(t.Syncs.TryAcquire("k", "b", TimeSpan.FromMinutes(5)));
        Assert.True(t.Syncs.TryAcquire("expired", "a", TimeSpan.FromSeconds(-1)));
        Assert.True(t.Syncs.TryAcquire("expired", "b", TimeSpan.FromMinutes(5))); // expired lease can be taken over
    }

    [Fact]
    public async Task A_lease_left_by_a_closed_app_is_taken_over_and_its_run_marked_interrupted()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 5, Connections = [TestDb.GoogleConn] });
        var key = $"{p.Id}|{Integrations.GoogleAds}|1234567890";
        const string deadOwner = "2147483644-0000"; // no such process
        Assert.False(SyncEngine.OwnerAlive(deadOwner));
        Assert.True(t.Syncs.TryAcquire(key, deadOwner, TimeSpan.FromMinutes(45)));
        var orphan = t.Syncs.StartRun(p.Id, Integrations.GoogleAds, "1234567890", "backfill", "project_created", null, null);

        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts) { Rows = (from, to) => [TestDb.Ad(to, "c", "a", 1, 10, 1, 1)] };
        var o = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("success", o.Status);
        var old = t.Syncs.Runs(p.Id).Single(r => r.Id == orphan);
        Assert.Equal("failed", old.Status);
        Assert.Contains("Interrupted", old.Error);
        Assert.Null(t.Syncs.LeaseOwner(key)); // released after the run
    }

    [Fact]
    public async Task A_lease_held_by_a_running_app_is_respected()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", Connections = [TestDb.GoogleConn] });
        var key = $"{p.Id}|{Integrations.GoogleAds}|1234567890";
        var liveOwner = $"{Environment.ProcessId}-another-engine";
        Assert.True(SyncEngine.OwnerAlive(liveOwner));
        Assert.True(t.Syncs.TryAcquire(key, liveOwner, TimeSpan.FromMinutes(45)));
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts);
        var o = (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single();
        Assert.Equal("skipped", o.Status);
        Assert.Empty(fake.Calls);
        Assert.Equal(liveOwner, t.Syncs.LeaseOwner(key));
    }

    [Fact]
    public async Task An_interrupted_backfill_keeps_its_progress_and_resumes_from_there()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", BackfillDays = 30, Connections = [TestDb.GoogleConn] });
        var today = TimeZones.Today("UTC");
        var fake = new FakeSyncer(Integrations.GoogleAds, t.Facts)
        {
            Rows = (from, to) => Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => TestDb.Ad(from.AddDays(i), "c", "a", 1, 10, 1, 1)).ToArray(),
            FailOn = (from, _) => from < today.AddDays(-15) ? new OperationCanceledException("app closed") : null,
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default));
        var state = t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!;
        Assert.NotNull(state.BackfilledFrom); // progress saved per window, not only at the end
        Assert.Null(state.LastSuccessAt);     // but the run itself is not reported as a success

        fake.FailOn = (_, _) => null;
        fake.Calls.Clear();
        Assert.Equal("success", (await Engine(t, fake).SyncProjectAsync(p.Id, "test", null, default)).Single().Status);
        Assert.DoesNotContain(fake.Calls, c => c.From > Db.Date(state.BackfilledFrom!) && c.To < today.AddDays(-20)); // done windows are not refetched
        Assert.Equal(Db.Iso(today.AddDays(-29)), t.Syncs.State(p.Id, Integrations.GoogleAds, "1234567890")!.BackfilledFrom);
    }

    [Fact]
    public void Runs_left_running_by_a_crash_are_recovered_as_failed()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: TestDb.GoogleConn);
        t.Syncs.StartRun(p.Id, Integrations.GoogleAds, "1234567890", "incremental", "test", null, null);
        Assert.Equal(1, t.Syncs.RecoverAbandonedRuns(TimeSpan.FromSeconds(-1)));
        Assert.Equal("failed", t.Syncs.Runs(p.Id).Single().Status);
    }

    [Fact]
    public void Scheduler_due_logic_backs_off_after_failures()
    {
        var p = new Project { Name = "G", SyncIntervalMinutes = 60, Connections = [TestDb.GoogleConn] };
        var now = DateTimeOffset.UtcNow;
        SyncState S(int minutesAgo, int failures, string status = "success") => new("p", Integrations.GoogleAds, "1234567890",
            now.AddMinutes(-minutesAgo).ToString("O", CultureInfo.InvariantCulture), null, status, null, null, null, null, null, failures);
        Assert.True(SyncScheduler.Due(p, [], now));                 // never synced
        Assert.False(SyncScheduler.Due(p, [S(30, 0)], now));        // inside interval
        Assert.True(SyncScheduler.Due(p, [S(61, 0)], now));
        Assert.True(SyncScheduler.Due(p, [S(16, 1, "failed")], now));   // first retry after 15 min
        Assert.False(SyncScheduler.Due(p, [S(50, 3, "failed")], now));  // 3rd failure waits 60 min
        Assert.False(SyncScheduler.Due(p, [S(300, 10, "failed")], now)); // capped at 6 h
        Assert.True(SyncScheduler.Due(p, [S(361, 10, "failed")], now));
    }
}

public class FreshnessTests
{
    private static SyncState State(string? success, string status, string? latest, int failures = 0) =>
        new("p", Integrations.GoogleAds, "1", success, success, status, status == "failed" ? "boom" : null, latest, null, 10, 100, failures);

    [Fact]
    public void Classifies_never_stale_error_delayed_fresh()
    {
        var now = DateTimeOffset.UtcNow;
        var today = Db.Iso(TimeZones.Today("UTC", now));
        var yesterday = Db.Iso(TimeZones.Today("UTC", now).AddDays(-1));
        var c = TestDb.GoogleConn;
        Assert.Equal("never", Freshness.Evaluate(c, null, 60, false, now).Freshness);
        Assert.Equal("syncing", Freshness.Evaluate(c, null, 60, true, now).Freshness);
        Assert.Equal("stale", Freshness.Evaluate(c, State(now.AddDays(-2).ToString("O"), "success", today), 60, false, now).Freshness);
        Assert.Equal("error", Freshness.Evaluate(c, State(now.AddMinutes(-30).ToString("O"), "failed", today, 1), 60, false, now).Freshness);
        Assert.Equal("delayed", Freshness.Evaluate(c, State(now.AddMinutes(-5).ToString("O"), "success", "2020-01-01"), 60, false, now).Freshness);
        Assert.Equal("fresh", Freshness.Evaluate(c, State(now.AddMinutes(-5).ToString("O"), "success", yesterday), 60, false, now).Freshness);
    }
}

// ======================================================================== retention

public class CohortRetentionTests
{
    private static readonly DateOnly Today = new(2026, 11, 20);

    private static IEnumerable<CohortCell> Cohort(DateOnly date, long users, params long[] activeByDay) =>
        activeByDay.Select((a, d) => new CohortCell("all", "", date, d, a, users));

    [Fact]
    public void Cells_are_mature_provisional_or_immature()
    {
        Assert.Equal(CellStatus.Mature, RetentionAnalytics.Status(new DateOnly(2026, 11, 1), 1, Today));
        Assert.Equal(CellStatus.Provisional, RetentionAnalytics.Status(new DateOnly(2026, 11, 17), 1, Today)); // reached Nov 18, inside lag
        Assert.Equal(CellStatus.Immature, RetentionAnalytics.Status(new DateOnly(2026, 11, 19), 1, Today));    // reaches today
        Assert.Equal(CellStatus.Immature, RetentionAnalytics.Status(new DateOnly(2026, 11, 10), 30, Today));
    }

    [Fact]
    public void Immature_cohorts_are_never_counted_as_zero()
    {
        // Old cohort: 100 users, 40 back on D1, 20 on D7. Young cohort (yesterday): 1,000 users, no D1 yet.
        var cells = Cohort(new DateOnly(2026, 11, 1), 100, 100, 40, 30, 28, 26, 24, 22, 20)
            .Concat(Cohort(new DateOnly(2026, 11, 19), 1000, 1000));
        var curve = RetentionAnalytics.Curve(cells, Today);
        Assert.Equal(40m, curve[1].Percent);   // not (40 + 0) / 1100
        Assert.Equal(100, curve[1].CohortUsers);
        Assert.Equal(20m, curve[7].Percent);
        Assert.Null(curve[30].Percent);        // no cohort reached day 30: null, not 0
        var heat = RetentionAnalytics.Heatmap(cells, Today);
        Assert.Null(heat.First(h => h.Cohort == new DateOnly(2026, 11, 19)).Cells[1].Percent);
    }

    [Fact]
    public void A_reached_day_with_no_returning_users_is_a_real_zero()
    {
        var cells = new[] { new CohortCell("all", "", new DateOnly(2026, 11, 1), 0, 10, 10) }; // GA4 omits rows with 0 active
        var heat = RetentionAnalytics.Heatmap(cells, Today);
        Assert.Equal(0m, heat[0].Cells[1].Percent);
        Assert.Equal(CellStatus.Mature, heat[0].Cells[1].Status);
    }

    [Fact]
    public void Curve_is_cohort_size_weighted_and_excludes_provisional_by_default()
    {
        var cells = Cohort(new DateOnly(2026, 11, 1), 100, 100, 50)
            .Concat(Cohort(new DateOnly(2026, 11, 2), 300, 300, 30))
            .Concat(Cohort(new DateOnly(2026, 11, 17), 1000, 1000, 900)); // provisional D1
        var curve = RetentionAnalytics.Curve(cells, Today);
        Assert.Equal(20m, curve[1].Percent); // (50+30)/(100+300)
        var withProv = RetentionAnalytics.Curve(cells, Today, includeProvisional: true);
        Assert.True(withProv[1].IncludesProvisional);
        Assert.Equal(1400, withProv[1].CohortUsers);
    }

    [Fact]
    public void Segments_are_compared_separately()
    {
        var cells = new[]
        {
            new CohortCell("country", "PH", new DateOnly(2026, 11, 1), 0, 100, 100), new CohortCell("country", "PH", new DateOnly(2026, 11, 1), 1, 30, 100),
            new CohortCell("country", "US", new DateOnly(2026, 11, 1), 0, 50, 50), new CohortCell("country", "US", new DateOnly(2026, 11, 1), 1, 10, 50),
        };
        var segs = RetentionAnalytics.BySegment(cells, Today);
        Assert.Equal("PH", segs[0].Segment);
        Assert.Equal(30m, segs[0].Curve[1].Percent);
        Assert.Equal(20m, segs[1].Curve[1].Percent);
    }

    [Fact]
    public void Observed_active_days_sum_mature_retention()
    {
        var cells = Cohort(new DateOnly(2026, 11, 1), 100, 100, 50, 25);
        var curve = RetentionAnalytics.Curve(cells, Today);
        Assert.Equal(1.75m, RetentionAnalytics.ObservedActiveDays(curve, 2));
        Assert.Null(RetentionAnalytics.ObservedActiveDays(curve, 30)); // day 3+ never observed, cohort mature -> 0? (rows missing = real zeros)
    }
}

// ======================================================================== KPIs, flags, findings (integration over a seeded store)

public class AnalyticsEngineTests
{
    private static (TestDb T, AnalyticsEngine E, Project P) Seed(Action<TestDb, Project, DateOnly>? extra = null)
    {
        var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "G", TimeZone = "UTC", Currency = "PKR", Connections = [TestDb.GoogleConn, TestDb.Ga4Conn] });
        var today = TimeZones.Today("UTC");
        // previous 7 days: CPI 20; current 7 days: CPI 40 (CPM up, same CTR / CVR)
        var rows = new List<AdDailyRow>();
        for (var i = 1; i <= 14; i++)
        {
            var d = today.AddDays(-i);
            var current = i <= 7;
            rows.Add(TestDb.Ad(d, "c1", "a1", current ? 400 : 200, 10_000, 200, 10, name: "Good campaign"));
            rows.Add(TestDb.Ad(d, "c2", "a2", current ? 300 : 300, 5_000, 50, 0, name: "Leaky campaign"));
        }
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "google_ads", "1234567890"), "date", today.AddDays(-14), today, rows);
        t.Facts.ReplaceWindow("ga4_daily", new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" }, "date", today.AddDays(-14), today,
            Enumerable.Range(1, 14).Select(i => new Ga4DailyRow(today.AddDays(-i), 100, 20, 300, 900, 150, 6000, 10, 10, 0, 0, "PKR")).ToArray());
        t.Syncs.FinishRun(t.Syncs.StartRun(p.Id, Integrations.GoogleAds, "1234567890", "incremental", "t", null, null), "success", 1, today, today.AddDays(-30), null, [], 1);
        t.Syncs.FinishRun(t.Syncs.StartRun(p.Id, Integrations.Ga4, "123456789", "incremental", "t", null, null), "success", 1, today.AddDays(-1), today.AddDays(-30), null, [], 1);
        extra?.Invoke(t, p, today);
        var engine = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        return (t, engine, p);
    }

    private static Filters Last7() { var today = TimeZones.Today("UTC"); return new Filters(today.AddDays(-7), today.AddDays(-1)); }

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Meeza.Marketing.Shared.Utilities.Json.Output);

    [Fact]
    public void Overview_kpis_are_computed_from_stored_data_with_previous_period_and_targets()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        t.Projects.UpsertTarget(p.Id, "cpi", "all", null, 35, null);
        var ov = J(e.Overview(p.Id, Last7()));
        var k = ov.GetProperty("kpis").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!);
        Assert.Equal(4900m, k["spend"].GetProperty("value").GetDecimal());          // (400 + 300) × 7
        Assert.Equal(3500m, k["spend"].GetProperty("previous").GetDecimal());       // (200 + 300) × 7
        Assert.Equal(70m, k["cpi"].GetProperty("value").GetDecimal());              // 4900 / 70 installs
        Assert.Equal(50m, k["cpi"].GetProperty("previous").GetDecimal());
        Assert.Equal(50m, k["cpi"].GetProperty("achievementPct").GetDecimal());     // lower is better: 35 / 70
        Assert.Equal(40m, k["cpi"].GetProperty("changePct").GetDecimal());
        Assert.Equal(100m, k["dau"].GetProperty("value").GetDecimal());
        Assert.Equal(300m, k["wau"].GetProperty("value").GetDecimal());
        Assert.Equal(900m, k["mau"].GetProperty("value").GetDecimal());
        Assert.Equal(0.1m, k["arpdau"].GetProperty("value").GetDecimal());          // 70 / 700
        Assert.Equal(1.4m, k["roas"].GetProperty("value").GetDecimal()); // 70 / 4900 = 1.43%, shown to 1 decimal
        Assert.Equal("unavailable", k["reach"].GetProperty("status").GetString());
        Assert.Equal("unavailable", k["d1"].GetProperty("status").GetString());     // no cohorts seeded
        Assert.Equal("fresh", k["spend"].GetProperty("freshness").GetString());
    }

    [Fact]
    public void Roas_is_unavailable_when_revenue_and_spend_currencies_differ()
    {
        var (t, e, p) = Seed((t, p, today) => t.Facts.ReplaceWindow("ga4_daily", new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" }, "date",
            today.AddDays(-14), today, Enumerable.Range(1, 14).Select(i => new Ga4DailyRow(today.AddDays(-i), 100, 20, 300, 900, 150, 6000, 10, 10, 0, 0, "USD")).ToArray()));
        using var _ = t;
        var k = J(e.Overview(p.Id, Last7())).GetProperty("kpis").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "roas");
        Assert.Equal(JsonValueKind.Null, k.GetProperty("value").ValueKind);
        Assert.Contains("never converted", k.GetProperty("note").GetString());
    }

    [Fact]
    public void Other_currencies_are_excluded_from_totals_and_reported()
    {
        var (t, e, p) = Seed((t, p, today) => t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "meta", "act_1"), "date", today.AddDays(-3), today.AddDays(-3),
            [TestDb.Ad(today.AddDays(-3), "m", "m", 999, 100, 1, 1, currency: "USD")]));
        using var _ = t;
        var ov = J(e.Overview(p.Id, Last7()));
        Assert.Equal("PKR", ov.GetProperty("currency").GetString());
        Assert.Equal(4900m, ov.GetProperty("kpis").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "spend").GetProperty("value").GetDecimal());
        Assert.Contains(ov.GetProperty("notes").EnumerateArray(), n => n.GetString()!.Contains("USD"));
    }

    [Fact]
    public void Campaign_flags_find_wasted_spend()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        var rows = J(e.Campaigns(p.Id, Last7(), "campaign")).GetProperty("rows").EnumerateArray().ToArray();
        var leaky = rows.Single(r => r.GetProperty("campaignId").GetString() == "c2");
        Assert.Contains(leaky.GetProperty("flags").EnumerateArray(), f => f.GetString() == "wasted_spend");
        var good = rows.Single(r => r.GetProperty("campaignId").GetString() == "c1");
        Assert.DoesNotContain(good.GetProperty("flags").EnumerateArray(), f => f.GetString() == "wasted_spend");
    }

    [Fact]
    public void Findings_cite_evidence_and_decompose_a_cpi_increase()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        var findings = e.Findings(p.Id, Last7());
        var cpi = findings.Single(f => f.Id == "cpi-up");
        Assert.Contains("CPM", cpi.Hypothesis); // spend doubled at equal impressions -> CPM drove CPI
        Assert.NotEmpty(cpi.Evidence);
        Assert.NotEmpty(cpi.Validation);
        Assert.False(string.IsNullOrWhiteSpace(cpi.Action));
        Assert.Contains(findings, f => f.Id == "wasted-spend");
        Assert.All(findings, f => Assert.Contains(f.Confidence, new[] { "high", "medium", "low" }));
    }

    // ---- strategy (diagnosis & marketing plan)

    private static void SeedCohorts(TestDb t, Project p, DateOnly today, int cohorts, long users, long d1Active, long d7Active)
    {
        var rows = new List<Ga4CohortRow>();
        for (var i = 0; i < cohorts; i++)
        {
            var date = today.AddDays(-20 + i);
            for (var n = 0; n <= 7; n++)
                rows.Add(new Ga4CohortRow("all", "all", date, n, n == 0 ? users : n == 1 ? d1Active : n == 7 ? d7Active : d7Active + 1, users));
        }
        t.Facts.ReplaceWindow("ga4_cohorts", new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" }, "cohort_date",
            today.AddDays(-20), today, rows, extraWhere: "segment_type=@seg", extraArgs: new { seg = "all" });
    }

    private static Filters Last21() { var today = TimeZones.Today("UTC"); return new Filters(today.AddDays(-21), today.AddDays(-1)); }

    [Fact]
    public void Strategy_with_low_mature_retention_says_improve_the_game_and_cites_benchmarks()
    {
        var (t, e, p) = Seed((t, p, today) => SeedCohorts(t, p, today, 10, 50, 10, 1)); // D1 20%, D7 2%, 500 users
        using var _ = t;
        var plan = e.Strategy(p.Id, Last21());
        Assert.Equal("improve", plan.Stage);
        var d1 = plan.Health.Single(h => h.Key == "d1");
        Assert.Equal("bad", d1.Status);
        Assert.Contains("27", d1.Reference);
        Assert.Contains("adjust-retention-2026", d1.ReferenceSource);
        Assert.Contains(plan.Moves, m => m.Area == "Game & onboarding");
        Assert.Equal(Enumerable.Range(1, plan.Moves.Count), plan.Moves.Select(m => m.Priority));
        Assert.Contains(plan.Sources, s => s.Id == "adjust-retention-2026" && s.Url is not null);
        J(plan); // serializable for the API
    }

    [Fact]
    public void Strategy_prefers_the_projects_own_target_over_benchmarks()
    {
        var (t, e, p) = Seed((t, p, today) => SeedCohorts(t, p, today, 10, 50, 10, 4)); // D1 20%, D7 8%
        using var _ = t;
        t.Projects.UpsertTarget(p.Id, "d1", "all", null, 18, null);
        var d1 = e.Strategy(p.Id, Last21()).Health.Single(h => h.Key == "d1");
        Assert.Equal("good", d1.Status);
        Assert.Equal("target 18%", d1.Reference);
        Assert.Null(d1.ReferenceSource);
    }

    [Fact]
    public void Strategy_with_a_small_sample_asks_for_a_validation_test_not_a_verdict()
    {
        var (t, e, p) = Seed((t, p, today) => SeedCohorts(t, p, today, 3, 10, 1, 0)); // 30 users
        using var _ = t;
        var plan = e.Strategy(p.Id, Last21());
        Assert.Equal("validate", plan.Stage);
        Assert.Equal("unknown", plan.Health.Single(h => h.Key == "d1").Status);
        Assert.Contains(plan.Moves, m => m.Area == "Testing");
        Assert.Contains(plan.Caveats, c => c.Contains("directional"));
    }

    [Fact]
    public void Strategy_puts_measurement_first_when_a_source_never_synced()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        t.Projects.SetConnections(p.Id, [new ProjectConnection(Integrations.GoogleAds, "9999999999", null, null, null), TestDb.Ga4Conn]);
        var plan = e.Strategy(p.Id, Last7());
        Assert.Equal("measure", plan.Stage);
        Assert.Equal("Measurement", plan.Moves[0].Area);
        Assert.Equal("bad", plan.Health.Single(h => h.Key == "data").Status);
    }

    [Fact]
    public void Strategy_never_compares_a_non_usd_cpi_with_usd_benchmarks()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        var cpi = e.Strategy(p.Id, Last7()).Health.Single(h => h.Key == "cpi");
        Assert.Null(cpi.Reference);
        Assert.Contains("never converted", cpi.Note);
        Assert.Equal("bad", cpi.Status); // CPI doubled vs the previous period in the seed
    }

    [Fact]
    public void Findings_flag_data_that_never_synced()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        t.Projects.SetConnections(p.Id, [TestDb.GoogleConn, TestDb.Ga4Conn, TestDb.MetaConn]);
        var f = e.Findings(p.Id, Last7());
        Assert.Equal("data_quality", f.First().Category);
    }

    [Fact]
    public void Retention_view_marks_rolling_and_creative_as_unavailable_rather_than_guessing()
    {
        var (t, e, p) = Seed();
        using var _ = t;
        Assert.False(J(e.Retention(p.Id, Last7(), "all", null, "rolling")).GetProperty("available").GetBoolean());
        Assert.False(J(e.Retention(p.Id, Last7(), "creative", null, "exact")).GetProperty("available").GetBoolean());
        Assert.Throws<ArgumentException>(() => e.Retention(p.Id, Last7(), "zodiac", null, "exact"));
    }

    [Fact]
    public void Every_view_renders_for_an_empty_project()
    {
        using var t = new TestDb();
        var p = t.NewProject();
        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var f = Last7();
        J(e.Overview(p.Id, f)); J(e.Campaigns(p.Id, f, "ad")); J(e.Retention(p.Id, f, "all", null, "exact")); J(e.Creatives(p.Id, f));
        J(e.Funnel(p.Id, f)); J(e.Attribution(p.Id, f)); J(e.Forecast(p.Id, f, null)); J(e.Findings(p.Id, f));
    }

    [Fact]
    public void Channel_classification_keeps_direct_unattributed()
    {
        Assert.Equal("paid", AnalyticsEngine.ChannelKind("Paid Search"));
        Assert.Equal("paid", AnalyticsEngine.ChannelKind("Cross-network"));
        Assert.Equal("organic", AnalyticsEngine.ChannelKind("Organic Search"));
        Assert.Equal("unattributed", AnalyticsEngine.ChannelKind("Direct"));
        Assert.Equal("unattributed", AnalyticsEngine.ChannelKind("Unassigned"));
    }
}

// ======================================================================== creatives & forecasting

public class CreativeIntelTests
{
    private static CreativeItem Item(string key, Dictionary<DateOnly, Perf> daily, string format = "video", decimal? freq = null) =>
        new(key, "meta", key, key, format, "c", "C", "ACTIVE", null, null, null, null, null, null, daily, freq);

    private static Dictionary<DateOnly, Perf> Days(int n, Func<int, Perf> f) =>
        Enumerable.Range(0, n).ToDictionary(i => new DateOnly(2026, 11, 1).AddDays(i), f);

    [Fact]
    public void Creatives_are_never_ranked_on_ctr_alone()
    {
        var clickbait = Item("clickbait", Days(5, _ => new Perf { Spend = 100, Impressions = 1000, Clicks = 100, Installs = 1 })); // CTR 10%, CPI 100
        var efficient = Item("efficient", Days(5, _ => new Perf { Spend = 100, Impressions = 1000, Clicks = 15, Installs = 10 })); // CTR 1.5%, CPI 10
        var median = Item("median", Days(5, _ => new Perf { Spend = 100, Impressions = 1000, Clicks = 30, Installs = 4 }));
        var scored = CreativeIntel.Score([clickbait, efficient, median], new DateOnly(2026, 11, 10));
        Assert.Equal("efficient", scored[0].Item.Key);
        Assert.True(scored.Single(s => s.Item.Key == "clickbait").Score < scored.Single(s => s.Item.Key == "efficient").Score);
        Assert.NotEqual("best", scored.Single(s => s.Item.Key == "clickbait").Classification);
    }

    [Fact]
    public void Low_volume_creatives_are_testing_not_judged()
    {
        var tiny = Item("tiny", Days(2, _ => new Perf { Spend = 5, Impressions = 100, Clicks = 5, Installs = 1 }));
        Assert.Equal("testing", CreativeIntel.Score([tiny], default)[0].Classification);
    }

    [Fact]
    public void Fatigue_is_detected_from_falling_ctr_and_ipm()
    {
        var fatigued = Item("f", Days(10, i => i < 5
            ? new Perf { Spend = 50, Impressions = 1000, Clicks = 30, Installs = 6 }
            : new Perf { Spend = 50, Impressions = 1000, Clicks = 12, Installs = 2 }));
        var (isFatigued, evidence) = CreativeIntel.Fatigue(fatigued);
        Assert.True(isFatigued);
        Assert.Contains("CTR", evidence);
        var steady = Item("s", Days(10, _ => new Perf { Spend = 50, Impressions = 1000, Clicks = 30, Installs = 6 }));
        Assert.False(CreativeIntel.Fatigue(steady).Fatigued);
    }

    [Theory]
    [InlineData("Can You Do It? Clear It in 14 Moves", "question / challenge")]
    [InlineData("14 moves to clear the board", "number / specific goal")]
    [InlineData("Download Gapsy free", "call to action")]
    [InlineData("Slide. Match. Solve.", "statement")]
    public void Hook_patterns(string hook, string pattern) => Assert.Equal(pattern, CreativeIntel.HookPattern(hook));
}

public class ForecastingTests
{
    [Fact]
    public void Recovers_diminishing_returns_from_history()
    {
        // installs = 2 × spend^0.7 exactly
        var days = Enumerable.Range(0, 14).Select(i =>
        {
            var spend = 100m + i * 50;
            return (new DateOnly(2026, 11, 1).AddDays(i), spend, (decimal?)(decimal)(2 * Math.Pow((double)spend, 0.7)));
        }).ToArray();
        var m = Forecasting.Build(days);
        Assert.Equal("power_curve", m.Type);
        Assert.Equal(0.7, m.B, 3);
        Assert.True(m.R2 > 0.999);
        var at = m.InstallsAt(400)!.Value;
        Assert.Equal(2 * Math.Pow(400, 0.7), (double)at, 1);
        Assert.Equal(14, m.CpiForecast(new DateOnly(2026, 11, 15), 14).Count);
    }

    [Fact]
    public void Falls_back_to_constant_cpi_and_says_so()
    {
        var m = Forecasting.Build([(new DateOnly(2026, 11, 1), 100m, 5m), (new DateOnly(2026, 11, 2), 200m, 10m)]);
        Assert.Equal("constant_cpi", m.Type);
        Assert.Equal(25m, Math.Round(m.InstallsAt(500)!.Value, 6));
        Assert.Null(Forecasting.Build([(new DateOnly(2026, 11, 1), 100m, null)]).InstallsAt(100));
    }
}

// ======================================================================== platform mapping

public class MetaSyncMappingTests
{
    [Fact]
    public void Ad_row_maps_installs_video_and_value_and_keeps_missing_installs_null()
    {
        var json = JsonDocument.Parse("""
            {"date_start":"2026-11-05","campaign_id":"1","campaign_name":"C","adset_id":"2","adset_name":"S","ad_id":"3","ad_name":"A","objective":"APP_INSTALLS",
             "account_currency":"PKR","spend":"120.5","impressions":"4000","reach":"3000","clicks":"80",
             "actions":[{"action_type":"mobile_app_install","value":"6"}],
             "action_values":[{"action_type":"app_custom_event.fb_mobile_purchase","value":"30"}],
             "video_play_actions":[{"action_type":"video_view","value":"900"}],
             "video_thruplay_watched_actions":[{"action_type":"video_view","value":"200"}]}
            """).RootElement;
        var result = new WindowResult();
        var ads = new Dictionary<string, JsonElement> { ["3"] = JsonDocument.Parse("""{"id":"3","effective_status":"ACTIVE"}""").RootElement };
        var row = MetaSyncer.ToAdRow(json, new AccountInfo("act_1", null, "PKR", "UTC"), ads, result);
        Assert.Equal(new DateOnly(2026, 11, 5), row.Date);
        Assert.Equal(6m, row.Installs);
        Assert.Equal(30m, row.ConversionValue);
        Assert.Equal(900, row.VideoViews);
        Assert.Equal(200, row.Thruplays);
        Assert.Equal("ACTIVE", row.AdStatus);
        Assert.Equal(new DateOnly(2026, 11, 5), result.LatestActivityDate);

        var noInstalls = JsonDocument.Parse("""{"date_start":"2026-11-05","ad_id":"3","spend":"1","impressions":"10"}""").RootElement;
        Assert.Null(MetaSyncer.ToAdRow(noInstalls, new AccountInfo("act_1", null, "PKR", "UTC"), ads, new WindowResult()).Installs);
    }
}

// ======================================================================== MCP tools & API

public class ProjectToolsTests
{
    [Fact]
    public void Analysis_tools_are_read_only_and_sync_is_not_destructive()
    {
        foreach (var m in typeof(ProjectTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = m.GetCustomAttribute<McpServerToolAttribute>();
            if (attr is null) continue;
            Assert.False(attr.Destructive);
            if (attr.Name != "sync_project_data") Assert.True(attr.ReadOnly, attr.Name);
        }
    }
}

[CollectionDefinition("api", DisableParallelization = true)]
public class ApiCollection;

[Collection("api")]
public class PlatformApiTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"meeza-api-{Guid.NewGuid():N}.db");
    private readonly int _port = FreePort();
    private ServiceProvider? _sp;
    private DashboardServer? _server;
    private HttpClient _http = null!;
    private string _csrf = "";

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("MEEZA_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", _port.ToString(CultureInfo.InvariantCulture));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddMeezaMarketing();
        // Isolated, empty credential store: tests never touch real accounts.
        services.AddSingleton(new LocalSecretStore(Path.Combine(Path.GetTempPath(), $"meeza-secrets-{Guid.NewGuid():N}.json")));
        _sp = services.BuildServiceProvider();
        _server = _sp.GetRequiredService<DashboardServer>();
        Assert.True(await _server.StartAsync(default));
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        (await _http.GetAsync($"/?key={AccessKey.Value}")).EnsureSuccessStatusCode();
        _csrf = (await _http.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("csrf").GetString()!;
    }

    public async Task DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_sp is not null) await _sp.DisposeAsync();
        Environment.SetEnvironmentVariable("MEEZA_DB_PATH", null);
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) try { File.Delete(f); } catch { }
    }

    private Task<HttpResponseMessage> Post(string path, object body, bool csrf = true)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Origin", $"http://127.0.0.1:{_port}");
        if (csrf) req.Headers.Add("X-Meeza-Csrf", _csrf);
        return _http.SendAsync(req);
    }

    [Fact]
    public async Task Projects_can_be_created_viewed_and_are_isolated_through_the_api()
    {
        var a = await Post("/api/v2/projects", new { name = "Game A", genre = "puzzle", timeZone = "UTC", syncNow = false });
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        var aId = (await a.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var b = await Post("/api/v2/projects", new { name = "Game B", syncNow = false });
        var bId = (await b.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;

        var db = _sp!.GetRequiredService<FactStore>();
        var d = new DateOnly(2026, 11, 1);
        db.ReplaceWindow("ad_daily", TestDb.Scope(aId, "google_ads", "1"), "date", d, d, [TestDb.Ad(d, "c", "a", 123, 1000, 10, 3)]);

        var ovA = await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{aId}/campaigns?start=2026-11-01&end=2026-11-01");
        var ovB = await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{bId}/campaigns?start=2026-11-01&end=2026-11-01");
        Assert.Equal(123m, ovA.GetProperty("totals").GetProperty("spend").GetDecimal());
        Assert.Equal(0m, ovB.GetProperty("totals").GetProperty("spend").GetDecimal());

        foreach (var v in new[] { "overview", "retention", "campaigns", "creatives", "funnel", "attribution", "forecast", "findings", "strategy", "sync", "targets", "filters", "check", "tasks" })
            Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync($"/api/v2/projects/{aId}/{v}?preset=last_7_days")).StatusCode);

        var list = await _http.GetFromJsonAsync<JsonElement>("/api/v2/projects");
        Assert.Equal(2, list.GetProperty("projects").GetArrayLength());
    }

    [Fact]
    public async Task Writes_require_the_anti_forgery_token_and_same_origin()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Post("/api/v2/projects", new { name = "X" }, csrf: false)).StatusCode);
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v2/projects") { Content = JsonContent.Create(new { name = "X" }) };
        req.Headers.Add("Origin", "http://evil.example");
        req.Headers.Add("X-Meeza-Csrf", _csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(req)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _http.DeleteAsync("/api/v2/projects/x")).StatusCode);
    }

    [Fact]
    public async Task Errors_are_reported_with_proper_status_codes()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/v2/projects/missing/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post("/api/v2/projects", new { name = "" })).StatusCode);
        var p = await Post("/api/v2/projects", new { name = "G", syncNow = false });
        var id = (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync($"/api/v2/projects/{id}/overview?platform=tiktok")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post($"/api/v2/projects/{id}/targets", new { metric = "cpi" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/v2/projects/{id}/targets", new { metric = "cpi", target = 20 })).StatusCode);
        // AI features need a key: a clear 400, never a crash.
        var ask = await Post($"/api/v2/projects/{id}/analyst", new { question = "Why?" });
        Assert.Equal(HttpStatusCode.BadRequest, ask.StatusCode);
        Assert.Contains("Anthropic API key", await ask.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Tasks_come_only_from_the_check_and_people_can_only_change_their_status()
    {
        async Task<string> NewProject(string name, bool connected) =>
            (await (await Post("/api/v2/projects", connected
                ? new { name, timeZone = "UTC", syncNow = false, connections = new[] { new { integration = "google_ads", externalId = "1234567890" } } }
                : (object)new { name, syncNow = false })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var a = await NewProject("Game A", connected: true);
        var b = await NewProject("Game B", connected: false);

        // There is no way to add or delete a task by hand.
        Assert.False((await Post($"/api/v2/projects/{a}/tasks", new { title = "Mine" })).IsSuccessStatusCode);
        Assert.Equal(0, (await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{a}/tasks")).GetProperty("tasks").GetArrayLength());

        await Post($"/api/v2/projects/{a}/check", new { });
        var list = await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{a}/tasks");
        var task = list.GetProperty("tasks").EnumerateArray().Single();
        var tid = task.GetProperty("id").GetInt64();
        var details = task.GetProperty("details").GetString();
        Assert.Equal("analysis", task.GetProperty("source").GetString());
        Assert.False((await Post($"/api/v2/projects/{a}/tasks/{tid}/delete", new { })).IsSuccessStatusCode);
        // Only the status can change: other fields are not accepted, and the status is required.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post($"/api/v2/projects/{a}/tasks/{tid}", new { title = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post($"/api/v2/projects/{a}/tasks/{tid}", new { status = "done" }, csrf: false)).StatusCode);

        Assert.Equal(1, list.GetProperty("tasks").GetArrayLength());
        Assert.Equal(1, list.GetProperty("counts").GetProperty("active").GetInt32());
        var summaries = (await _http.GetFromJsonAsync<JsonElement>("/api/v2/projects")).GetProperty("projects").EnumerateArray()
            .ToDictionary(x => x.GetProperty("project").GetProperty("id").GetString()!, x => x.GetProperty("openTasks").GetInt32());
        Assert.Equal((1, 0), (summaries[a], summaries[b]));

        // Another project can neither see nor change it.
        Assert.Equal(0, (await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{b}/tasks")).GetProperty("tasks").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Post($"/api/v2/projects/{b}/tasks/{tid}", new { status = "done" })).StatusCode);

        // A status change leaves the rest of the task alone.
        var done = await (await Post($"/api/v2/projects/{a}/tasks/{tid}", new { status = "done" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("done", details, "user"), (done.GetProperty("status").GetString(), done.GetProperty("details").GetString(), done.GetProperty("closedBy").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post($"/api/v2/projects/{a}/tasks/{tid}", new { status = "archived" })).StatusCode);
        Assert.Equal(1, (await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{a}/tasks")).GetProperty("tasks").GetArrayLength());
    }

    [Fact]
    public async Task The_30_day_check_runs_on_demand_and_at_the_end_of_every_sync()
    {
        async Task<string> NewProject(string name) =>
            (await (await Post("/api/v2/projects", new { name, timeZone = "UTC", syncNow = false, connections = new[] { new { integration = "google_ads", externalId = "1234567890" } } }))
                .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        static string? Id(JsonElement problem) => problem.GetProperty("finding").GetProperty("id").GetString();
        var id = await NewProject("Checked");

        var before = await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/check");
        Assert.Equal(JsonValueKind.Null, before.GetProperty("analyzedAt").ValueKind);
        Assert.Equal((30, 1), (before.GetProperty("windowDays").GetInt32(), before.GetProperty("sources").GetInt32()));

        // On demand: the never-synced source is a problem, and the problem is a task.
        var ran = await Post($"/api/v2/projects/{id}/check", new { });
        Assert.Equal(HttpStatusCode.OK, ran.StatusCode);
        var check = await ran.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual", check.GetProperty("trigger").GetString());
        var problem = check.GetProperty("problems").EnumerateArray().Single();
        Assert.Equal("data-google_ads-1234567890", Id(problem));
        Assert.Equal("open", problem.GetProperty("task").GetProperty("status").GetString());
        var tid = problem.GetProperty("task").GetProperty("id").GetInt64();

        var task = (await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/tasks")).GetProperty("tasks").EnumerateArray().Single();
        Assert.Equal(("analysis", "high", true), (task.GetProperty("source").GetString(), task.GetProperty("priority").GetString(), task.GetProperty("detected").GetBoolean()));
        Assert.NotEmpty(task.GetProperty("finding").GetProperty("evidence").EnumerateArray());

        // A dismissed task stays dismissed while the problem lasts, and no duplicate appears.
        Assert.Equal(HttpStatusCode.OK, (await Post($"/api/v2/projects/{id}/tasks/{tid}", new { status = "dismissed" })).StatusCode);
        var again = await (await Post($"/api/v2/projects/{id}/check", new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("dismissed", again.GetProperty("problems").EnumerateArray().Single().GetProperty("task").GetProperty("status").GetString());
        Assert.Equal(1, (await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/tasks")).GetProperty("tasks").GetArrayLength());

        // At the end of a sync, through the engine every host shares. With no credentials the sync fails before any
        // request is made; if this machine has real Google Ads credentials in its environment, do not use them.
        var sync = _sp!.GetRequiredService<SyncEngine>();
        if (sync.Syncer(Integrations.GoogleAds)!.IsConfigured) return;
        var synced = await NewProject("Synced");
        Assert.Equal("failed", (await sync.SyncProjectAsync(synced, "schedule", null, default)).Single().Status);
        var after = await _http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{synced}/check");
        Assert.Equal("schedule", after.GetProperty("trigger").GetString());
        Assert.Equal("data-google_ads-1234567890", Id(after.GetProperty("problems").EnumerateArray().Single()));
        Assert.Equal(1, after.GetProperty("tasks").GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task The_app_page_is_served_and_classic_redirects_to_it()
    {
        var app = await _http.GetStringAsync("/");
        Assert.Contains("Meeza Growth", app);
        Assert.DoesNotContain("/classic", app);
        var classic = await _http.GetAsync("/classic"); // HttpClient follows the redirect
        Assert.Equal(HttpStatusCode.OK, classic.StatusCode);
        Assert.Contains("Meeza Growth", await classic.Content.ReadAsStringAsync());
    }
}

public class ClaudeGatewayTests
{
    private sealed class Settings(Dictionary<string, string?> v) : ISettingsResolver
    {
        public string? Get(string key) => v.GetValueOrDefault(key);
        public string? SourceOf(string key) => v.ContainsKey(key) ? "test" : null;
    }

    [Fact]
    public void Requests_use_the_default_model_effort_and_default_refusal_fallback()
    {
        var gw = new Meeza.MarketingMcp.Ai.ClaudeGateway(new Settings(new() { [SettingKeys.AnthropicApiKey] = "sk-ant-test" }));
        Assert.True(gw.IsConfigured);
        var p = gw.Params(1000, [new Anthropic.Models.Beta.Messages.BetaUserMessageParam { Content = "hi" }], "system");
        var json = p.ToString();
        Assert.Contains("claude-opus-5-5", json);
        Assert.Matches(@"""fallbacks""\s*:\s*""default""", json);
        Assert.Contains("server-side-fallback-2026-07-01", string.Join(",", p.Betas!.Select(b => b.ToString())));
        Assert.False(new Meeza.MarketingMcp.Ai.ClaudeGateway(new Settings([])).IsConfigured);
    }
}
