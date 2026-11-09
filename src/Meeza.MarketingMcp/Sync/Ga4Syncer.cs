using System.Globalization;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Sync;

/// <summary>
/// Synchronizes GA4 / Firebase daily users, revenue, events, dimension splits and exact-day cohort retention.
/// Cohorts are GA4 "firstSessionDate" daily cohorts; cohortActiveUsers on cohortNthDay N is exact-day (Day N) retention.
/// </summary>
public sealed class Ga4Syncer(AnalyticsService analytics, FactStore facts) : IIntegrationSyncer
{
    public const int PageSize = 10_000;
    public const int MaxCohortDay = 30;

    /// <summary>Dimensions stored as daily splits (dimension key → GA4 API name).</summary>
    public static readonly IReadOnlyDictionary<string, string> Dimensions = new Dictionary<string, string>
    {
        ["country"] = "countryId",
        ["platform"] = "platform",
        ["channel"] = "firstUserDefaultChannelGroup",
        ["source_medium"] = "firstUserSourceMedium",
        ["campaign"] = "firstUserCampaignName",
    };

    /// <summary>Cohort segments (segment type → GA4 API dimension). "all" is the unsegmented cohort.</summary>
    public static readonly IReadOnlyDictionary<string, string?> CohortSegments = new Dictionary<string, string?>
    {
        ["all"] = null,
        ["country"] = "countryId",
        ["platform"] = "platform",
        ["channel"] = "firstUserDefaultChannelGroup",
        ["campaign"] = "firstUserCampaignName",
        ["ad_group"] = "firstUserGoogleAdsAdGroupName",
    };

    public string Integration => Integrations.Ga4;
    public bool IsConfigured => analytics.IsConfigured;
    public string ConfigurationHint => AnalyticsOptions.Hint;
    // Cohorts keep maturing for 30 days and GA4 finishes processing within ~48h, so the last 33 days are re-read.
    public int LookbackDays => MaxCohortDay + 3;
    public int ChunkDays => 30;

    public async Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct)
    {
        // Same allow-list check as the live tools: an excluded property is rejected before any request is made.
        var id = (await analytics.ResolvePropertiesAsync(externalId, ct).ConfigureAwait(false)).Single();
        var props = await analytics.GetPropertiesAsync(ct).ConfigureAwait(false);
        var p = props.FirstOrDefault(x => x.PropertyId == id);
        // Probe the property (and learn its time zone and currency) with a tiny report.
        var probe = await analytics.RunReportAsync(id, new
        {
            dateRanges = new[] { new { startDate = "yesterday", endDate = "today" } },
            metrics = new[] { new { name = "activeUsers" } },
            limit = 1,
        }, ct).ConfigureAwait(false);
        return new AccountInfo(id, p?.DisplayName, probe.CurrencyCode, probe.TimeZone);
    }

    public async Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct)
    {
        var pid = account.ExternalId;
        var result = new WindowResult();
        var scope = new Dictionary<string, object?> { ["project_id"] = projectId, ["property_id"] = pid };
        var dateRange = new { startDate = Db.Iso(from), endDate = Db.Iso(to) };

        // ---- required: daily totals.
        var daily = await RunAllAsync(pid, new
        {
            dateRanges = new[] { dateRange },
            dimensions = new[] { D("date") },
            metrics = new[] { M("activeUsers"), M("newUsers"), M("active7DayUsers"), M("active28DayUsers"), M("sessions"),
                              M("userEngagementDuration"), M("totalRevenue"), M("totalAdRevenue"), M("purchaseRevenue"), M("totalPurchasers") },
            keepEmptyRows = true,
        }, ct).ConfigureAwait(false);
        var dailyRows = daily.Rows.Select(r =>
        {
            var date = AnalyticsService.ParseGaDate(r.Dimensions[0]);
            var au = L(daily, r, "activeUsers");
            if (au > 0) result.Seen(date);
            return new Ga4DailyRow(date, au, L(daily, r, "newUsers"), L(daily, r, "active7DayUsers"), L(daily, r, "active28DayUsers"),
                L(daily, r, "sessions"), daily.Report.Metric(r, "userEngagementDuration"), daily.Report.Metric(r, "totalRevenue"),
                daily.Report.Metric(r, "totalAdRevenue"), daily.Report.Metric(r, "purchaseRevenue"), L(daily, r, "totalPurchasers"),
                daily.Report.CurrencyCode);
        }).ToArray();

        // ---- required: dimension splits and events.
        var dimRows = new List<Ga4DimRow>();
        foreach (var (key, api) in Dimensions)
        {
            var rep = await RunAllAsync(pid, new
            {
                dateRanges = new[] { dateRange },
                dimensions = new[] { D("date"), D(api) },
                metrics = new[] { M("activeUsers"), M("newUsers"), M("totalRevenue") },
            }, ct).ConfigureAwait(false);
            dimRows.AddRange(rep.Rows
                .Select(r => new Ga4DimRow(AnalyticsService.ParseGaDate(r.Dimensions[0]), key, r.Dimensions[1] is { Length: > 0 } v ? v : "(not set)",
                    L(rep, r, "activeUsers"), L(rep, r, "newUsers"), rep.Report.Metric(r, "totalRevenue")))
                // GA4 can return the same label twice in rare cases ("(other)" rows); merge to honour the primary key.
                .GroupBy(x => (x.Date, x.Value))
                .Select(g => g.Count() == 1 ? g.First() : g.First() with { ActiveUsers = g.Sum(x => x.ActiveUsers), NewUsers = g.Sum(x => x.NewUsers), TotalRevenue = g.Sum(x => x.TotalRevenue) }));
            if (rep.Report.DataLossFromOtherRow) result.Warnings.Add($"GA4 grouped some {key} values into (other) because of cardinality limits.");
        }

        var events = await RunAllAsync(pid, new
        {
            dateRanges = new[] { dateRange },
            dimensions = new[] { D("date"), D("eventName") },
            metrics = new[] { M("eventCount"), M("totalUsers") },
        }, ct).ConfigureAwait(false);
        var eventRows = events.Rows.Select(r => new Ga4EventRow(AnalyticsService.ParseGaDate(r.Dimensions[0]), r.Dimensions[1],
            L(events, r, "eventCount"), L(events, r, "totalUsers"))).ToArray();

        // ---- cohorts: "all" is required; segmented cohorts are optional (a dimension GA4 rejects for cohorts only warns).
        var cohortRows = new List<Ga4CohortRow>();
        var cohortTypes = new List<string>();
        foreach (var (segment, api) in CohortSegments)
        {
            try
            {
                cohortRows.AddRange(await CohortsAsync(pid, segment, api, from, to, ct).ConfigureAwait(false));
                cohortTypes.Add(segment);
            }
            catch (PlatformApiException ex) when (segment != "all")
            {
                result.Warnings.Add($"Cohort retention by {segment} unavailable: {ex.Message}");
            }
        }

        using var c = facts.Db.Open();
        using var tx = c.BeginTransaction();
        result.Add(facts.ReplaceWindow(c, tx, "ga4_daily", scope, "date", from, to, dailyRows), "daily users");
        result.Add(facts.ReplaceWindow(c, tx, "ga4_dim_daily", scope, "date", from, to, dimRows), "dimensions");
        result.Add(facts.ReplaceWindow(c, tx, "ga4_events_daily", scope, "date", from, to, eventRows), "events");
        foreach (var segment in cohortTypes)
            result.Add(facts.ReplaceWindow(c, tx, "ga4_cohorts", scope, "cohort_date", from, to,
                cohortRows.Where(x => x.SegmentType == segment).ToArray(), extraWhere: "segment_type=@seg", extraArgs: new { seg = segment }), $"cohorts ({segment})");
        tx.Commit();
        return result;
    }

    /// <summary>Daily first-session cohorts for every date in the window, day 0..30, optionally split by one dimension.</summary>
    private async Task<IReadOnlyList<Ga4CohortRow>> CohortsAsync(string pid, string segment, string? api, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var dates = Enumerable.Range(0, to.DayNumber - from.DayNumber + 1).Select(i => from.AddDays(i)).ToArray();
        var rows = new List<Ga4CohortRow>();
        foreach (var chunk in dates.Chunk(AnalyticsService.MaxCohortsPerRequest))
        {
            var dims = new List<object> { D("cohort"), D("cohortNthDay") };
            if (api is not null) dims.Add(D(api));
            var rep = await RunAllAsync(pid, new
            {
                dimensions = dims.ToArray(),
                metrics = new[] { M("cohortActiveUsers"), M("cohortTotalUsers") },
                cohortSpec = new
                {
                    cohorts = chunk.Select(d => new { name = Db.Iso(d), dimension = "firstSessionDate", dateRange = new { startDate = Db.Iso(d), endDate = Db.Iso(d) } }).ToArray(),
                    cohortsRange = new { granularity = "DAILY", startOffset = 0, endOffset = MaxCohortDay },
                },
            }, ct).ConfigureAwait(false);
            foreach (var r in rep.Rows)
            {
                if (!DateOnly.TryParseExact(r.Dimensions[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var cohort)) continue;
                if (!int.TryParse(r.Dimensions[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
                var value = api is null ? "" : r.Dimensions[2] is { Length: > 0 } v ? v : "(not set)";
                rows.Add(new Ga4CohortRow(segment, value, cohort, n, L(rep, r, "cohortActiveUsers") ?? 0, L(rep, r, "cohortTotalUsers") ?? 0));
            }
        }
        return rows.GroupBy(x => (x.SegmentValue, x.CohortDate, x.DayN))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { ActiveUsers = g.Sum(x => x.ActiveUsers), CohortUsers = g.Sum(x => x.CohortUsers) })
            .ToArray();
    }

    private sealed record Paged(Ga4Report Report, IReadOnlyList<Ga4Row> Rows);

    /// <summary>Runs a report and follows offset pagination until every row is read.</summary>
    private async Task<Paged> RunAllAsync(string pid, object body, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.SerializeToNode(body)!.AsObject();
        var rows = new List<Ga4Row>();
        Ga4Report? first = null;
        for (var offset = 0; ; offset += PageSize)
        {
            json["limit"] = PageSize;
            json["offset"] = offset;
            var rep = await analytics.RunReportAsync(pid, json, ct).ConfigureAwait(false);
            first ??= rep;
            rows.AddRange(rep.Rows);
            if (rep.Rows.Count < PageSize || offset > 2_000_000) break;
        }
        return new Paged(first!, rows);
    }

    private static object D(string name) => new { name };
    private static object M(string name) => new { name };
    private static long? L(Paged p, Ga4Row r, string metric) => p.Report.Metric(r, metric) is { } v ? (long)v : null;
}
