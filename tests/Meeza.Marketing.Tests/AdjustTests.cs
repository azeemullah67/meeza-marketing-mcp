using System.Net;
using System.Text.Json;
using Meeza.Marketing.Adjust;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Services;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

public class AdjustParsingTests
{
    [Theory]
    [InlineData(" abc123def456 ", "abc123def456")]
    [InlineData("ABCdef123", "ABCdef123")]
    public void App_tokens_are_trimmed(string input, string expected) => Assert.Equal(expected, AdjustOptions.NormalizeAppToken(input));

    [Theory]
    [InlineData("abc")]
    [InlineData("abc-123-def")]
    [InlineData("abc 123 def")]
    public void Bad_app_tokens_are_rejected(string input) => Assert.Throws<ArgumentException>(() => AdjustOptions.NormalizeAppToken(input));

    [Fact]
    public void Settings_normalise_app_tokens_and_currency()
    {
        Assert.Equal("abc123def456,zzz999yyy888", ConnectionSettings.Normalize(SettingKeys.AdjustAppTokens, "abc123def456, zzz999yyy888"));
        Assert.Equal("EUR", ConnectionSettings.Normalize(SettingKeys.AdjustCurrency, " eur "));
        Assert.ThrowsAny<ArgumentException>(() => ConnectionSettings.Normalize(SettingKeys.AdjustCurrency, "euro"));
        Assert.ThrowsAny<ArgumentException>(() => ConnectionSettings.Normalize(SettingKeys.AdjustApiToken, "two words"));
        Assert.True(SettingKeys.IsSecret(SettingKeys.AdjustApiToken));
        Assert.False(SettingKeys.IsSecret(SettingKeys.AdjustAppTokens));
    }

    [Fact]
    public void Report_rows_keep_dimensions_and_parse_string_metrics()
    {
        using var doc = JsonDocument.Parse("""
            {"rows":[{"attr_dependency":{"partner":"Organic","partner_id":"-300"},"day":"2026-11-01","partner_name":"Organic","installs":"12","cost":"0.0"},
                     {"day":"2026-11-01","partner_name":"Facebook Installs","installs":"30","cost":"45.5","clicks":null}],
             "totals":{"installs":42},"warnings":["Cost data for some networks is delayed"],"pagination":null}
            """);
        var rep = AdjustClient.ParseReport(doc.RootElement);
        Assert.Equal(2, rep.Rows.Count);
        Assert.False(rep.Rows[0].ContainsKey("attr_dependency"));
        Assert.Equal("Organic", AdjustReport.Text(rep.Rows[0], "partner_name"));
        Assert.Equal(12, AdjustReport.Count(rep.Rows[0], "installs"));
        Assert.Equal(45.5m, AdjustReport.Number(rep.Rows[1], "cost"));
        Assert.Null(AdjustReport.Number(rep.Rows[1], "clicks"));        // null stays unavailable
        Assert.Null(AdjustReport.Number(rep.Rows[1], "impressions"));   // not requested
        Assert.Equal(["Cost data for some networks is delayed"], rep.Warnings);
    }

    [Fact]
    public void Apps_are_read_from_filters_data()
    {
        using var doc = JsonDocument.Parse("""{"apps":[{"id":"abc123def456","name":"Gapsy","short_name":""},{"id":"","name":"broken"},{"id":"zzz999yyy888","name":""}]}""");
        var apps = AdjustClient.ParseApps(doc.RootElement);
        Assert.Equal([new AdjustApp("abc123def456", "Gapsy"), new AdjustApp("zzz999yyy888", null)], apps);
    }

    [Fact]
    public void Errors_explain_the_fix_and_never_echo_the_token()
    {
        var msg = AdjustClient.DescribeError(401, """{"detail":"Invalid token Bearer abcdefghijklmnop123"}""");
        Assert.Contains("My profile", msg);
        Assert.DoesNotContain("abcdefghijklmnop123", msg);
        Assert.Contains("permission", AdjustClient.DescribeError(403, "not json"));
    }

    [Theory]
    [InlineData("Summer UA", "120210000000001", "Summer UA (120210000000001)")]
    [InlineData("Summer UA (120210000000001)", "120210000000001", "Summer UA (120210000000001)")]
    [InlineData("Organic", "unknown", "Organic")]
    [InlineData(null, null, "(unknown)")]
    [InlineData(null, "555", "555")]
    public void Campaign_labels_carry_the_network_id(string? name, string? id, string expected) => Assert.Equal(expected, AdjustService.WithId(name, id));

    [Theory]
    [InlineData("Summer UA (120210000000001)", "120210000000001")]
    [InlineData("Organic", null)]
    [InlineData("Odd ()", null)]
    public void Campaign_ids_are_read_back_from_labels(string label, string? id) => Assert.Equal(id, AdjustService.IdOf(label));

    [Fact]
    public void Cohorts_store_only_days_the_cohort_has_reached()
    {
        var last = new DateOnly(2026, 11, 9);
        var rep = new AdjustReport(
        [
            Row(("day", "2026-11-07"), ("installs", "100"), ("retained_users_d0", "100"), ("retained_users_d1", "35"), ("retained_users_d2", "20"), ("retained_users_d3", "0")),
            Row(("day", "2026-11-09"), ("installs", "50"), ("retained_users_d0", "50"), ("retained_users_d1", "0")),
            Row(("day", "2026-11-08"), ("installs", "0"), ("retained_users_d0", "0")),
        ], []);
        var cells = AdjustSyncer.MapCohorts(rep, "all", last);
        // 11-07 reached day 2 (11-09); its day 3 "0" is immature and must not be stored as 0% retention.
        Assert.Equal([0, 1, 2], cells.Where(c => c.CohortDate == new DateOnly(2026, 11, 7)).Select(c => c.DayN).Order().ToArray());
        Assert.Equal([0], cells.Where(c => c.CohortDate == new DateOnly(2026, 11, 9)).Select(c => c.DayN).ToArray());
        Assert.DoesNotContain(cells, c => c.CohortDate == new DateOnly(2026, 11, 8)); // no installs: no cohort
        Assert.All(cells.Where(c => c.CohortDate == new DateOnly(2026, 11, 7)), c => Assert.Equal(100, c.CohortUsers));
    }

    [Fact]
    public void Campaign_rows_normalise_labels_and_merge_duplicates()
    {
        var rep = new AdjustReport(
        [
            Row(("day", "2026-11-01"), ("partner_name", "Organic"), ("campaign_id_network", "unknown"), ("campaign_network", "unknown"), ("country_code", "ph"), ("os_name", "Android"), ("installs", "5"), ("cost", "0")),
            Row(("day", "2026-11-01"), ("partner_name", "Organic"), ("campaign_id_network", "unknown"), ("campaign_network", "unknown"), ("country_code", "PH"), ("os_name", "android"), ("installs", "7")),
            Row(("day", "2026-11-01"), ("partner_name", "Facebook Installs"), ("campaign_id_network", "1202"), ("campaign_network", "UA"), ("country_code", "pk"), ("os_name", "android"), ("installs", "9"), ("cost", "12.5")),
        ], []);
        var rows = AdjustSyncer.MapCampaigns(rep, "USD");
        Assert.Equal(2, rows.Count);
        var organic = rows.Single(r => r.Network == "Organic");
        Assert.Equal("", organic.CampaignId);
        Assert.Equal("PH", organic.Country);
        Assert.Equal(12, organic.Installs);
        Assert.Equal(0m, organic.Cost); // one row measured 0 cost, the other did not report it
        Assert.Equal("1202", rows.Single(r => r.Network != "Organic").CampaignId);
    }

    internal static IReadOnlyDictionary<string, string?> Row(params (string Key, string? Value)[] cells) =>
        cells.ToDictionary(c => c.Key, c => c.Value);
}

public class AdjustClientTests
{
    private static (AdjustService Service, FakeHttp Http) Service(Func<HttpRequestMessage, HttpResponseMessage> respond, params string[] allow)
    {
        var http = new FakeHttp(respond);
        var client = new AdjustClient(http.Client, new AdjustOptions { ApiToken = "adj-token-123", AppTokens = allow, Currency = "EUR" });
        return (new AdjustService(client), http);
    }

    [Fact]
    public async Task Reports_are_get_requests_to_adjust_with_bearer_token_and_fixed_parameters()
    {
        var (svc, http) = Service(_ => FakeHttp.Json("""{"rows":[],"totals":{},"warnings":[]}"""));
        await svc.RunReportAsync("abc123def456", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 7), ["day", "partner_name"], ["installs", "cost"], default);
        var req = Assert.Single(http.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("automate.adjust.com", req.RequestUri!.Host);
        Assert.Equal("/reports-service/report", req.RequestUri.AbsolutePath);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("adj-token-123", req.Headers.Authorization.Parameter);
        var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
        Assert.Equal("abc123def456", q["app_token__in"]);
        Assert.Equal("2026-11-01:2026-11-07", q["date_period"]);
        Assert.Equal("day,partner_name", q["dimensions"]);
        Assert.Equal("installs,cost", q["metrics"]);
        Assert.Equal("EUR", q["currency"]);
        Assert.Equal("+00:00", q["utc_offset"]);
        Assert.Equal("first", q["attribution_source"]);
        Assert.Equal("immature", q["cohort_maturity"]);
    }

    [Fact]
    public async Task Apps_outside_the_allow_list_are_refused_before_any_request()
    {
        var (svc, http) = Service(_ => FakeHttp.Json("{}"), "abc123def456");
        await Assert.ThrowsAsync<AccountAccessDeniedException>(() =>
            svc.RunReportAsync("zzz999yyy888", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), ["day"], ["installs"], default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task No_content_means_an_empty_report()
    {
        var (svc, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var rep = await svc.RunReportAsync("abc123def456", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), ["day"], ["installs"], default);
        Assert.Empty(rep.Rows);
    }

    [Fact]
    public async Task Unconfigured_client_explains_setup()
    {
        var client = new AdjustClient(new FakeHttp(_ => FakeHttp.Json("{}")).Client, new AdjustOptions());
        var ex = await Assert.ThrowsAsync<PlatformNotConfiguredException>(() => client.ListAppsAsync(default));
        Assert.Contains("ADJUST_API_TOKEN", ex.Message);
    }

    [Fact]
    public async Task Retention_blends_only_cohorts_that_reached_each_day()
    {
        var (svc, _) = Service(_ => FakeHttp.Json("""
            {"rows":[
              {"day":"2026-11-01","installs":"100","retained_users_d1":"40","retained_users_d3":"20","retained_users_d7":"10"},
              {"day":"2026-11-06","installs":"100","retained_users_d1":"20","retained_users_d3":"10","retained_users_d7":"0"}],"warnings":[]}
            """));
        var rep = await svc.GetRetentionAsync(new AdjustApp("abc123def456", "Gapsy"), new Meeza.Marketing.Shared.Models.DateRange(new(2026, 11, 1), new(2026, 11, 6)), null, new DateOnly(2026, 11, 9), default);
        var all = Assert.Single(rep.Segments);
        Assert.Equal(200, all.Installs);
        Assert.Equal(30m, all.Curve.Single(p => p.Day == 1).Percent);  // (40 + 20) / 200
        Assert.Equal(15m, all.Curve.Single(p => p.Day == 3).Percent);  // 11-06 + 3 = 11-09 reached
        Assert.Equal(10m, all.Curve.Single(p => p.Day == 7).Percent);  // only the 11-01 cohort reached day 7
        Assert.Null(all.Curve.Single(p => p.Day == 30).Percent);
    }
}

public class AdjustStoreTests
{
    private static readonly ProjectConnection AdjustConn = new(Integrations.Adjust, "abc123def456", "Gapsy", null, null);

    [Fact]
    public void Projects_link_adjust_apps_and_reject_bad_tokens()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: [AdjustConn]);
        Assert.Contains(p.Connections, c => c.Integration == Integrations.Adjust && c.ExternalId == "abc123def456");
        Assert.Throws<ArgumentException>(() => t.Projects.SetConnections(p.Id, [new(Integrations.Adjust, "not a token!", null, null, null)]));
    }

    [Fact]
    public async Task Sync_writes_daily_campaign_and_cohort_facts_and_unlinking_purges_them()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: [AdjustConn]);
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var day = yesterday.AddDays(-3).ToString("yyyy-MM-dd");
        var http = new FakeHttp(req =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            if (req.RequestUri.AbsolutePath.EndsWith("/filters_data")) return FakeHttp.Json("""{"apps":[{"id":"abc123def456","name":"Gapsy"}]}""");
            var dims = q["dimensions"]!;
            var from = DateOnly.Parse(q["date_period"]!.Split(':')[0]);
            var to = DateOnly.Parse(q["date_period"]!.Split(':')[1]);
            var inWindow = DateOnly.Parse(day) >= from && DateOnly.Parse(day) <= to;
            if (!inWindow) return FakeHttp.Json("""{"rows":[],"warnings":[]}""");
            return dims switch
            {
                "day" when q["metrics"]!.Contains("daus") => FakeHttp.Json($$"""{"rows":[{"day":"{{day}}","installs":"30","sessions":"90","daus":"25","cost":"15.5","all_revenue":"3.2"}],"warnings":[]}"""),
                "day" => FakeHttp.Json($$"""{"rows":[{"day":"{{day}}","installs":"30","retained_users_d0":"30","retained_users_d1":"12","retained_users_d2":"9","retained_users_d3":"6","retained_users_d4":"4"}],"warnings":[]}"""),
                _ when dims.Contains("country_code") && dims.Contains("campaign_id_network") => FakeHttp.Json($$"""
                    {"rows":[{"day":"{{day}}","partner_name":"Facebook Installs","campaign_id_network":"1202","campaign_network":"UA","country_code":"pk","os_name":"android","installs":"20","cost":"15.5"},
                             {"day":"{{day}}","partner_name":"Organic","campaign_id_network":"unknown","campaign_network":"unknown","country_code":"pk","os_name":"android","installs":"10"}],"warnings":[]}
                    """),
                _ when dims == "day,campaign_network,campaign_id_network" => FakeHttp.Json($$"""{"rows":[{"day":"{{day}}","campaign_network":"UA","campaign_id_network":"1202","installs":"20","retained_users_d0":"20","retained_users_d1":"10"}],"warnings":[]}"""),
                _ when dims == "day,os_name" => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"detail":"bad dimension"}""") },
                _ => FakeHttp.Json("""{"rows":[],"warnings":[]}"""),
            };
        });
        var svc = new AdjustService(new AdjustClient(http.Client, new AdjustOptions { ApiToken = "adj-token-123" }));
        var engine = new SyncEngine(t.Projects, t.Syncs, [new AdjustSyncer(svc, t.Facts)], NullLogger<SyncEngine>.Instance);

        var outcome = (await engine.SyncProjectAsync(p.Id, "test", null, default)).Single();

        Assert.Equal("success", outcome.Status);
        Assert.Contains(outcome.Warnings, w => w.Contains("platform")); // the rejected platform split only warns
        Assert.Equal(DateOnly.Parse(day), outcome.LatestDataDate);
        var installs = t.Data.AdjustInstalls(p.Id, DateOnly.Parse(day), DateOnly.Parse(day)).Single();
        Assert.Equal(30, installs.Installs);
        Assert.Equal(20, installs.MetaInstalls);
        Assert.Equal(10, installs.OrganicInstalls);
        var all = t.Data.AdjustCohorts(p.Id, "all", DateOnly.Parse(day), DateOnly.Parse(day));
        Assert.Equal(12, all.Single(c => c.DayN == 1).ActiveUsers);
        Assert.DoesNotContain(all, c => c.DayN > 3); // day + 4 is today: not reached yet
        Assert.Equal("UA (1202)", t.Data.AdjustCohorts(p.Id, "campaign", DateOnly.Parse(day), DateOnly.Parse(day)).First().SegmentValue);
        Assert.Equal("UTC", t.Projects.Require(p.Id).Connections.Single().TimeZone);

        t.Projects.SetConnections(p.Id, []);
        Assert.Empty(t.Data.AdjustInstalls(p.Id, DateOnly.Parse(day), DateOnly.Parse(day)));
        Assert.Empty(t.Data.AdjustCohorts(p.Id, "all", DateOnly.Parse(day), DateOnly.Parse(day)));
    }

    [Fact]
    public void Campaign_retention_matches_meta_campaigns_by_adjust_campaign_id()
    {
        using var t = new TestDb();
        var p = t.NewProject(conns: [TestDb.MetaConn, AdjustConn]);
        var today = TimeZones.Today("UTC");
        var f = new Filters(today.AddDays(-7), today.AddDays(-1));
        var rows = Enumerable.Range(1, 7).Select(i => TestDb.Ad(today.AddDays(-i), "1202", "a1", 100, 10_000, 100, 10, name: "UA")).ToArray();
        t.Facts.ReplaceWindow("ad_daily", TestDb.Scope(p.Id, "meta", "act_1000000000000001"), "date", f.Start, f.End, rows);
        var scope = new Dictionary<string, object?> { ["project_id"] = p.Id, ["app_token"] = "abc123def456" };
        var cohorts = new[] { today.AddDays(-7), today.AddDays(-6) }.SelectMany(d => new[]
        {
            new AdjustCohortRow("campaign", "UA (1202)", d, 0, 50, 50), new AdjustCohortRow("campaign", "UA (1202)", d, 1, 20, 50),
        }).ToArray();
        var overall = new[] { today.AddDays(-7), today.AddDays(-6) }.SelectMany(d => new[]
        {
            new AdjustCohortRow("all", "", d, 0, 100, 100), new AdjustCohortRow("all", "", d, 1, 30, 100),
        }).ToArray();
        t.Facts.ReplaceWindow("adjust_cohorts", scope, "cohort_date", f.Start, f.End, cohorts, extraWhere: "segment_type=@seg", extraArgs: new { seg = "campaign" });
        t.Facts.ReplaceWindow("adjust_cohorts", scope, "cohort_date", f.Start, f.End, overall, extraWhere: "segment_type=@seg", extraArgs: new { seg = "all" });

        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var row = JsonSerializer.SerializeToElement(e.Campaigns(p.Id, f, "campaign"), Json.Output).GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(40m, row.GetProperty("d1").GetDecimal());          // 40 / 100 users of the Meta campaign
        Assert.Equal(100, row.GetProperty("cohortUsers").GetInt64());
        Assert.Contains(row.GetProperty("flags").EnumerateArray(), x => x.GetString() == "good_retention"); // 40% vs project 30%, both from Adjust

        var ret = JsonSerializer.SerializeToElement(e.Retention(p.Id, f, "adjust_network", null, "exact"), Json.Output);
        Assert.Equal("adjust", ret.GetProperty("source").GetString());
        Assert.True(ret.GetProperty("sources").GetProperty("adjust").GetBoolean());
        var fallback = JsonSerializer.SerializeToElement(e.Retention(p.Id, f, "all", null, "exact"), Json.Output);
        Assert.Equal("adjust", fallback.GetProperty("source").GetString()); // no GA4 linked: Adjust answers the shared segments
        Assert.Throws<ArgumentException>(() => e.Retention(p.Id, f, "adjust_channel", null, "exact"));
    }
}
