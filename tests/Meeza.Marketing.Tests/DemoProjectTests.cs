using System.Globalization;
using System.Text.Json;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

/// <summary>The demo project behind `dashboard --demo`: seeded into the store, every page of the app must have data.</summary>
public class DemoProjectTests
{
    /// <summary>Views read "today" from the clock, so the demo is seeded for the real current day, as the CLI does.</summary>
    private static (TestDb T, AnalyticsEngine E, Project P, DateOnly Today) Seed()
    {
        var t = new TestDb();
        var today = TimeZones.Today(DemoProject.TimeZone);
        var p = DemoProject.Seed(t.Projects, t.Facts, t.Syncs, today);
        var engine = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        return (t, engine, p, today);
    }

    /// <summary>The range of the "last N days" preset: N complete days ending yesterday.</summary>
    private static Filters Last(DateOnly today, int days) => new(today.AddDays(-days), today.AddDays(-1));

    private static JsonElement J(object o) => JsonSerializer.SerializeToElement(o, Meeza.Marketing.Shared.Utilities.Json.Output);

    private static decimal Number(JsonElement e, string property)
    {
        Assert.Equal(JsonValueKind.Number, e.GetProperty(property).ValueKind);
        return e.GetProperty(property).GetDecimal();
    }

    /// <summary>Every row of a table as text, without the columns that differ between two seeds by design (ids and write times).</summary>
    private static List<string> Rows(TestDb t, string table)
    {
        using var c = t.Db.Open();
        return c.Query($"SELECT * FROM {table}", null, r => string.Join("|", Enumerable.Range(0, r.FieldCount)
                .Where(i => r.GetName(i) is not ("id" or "project_id" or "synced_at" or "updated_at" or "added_at"))
                .Select(i => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture))))
            .Order(StringComparer.Ordinal).ToList();
    }

    private static readonly string[] SeededTables =
        ["project_connections", "kpi_targets", "ad_daily", "campaign_country_daily", "asset_daily", "creatives", "reach_windows",
         "ga4_daily", "ga4_dim_daily", "ga4_events_daily", "ga4_cohorts"];

    [Fact]
    public void The_project_is_a_puzzle_game_on_three_fake_accounts()
    {
        var (t, _, p, _) = Seed();
        using var _1 = t;
        Assert.Equal(DemoProject.Name, p.Name);
        Assert.Equal(("puzzle", "android", "USD", "UTC"), (p.Genre, p.Platforms, p.Currency, p.TimeZone));
        Assert.Null(p.AndroidStoreUrl);
        Assert.Null(p.IosStoreUrl);
        Assert.True(p.TargetCountries!.Split(',').Length >= 3);
        Assert.Equal(("tutorial_begin", "tutorial_complete", "in_app_purchase"), (p.TutorialStartEvent, p.TutorialEvent, p.PurchaseEvent));
        Assert.True(p.NuxEvents!.Count >= 3);
        // One Google Ads account, one Meta account, one GA4 property: no Adjust, no Play.
        Assert.Equal([Integrations.Ga4, Integrations.GoogleAds, Integrations.Meta], p.Connections.Select(c => c.Integration).Order());
        Assert.Equal([DemoProject.Ga4Property, DemoProject.GoogleAdsAccount, DemoProject.MetaAccount], p.Connections.OrderBy(c => c.Integration).Select(c => c.ExternalId));
        Assert.Equal(["cpi", "d1", "d7"], t.Projects.Targets(p.Id).Select(x => x.Metric).Order());
    }

    [Fact]
    public void Overview_of_the_last_14_days_has_spend_installs_cpi_and_retention()
    {
        var (t, e, p, today) = Seed();
        using var _ = t;
        var ov = J(e.Overview(p.Id, Last(today, 14)));
        var k = ov.GetProperty("kpis").EnumerateArray().ToDictionary(x => x.GetProperty("key").GetString()!);
        foreach (var key in new[] { "spend", "installs", "cpi", "d1", "d7", "dau", "mau", "revenue", "roas", "reach" })
        {
            Assert.True(Number(k[key], "value") > 0, key);
            Assert.NotEqual("unavailable", k[key].GetProperty("status").GetString());
        }
        // The previous 14 days are there too, so every card shows a change.
        foreach (var key in new[] { "spend", "installs", "cpi", "d1" }) Assert.True(Number(k[key], "previous") > 0, key);
        Assert.InRange(Number(k["cpi"], "value"), 0.4m, 2.5m);
        foreach (var key in new[] { "cpi", "d1", "d7" }) Assert.True(Number(k[key], "target") > 0, key);
        Assert.Equal("USD", ov.GetProperty("currency").GetString());
        Assert.True(ov.GetProperty("acquisition").GetProperty("available").GetBoolean());
        Assert.Empty(ov.GetProperty("notes").EnumerateArray()); // nothing to warn about
    }

    [Fact]
    public void Campaigns_have_rows_for_both_platforms_at_every_level()
    {
        var (t, e, p, today) = Seed();
        using var _ = t;
        foreach (var (level, name) in new[] { ("campaign", "campaignName"), ("adset", "adsetName"), ("ad", "adName") })
        {
            var rows = J(e.Campaigns(p.Id, Last(today, 14), level)).GetProperty("rows").EnumerateArray().ToArray();
            foreach (var platform in new[] { Integrations.GoogleAds, Integrations.Meta })
                Assert.True(rows.Count(r => r.GetProperty("platform").GetString() == platform) >= 2, $"{level} {platform}");
            Assert.All(rows, r =>
            {
                Assert.False(string.IsNullOrWhiteSpace(r.GetProperty(name).GetString()));
                Assert.False(string.IsNullOrWhiteSpace(r.GetProperty("status").GetString()));
            });
        }

        var view = J(e.Campaigns(p.Id, Last(today, 14), "campaign"));
        var campaigns = view.GetProperty("rows").EnumerateArray().ToArray();
        // Campaigns differ in cost, and one spends without a single install.
        var cpis = campaigns.Select(r => r.GetProperty("metrics").GetProperty("cpi")).Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDecimal()).ToArray();
        Assert.All(cpis, cpi => Assert.InRange(cpi, 0.3m, 3m));
        Assert.True(cpis.Max() > cpis.Min() * 3);
        var wasted = Assert.Single(campaigns, r => r.GetProperty("flags").EnumerateArray().Any(f => f.GetString() == "wasted_spend"));
        Assert.Equal(0m, Number(wasted.GetProperty("metrics"), "installs"));
        Assert.True(Number(wasted.GetProperty("metrics"), "spend") > 0);
        // Google campaigns carry the retention of the players they brought (GA4 first-user campaign).
        Assert.Contains(campaigns, r => r.GetProperty("platform").GetString() == Integrations.GoogleAds && r.GetProperty("d1").ValueKind == JsonValueKind.Number);
        var countries = view.GetProperty("countries").GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("country").GetString()).ToArray();
        Assert.Equal(["BR", "IN", "PH", "PK", "US"], countries.Order());
    }

    [Fact]
    public void Retention_has_a_heatmap_and_a_plausible_curve_for_every_segment()
    {
        var (t, e, p, today) = Seed();
        using var _ = t;
        static decimal? On(JsonElement view, int day) =>
            view.GetProperty("curve").EnumerateArray().Single(x => x.GetProperty("day").GetInt32() == day).GetProperty("percent") is { ValueKind: JsonValueKind.Number } v ? v.GetDecimal() : null;

        var month = J(e.Retention(p.Id, Last(today, 30), "all", null, "exact"));
        Assert.True(month.GetProperty("available").GetBoolean());
        Assert.Equal(30, month.GetProperty("heatmap").GetArrayLength());
        Assert.InRange(On(month, 1)!.Value, 25m, 45m);
        Assert.InRange(On(month, 7)!.Value, 8m, 18m);
        Assert.NotEmpty(month.GetProperty("newVsReturning").EnumerateArray());

        // The whole history holds cohorts old enough for day 30, and the curve falls with every milestone.
        var all = J(e.Retention(p.Id, Last(today, DemoProject.Days), "all", null, "exact"));
        Assert.InRange(On(all, 30)!.Value, 2m, 7m);
        Assert.True(On(all, 1) > On(all, 7) && On(all, 7) > On(all, 14) && On(all, 14) > On(all, 30));

        foreach (var segment in Ga4Syncer.CohortSegments.Keys.Where(s => s != "all"))
        {
            var view = J(e.Retention(p.Id, Last(today, 30), segment, null, "exact"));
            Assert.True(view.GetProperty("segments").GetArrayLength() > 0, segment);
            Assert.True(On(view, 1) > 0, segment);
        }
    }

    [Fact]
    public void Creatives_funnel_attribution_forecast_findings_and_strategy_all_have_content()
    {
        var (t, e, p, today) = Seed();
        using var _ = t;
        var f = Last(today, 14);

        var creatives = J(e.Creatives(p.Id, f)).GetProperty("creatives").EnumerateArray().ToArray();
        foreach (var platform in new[] { Integrations.GoogleAds, Integrations.Meta })
            Assert.Contains(creatives, c => c.GetProperty("platform").GetString() == platform && c.GetProperty("score").ValueKind == JsonValueKind.Number);
        Assert.Contains(creatives, c => c.GetProperty("fatigued").GetBoolean());
        Assert.Contains(creatives, c => c.GetProperty("classification").GetString() == "best");
        // Nothing for the browser to load from outside.
        Assert.All(creatives, c => Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (c.GetProperty("thumbnailUrl").ValueKind, c.GetProperty("previewUrl").ValueKind)));

        var funnel = J(e.Funnel(p.Id, f));
        var steps = funnel.GetProperty("steps").EnumerateArray().ToArray();
        Assert.True(Number(steps[0], "value") > 0);
        Assert.True(steps.Count(s => s.GetProperty("value").ValueKind == JsonValueKind.Number) >= 3);
        Assert.True(funnel.GetProperty("nuxConfigured").GetBoolean());
        var journey = steps.Where(s => s.GetProperty("nux").GetBoolean()).Select(s => Number(s, "value")).ToArray();
        Assert.Equal(p.NuxEvents!.Count, journey.Length);
        Assert.Equal(journey.OrderDescending(), journey); // each step keeps fewer players than the one before
        Assert.True(Number(steps.Single(s => s.GetProperty("key").GetString() == "purchasers"), "value") > 0);
        Assert.True(Number(funnel.GetProperty("revenue"), "ad") > 0 && Number(funnel.GetProperty("revenue"), "purchase") > 0);

        var attribution = J(e.Attribution(p.Id, f));
        Assert.Equal(14, attribution.GetProperty("rows").GetArrayLength());
        var totals = attribution.GetProperty("totals");
        foreach (var key in new[] { "googleAdsInstalls", "metaInstalls", "ga4FirstOpen", "ga4NewUsersGoogleAds", "ga4NewUsersMeta", "ga4NewUsersOrganic" })
            Assert.True(Number(totals, key) > 0, key);

        var forecast = J(e.Forecast(p.Id, f, null));
        Assert.Equal("power_curve", forecast.GetProperty("model").GetProperty("type").GetString());
        Assert.NotEmpty(forecast.GetProperty("cpiForecast").EnumerateArray());
        Assert.All(forecast.GetProperty("scenarios").EnumerateArray(), s => Assert.True(Number(s, "installsPerDay") > 0));
        Assert.True(Number(forecast.GetProperty("ltv"), "d30") > 0);

        var findings = e.Findings(p.Id, f);
        Assert.Contains(findings, x => x.Id == "wasted-spend");
        Assert.Contains(findings, x => x.Id == "creative-fatigue");
        Assert.DoesNotContain(findings, x => x.Category == "data_quality"); // every source is current

        var plan = e.Strategy(p.Id, f);
        Assert.NotEqual("measure", plan.Stage);
        Assert.NotEmpty(plan.Moves);
        Assert.Equal("good", plan.Health.Single(h => h.Key == "data").Status);
        Assert.DoesNotContain(plan.Health, h => h.Status == "unknown");
        J(plan); // serializable for the API
    }

    [Fact]
    public void Every_connection_was_synchronized_through_yesterday()
    {
        var (t, e, p, today) = Seed();
        using var _ = t;
        var status = e.Status(p);
        Assert.Equal(3, status.Count);
        Assert.All(status, s =>
        {
            Assert.DoesNotContain(s.Freshness, new[] { "never", "stale", "error" });
            Assert.Equal(("success", Db.Iso(today.AddDays(-1)), 0), (s.LastStatus, s.LatestDataDate, s.ConsecutiveFailures));
            Assert.NotNull(s.LastSuccessAt);
            Assert.True(s.RecordsLast > 0);
        });
        Assert.All(t.Syncs.Runs(p.Id), r => Assert.Equal("success", r.Status));
    }

    [Fact]
    public void The_data_covers_the_days_before_today_like_a_real_sync()
    {
        var (t, _, p, today) = Seed();
        using var _1 = t;
        using var c = t.Db.Open();
        var (first, last) = (Db.Iso(today.AddDays(-DemoProject.Days)), Db.Iso(today.AddDays(-1)));
        foreach (var table in new[] { "ad_daily", "campaign_country_daily", "asset_daily", "ga4_daily", "ga4_dim_daily", "ga4_events_daily" })
            Assert.Equal((first, last), (c.Scalar<string>($"SELECT MIN(date) FROM {table}"), c.Scalar<string>($"SELECT MAX(date) FROM {table}")));
        Assert.Equal(DemoProject.Days, c.Scalar<long>("SELECT COUNT(*) FROM ga4_daily"));
        foreach (var platform in new[] { Integrations.GoogleAds, Integrations.Meta })
        {
            Assert.True(c.Scalar<long>("SELECT COUNT(*) FROM ad_daily WHERE platform=@platform", new { platform }) > 0, platform);
            Assert.True(c.Scalar<long>("SELECT COUNT(*) FROM creatives WHERE platform=@platform", new { platform }) > 0, platform);
        }

        // GA4: every dimension and cohort segment a real sync stores, and the events the funnel reads.
        Assert.Equal(Ga4Syncer.Dimensions.Keys.Order(), c.Query("SELECT DISTINCT dimension FROM ga4_dim_daily", null, r => r.GetString(0)).Order());
        Assert.Equal(Ga4Syncer.CohortSegments.Keys.Order(), c.Query("SELECT DISTINCT segment_type FROM ga4_cohorts", null, r => r.GetString(0)).Order());
        var events = c.Query("SELECT DISTINCT event_name FROM ga4_events_daily", null, r => r.GetString(0));
        Assert.Subset(events.ToHashSet(), p.NuxEvents!.Select(s => s.Event).Concat(["first_open", p.TutorialStartEvent!, p.TutorialEvent!, p.PurchaseEvent!, "ad_impression"]).ToHashSet());

        // A cohort has no cell for a day it has not reached yet, and never more players back than it started with.
        Assert.Equal(last, c.Scalar<string>("SELECT MAX(date(cohort_date, '+' || day_n || ' day')) FROM ga4_cohorts"));
        Assert.Equal(0, c.Scalar<long>("SELECT COUNT(*) FROM ga4_cohorts WHERE active_users > cohort_users OR day_n > @max", new { max = Ga4Syncer.MaxCohortDay }));
        Assert.Contains(t.Data.Cohorts(p.Id, "all", today.AddDays(-DemoProject.Days), today), x => x.DayN == 30);
    }

    [Fact]
    public void Seeding_is_deterministic()
    {
        var day = new DateOnly(2026, 3, 10);
        using var a = new TestDb();
        using var b = new TestDb();
        var pa = DemoProject.Seed(a.Projects, a.Facts, a.Syncs, day);
        var pb = DemoProject.Seed(b.Projects, b.Facts, b.Syncs, day);
        foreach (var table in SeededTables)
        {
            var rows = Rows(a, table);
            Assert.NotEmpty(rows);
            Assert.Equal(rows, Rows(b, table));
        }

        // The same totals on the page, too.
        string Totals(TestDb t, Project p)
        {
            var engine = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
            return string.Join(";", J(engine.Overview(p.Id, Last(day, 14))).GetProperty("kpis").EnumerateArray()
                .Select(k => $"{k.GetProperty("key")}={k.GetProperty("value")}/{k.GetProperty("previous")}"));
        }
        Assert.Equal(Totals(a, pa), Totals(b, pb));
        Assert.Contains("spend=", Totals(a, pa));

        // Another day gives other numbers: the data follows the calendar, it is not one fixed picture.
        using var c = new TestDb();
        DemoProject.Seed(c.Projects, c.Facts, c.Syncs, day.AddDays(1));
        Assert.NotEqual(Rows(a, "ad_daily"), Rows(c, "ad_daily"));
    }

    [Fact]
    public void Seeding_again_replaces_the_earlier_demo_and_leaves_other_projects_alone()
    {
        using var t = new TestDb();
        var mine = t.NewProject("My game", conns: TestDb.GoogleConn);
        var d = new DateOnly(2026, 3, 1);
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(mine.Id, "google_ads", "1234567890"), "date", d, d, [TestDb.Ad(d, "c1", "ad1", 100, 1000, 10, 5)]);

        var first = DemoProject.Seed(t.Projects, t.Facts, t.Syncs, new DateOnly(2026, 3, 10));
        var second = DemoProject.Seed(t.Projects, t.Facts, t.Syncs, new DateOnly(2026, 3, 12));

        Assert.Equal(["My game", DemoProject.Name], t.Projects.List(includeArchived: true).Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Null(t.Projects.Get(first.Id));
        using var c = t.Db.Open();
        Assert.Equal(0, c.Scalar<long>("SELECT COUNT(*) FROM ad_daily WHERE project_id=@id", new { id = first.Id }));
        Assert.Equal("2026-03-11", c.Scalar<string>("SELECT MAX(date) FROM ad_daily WHERE project_id=@id", new { id = second.Id }));
        Assert.Equal(100m, t.Data.Ads(mine.Id, new Filters(d, d)).Single().Spend);
    }

    [Fact]
    public void No_stored_text_contains_a_url()
    {
        var (t, _, _, _) = Seed();
        using var _1 = t;
        using var c = t.Db.Open();
        var texts = 0;
        foreach (var table in c.Query("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'", null, r => r.GetString(0)))
            foreach (var row in c.Query($"SELECT * FROM {table}", null, r => Enumerable.Range(0, r.FieldCount).Select(i => (Column: r.GetName(i), Value: r.GetValue(i) as string)).ToArray()))
                foreach (var (column, value) in row.Where(x => x.Value is not null))
                {
                    texts++;
                    Assert.False(value!.Contains("http", StringComparison.OrdinalIgnoreCase), $"{table}.{column}: {value}");
                }
        Assert.True(texts > 10_000); // the scan really read the stored rows
    }
}
