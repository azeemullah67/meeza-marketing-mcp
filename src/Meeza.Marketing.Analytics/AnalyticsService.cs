using System.Globalization;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Analytics;

public sealed record Ga4Daily(DateOnly Date, long ActiveUsers, long NewUsers, long Sessions, decimal? EngagementSecondsPerUser);

public sealed record Ga4Breakdown(string Value, long ActiveUsers, long NewUsers, decimal SharePercent);

public sealed record ActiveUsersReport(
    string PropertyId, string? TimeZone, string Range,
    long ActiveUsersInRange, long NewUsersInRange, decimal? AverageDau, decimal? PeakDau,
    decimal? StickinessPercent, decimal? EngagementSecondsPerUser,
    IReadOnlyList<Ga4Daily> Daily, string? BreakdownBy, IReadOnlyList<Ga4Breakdown>? Breakdown, bool Thresholded);

public sealed record CohortSummary(DateOnly Cohort, long Users, decimal? D1, decimal? D3, decimal? D7, decimal? D14, decimal? D30);

public sealed record RetentionReport(
    string PropertyId, string CohortRange, DateOnly LastCompleteDay, int MaxDay,
    IReadOnlyList<RetentionDay> Curve, IReadOnlyList<CohortSummary> Cohorts, RetentionFit? Fit, bool Thresholded);

public sealed record RevenueReport(
    string PropertyId, string Range, string? Currency,
    decimal? TotalRevenue, decimal? AdRevenue, decimal? PurchaseRevenue,
    long UserDays, decimal? Arpdau, decimal? AdSharePercent);

/// <summary>Read-only GA4 queries shaped for game marketing: active users, cohort retention and revenue per daily user.</summary>
public sealed class AnalyticsService(AnalyticsClient client)
{
    public const int MaxCohortsPerRequest = 10;

    /// <summary>Breakdown names accepted by the tools, mapped to GA4 API dimensions.</summary>
    public static readonly IReadOnlyDictionary<string, string> Breakdowns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["country"] = "countryId",
        ["app_version"] = "appVersion",
        ["platform"] = "platform",
        ["source"] = "firstUserSource",
        ["medium"] = "firstUserMedium",
        ["campaign"] = "firstUserCampaignName",
        ["device"] = "deviceCategory",
        ["os_version"] = "operatingSystemVersion",
    };

    private volatile IReadOnlyList<Ga4Property>? _properties;

    public bool IsConfigured => client.Options.IsConfigured;
    public bool UsesPlayKey => client.Options.UsesPlayKey;
    public void ResetCache() => _properties = null;

    /// <summary>The GA4_PROPERTY_IDS allow-list (normalized). Empty means every property the service account can see.</summary>
    public IReadOnlyList<string> AllowedPropertyIds => client.Options.PropertyIds;

    /// <summary>
    /// Throws unless <paramref name="propertyId"/> may be queried. This is the single authorization point: every report,
    /// including the synchronizer's, goes through <see cref="RunReportAsync"/>, which calls it.
    /// </summary>
    public string Authorize(string propertyId)
    {
        var id = AnalyticsOptions.NormalizePropertyId(propertyId);
        var allow = client.Options.PropertyIds;
        if (allow.Count > 0 && !allow.Contains(id))
            throw new AccountAccessDeniedException(AnalyticsOptions.Platform, id, "it is not in GA4_PROPERTY_IDS");
        return id;
    }

    /// <summary>Runs a Data API report after checking the property against the allow-list. The only way to reach the client's report call.</summary>
    public Task<Ga4Report> RunReportAsync(string propertyId, object body, CancellationToken ct) =>
        client.RunReportAsync(Authorize(propertyId), body, ct);

    public async Task<IReadOnlyList<Ga4Property>> GetPropertiesAsync(CancellationToken ct)
    {
        if (_properties is { } cached) return cached;
        var visible = await client.ListPropertiesAsync(ct).ConfigureAwait(false);
        var allow = client.Options.PropertyIds;
        IReadOnlyList<Ga4Property> result = allow.Count == 0
            ? visible
            : allow.Select(id => visible.FirstOrDefault(v => v.PropertyId == id) ?? new Ga4Property(id, null, null)).ToArray();
        if (result.Count > 0) _properties = result; // never cache "nothing shared yet": access is often granted minutes later
        return result;
    }

    /// <summary>The requested property (checked against the allow-list), or every allowed/visible property.</summary>
    public async Task<IReadOnlyList<string>> ResolvePropertiesAsync(string? propertyId, CancellationToken ct)
    {
        var allow = client.Options.PropertyIds;
        if (!string.IsNullOrWhiteSpace(propertyId))
        {
            var id = AnalyticsOptions.NormalizePropertyId(propertyId);
            if (allow.Count > 0 && !allow.Contains(id))
                throw new AccountAccessDeniedException(AnalyticsOptions.Platform, id, "it is not in GA4_PROPERTY_IDS");
            return [id];
        }
        if (allow.Count > 0) return allow;
        var props = await GetPropertiesAsync(ct).ConfigureAwait(false);
        if (props.Count == 0)
            throw new PlatformApiException(AnalyticsOptions.Platform,
                $"The service account ({SafeEmail()}) can't see any GA4 property. Add it as a Viewer in GA4 → Admin → Property access management.");
        return props.Select(p => p.PropertyId).ToArray();
    }

    public string? SafeEmail()
    {
        try { return client.ServiceAccountEmail; } catch { return null; }
    }

    // ---------------------------------------------------------------- active users

    public async Task<ActiveUsersReport> GetActiveUsersAsync(string propertyId, DateRange range, string? breakdown, string? platform, CancellationToken ct)
    {
        string? breakdownDim = null;
        if (!string.IsNullOrWhiteSpace(breakdown) && !Breakdowns.TryGetValue(breakdown.Trim(), out breakdownDim))
            throw new ArgumentException($"Unknown breakdown '{breakdown}'. Use one of: {string.Join(", ", Breakdowns.Keys)}.");
        var filter = PlatformFilter(platform);

        var dailyTask = RunReportAsync(propertyId, new
        {
            dateRanges = new[] { DateRangeJson(range) },
            dimensions = new[] { new { name = "date" } },
            metrics = new[] { M("activeUsers"), M("newUsers"), M("sessions"), M("userEngagementDuration") },
            dimensionFilter = filter,
            orderBys = new[] { new { dimension = new { dimensionName = "date" } } },
            limit = 1000,
            keepEmptyRows = true,
        }, ct);
        var totalTask = RunReportAsync(propertyId, new
        {
            dateRanges = new[] { DateRangeJson(range) },
            metrics = new[] { M("activeUsers"), M("newUsers"), M("userEngagementDuration") },
            dimensionFilter = filter,
        }, ct);
        Task<Ga4Report>? breakdownTask = breakdownDim is null ? null : RunReportAsync(propertyId, new
        {
            dateRanges = new[] { DateRangeJson(range) },
            dimensions = new[] { new { name = breakdownDim } },
            metrics = new[] { M("activeUsers"), M("newUsers") },
            dimensionFilter = filter,
            orderBys = new[] { new { metric = new { metricName = "activeUsers" }, desc = true } },
            limit = 50,
        }, ct);

        var daily = await dailyTask.ConfigureAwait(false);
        var total = await totalTask.ConfigureAwait(false);
        var dailyRows = daily.Rows
            .Select(r =>
            {
                var au = (long)(daily.Metric(r, "activeUsers") ?? 0);
                var eng = daily.Metric(r, "userEngagementDuration");
                return new Ga4Daily(ParseGaDate(daily.Dimension(r, "date")!), au, (long)(daily.Metric(r, "newUsers") ?? 0),
                    (long)(daily.Metric(r, "sessions") ?? 0), au > 0 && eng is not null ? Math.Round(eng.Value / au, 1) : null);
            })
            .OrderBy(d => d.Date)
            .ToArray();

        var totalRow = total.Rows.FirstOrDefault();
        var rangeUsers = (long)(totalRow is null ? 0 : total.Metric(totalRow, "activeUsers") ?? 0);
        var rangeNew = (long)(totalRow is null ? 0 : total.Metric(totalRow, "newUsers") ?? 0);
        var engTotal = totalRow is null ? null : total.Metric(totalRow, "userEngagementDuration");
        decimal? avgDau = dailyRows.Length == 0 ? null : Math.Round((decimal)dailyRows.Average(d => d.ActiveUsers), 1);
        decimal? peak = dailyRows.Length == 0 ? null : dailyRows.Max(d => d.ActiveUsers);
        var userDays = dailyRows.Sum(d => d.ActiveUsers);

        IReadOnlyList<Ga4Breakdown>? rows = null;
        var thresholded = daily.SubjectToThresholding || total.SubjectToThresholding;
        if (breakdownTask is not null)
        {
            var b = await breakdownTask.ConfigureAwait(false);
            thresholded |= b.SubjectToThresholding;
            var sum = b.Rows.Sum(r => b.Metric(r, "activeUsers") ?? 0);
            rows = b.Rows.Select(r =>
            {
                var au = b.Metric(r, "activeUsers") ?? 0;
                return new Ga4Breakdown(b.Dimension(r, breakdownDim!) is { Length: > 0 } v ? v : "(not set)", (long)au,
                    (long)(b.Metric(r, "newUsers") ?? 0), sum > 0 ? Math.Round(au * 100 / sum, 1) : 0);
            }).ToArray();
        }

        return new ActiveUsersReport(
            propertyId, daily.TimeZone, range.ToString(), rangeUsers, rangeNew, avgDau, peak,
            avgDau is not null && rangeUsers > 0 ? Math.Round(avgDau.Value * 100 / rangeUsers, 1) : null,
            userDays > 0 && engTotal is not null ? Math.Round(engTotal.Value / userDays, 1) : null,
            dailyRows, breakdown?.Trim().ToLowerInvariant(), rows, thresholded);
    }

    // ---------------------------------------------------------------- retention

    /// <summary>
    /// Day-N retention for daily install cohorts (users grouped by first session date) in <paramref name="cohortRange"/>.
    /// </summary>
    public async Task<RetentionReport> GetRetentionAsync(
        string propertyId, DateRange cohortRange, int maxDay, string? platform, DateOnly lastCompleteDay, CancellationToken ct,
        string? appVersion = null)
    {
        maxDay = Math.Clamp(maxDay, 1, 60);
        if (cohortRange.End > lastCompleteDay) cohortRange = cohortRange with { End = lastCompleteDay };
        if (cohortRange.End < cohortRange.Start) throw new ArgumentException("The cohort range has no complete days yet.");
        if (cohortRange.Days > 90) throw new ArgumentException("Use a cohort range of 90 days or less.");

        var dates = Enumerable.Range(0, cohortRange.Days).Select(i => cohortRange.Start.AddDays(i)).ToArray();
        var filter = And(PlatformFilter(platform), AppVersionFilter(appVersion));
        var tasks = dates.Chunk(MaxCohortsPerRequest).Select(chunk => RunReportAsync(propertyId, new
        {
            dimensions = new[] { new { name = "cohort" }, new { name = "cohortNthDay" } },
            metrics = new[] { M("cohortActiveUsers"), M("cohortTotalUsers") },
            dimensionFilter = filter,
            cohortSpec = new
            {
                cohorts = chunk.Select(d => new { name = Iso(d), dimension = "firstSessionDate", dateRange = new { startDate = Iso(d), endDate = Iso(d) } }).ToArray(),
                cohortsRange = new { granularity = "DAILY", startOffset = 0, endOffset = maxDay },
            },
            limit = 10000,
        }, ct)).ToArray();
        var reports = await Task.WhenAll(tasks).ConfigureAwait(false);

        var points = new List<CohortPoint>();
        foreach (var rep in reports)
            foreach (var r in rep.Rows)
            {
                if (!DateOnly.TryParseExact(rep.Dimension(r, "cohort"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var c)) continue;
                if (!int.TryParse(rep.Dimension(r, "cohortNthDay"), NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
                points.Add(new CohortPoint(c, n, (long)(rep.Metric(r, "cohortActiveUsers") ?? 0), (long)(rep.Metric(r, "cohortTotalUsers") ?? 0)));
            }

        var curve = RetentionMath.Blend(points, lastCompleteDay, maxDay);
        var cohorts = points.GroupBy(p => p.Cohort).OrderBy(g => g.Key).Select(g =>
        {
            var one = RetentionMath.Blend(g, lastCompleteDay, Math.Min(maxDay, 30));
            return new CohortSummary(g.Key, g.Max(p => p.CohortUsers),
                RetentionMath.On(one, 1), RetentionMath.On(one, 3), RetentionMath.On(one, 7), RetentionMath.On(one, 14), RetentionMath.On(one, 30));
        }).ToArray();
        return new RetentionReport(propertyId, cohortRange.ToString(), lastCompleteDay, maxDay, curve, cohorts,
            RetentionMath.Fit(curve), reports.Any(r => r.SubjectToThresholding));
    }

    // ---------------------------------------------------------------- revenue

    /// <summary>Revenue GA4 knows about (IAP purchases, plus ad revenue when AdMob or another ad source is linked) and ARPDAU.</summary>
    public async Task<RevenueReport> GetRevenueAsync(string propertyId, DateRange range, string? platform, CancellationToken ct)
    {
        var filter = PlatformFilter(platform);
        var rep = await RunReportAsync(propertyId, new
        {
            dateRanges = new[] { DateRangeJson(range) },
            dimensions = new[] { new { name = "date" } },
            metrics = new[] { M("activeUsers"), M("totalRevenue"), M("totalAdRevenue"), M("purchaseRevenue") },
            dimensionFilter = filter,
            limit = 1000,
        }, ct).ConfigureAwait(false);

        decimal? Sum(string m) => rep.Rows.Count == 0 ? null : rep.Rows.Sum(r => rep.Metric(r, m) ?? 0);
        var userDays = (long)rep.Rows.Sum(r => rep.Metric(r, "activeUsers") ?? 0);
        var total = Sum("totalRevenue");
        var ad = Sum("totalAdRevenue");
        return new RevenueReport(propertyId, range.ToString(), rep.CurrencyCode, total, ad, Sum("purchaseRevenue"), userDays,
            total is not null && userDays > 0 ? Math.Round(total.Value / userDays, 4) : null,
            total is > 0 && ad is not null ? Math.Round(ad.Value * 100 / total.Value, 1) : null);
    }

    // ---------------------------------------------------------------- level progression

    /// <summary>
    /// Level funnel from the game's own events (level_start, level_first_move, level_end with success 1/0, level_quit)
    /// broken down by the event-scoped custom dimensions mode and level_name, which must be registered in GA4.
    /// </summary>
    public async Task<LevelProgressionReport> GetLevelProgressionAsync(
        string propertyId, DateRange range, string? platform, string? appVersion, string mode, CancellationToken ct)
    {
        var filter = And(PlatformFilter(platform), AppVersionFilter(appVersion),
            new { filter = new { fieldName = "eventName", inListFilter = new { values = ProgressionMath.AllEvents } } });
        var rep = await RunReportAsync(propertyId, new
        {
            dateRanges = new[] { DateRangeJson(range) },
            dimensions = new[]
            {
                new { name = "eventName" }, new { name = "customEvent:mode" },
                new { name = "customEvent:level_name" }, new { name = "customEvent:success" },
            },
            metrics = new[] { M("totalUsers"), M("eventCount") },
            dimensionFilter = filter,
            limit = 10000,
        }, ct).ConfigureAwait(false);

        var rows = rep.Rows.Select(r => new LevelEventRow(
            rep.Dimension(r, "customEvent:mode") ?? "(not set)",
            rep.Dimension(r, "customEvent:level_name") ?? "(not set)",
            rep.Dimension(r, "eventName") ?? "",
            NormalizeSuccess(rep.Dimension(r, "customEvent:success")),
            (long)(rep.Metric(r, "totalUsers") ?? 0),
            (long)(rep.Metric(r, "eventCount") ?? 0))).ToList();

        var successSource = rows.Any(r => r.EventName == "level_end" && r.Success is not null) ? "dimension" : null;
        if (successSource is null)
        {
            // A numeric parameter registered as a custom *dimension* comes back empty. Try it as a custom *metric*:
            // sum(success) over level_end = wins, eventCount - wins = losses (events, not distinct players).
            try
            {
                var metricRep = await RunReportAsync(propertyId, new
                {
                    dateRanges = new[] { DateRangeJson(range) },
                    dimensions = new[] { new { name = "customEvent:mode" }, new { name = "customEvent:level_name" } },
                    metrics = new[] { M("eventCount"), M("customEvent:success") },
                    dimensionFilter = And(PlatformFilter(platform), AppVersionFilter(appVersion),
                        new { filter = new { fieldName = "eventName", stringFilter = new { value = "level_end", matchType = "EXACT" } } }),
                    limit = 10000,
                }, ct).ConfigureAwait(false);
                rows.RemoveAll(r => r.EventName == "level_end");
                foreach (var r in metricRep.Rows)
                {
                    var m = metricRep.Dimension(r, "customEvent:mode") ?? "(not set)";
                    var l = metricRep.Dimension(r, "customEvent:level_name") ?? "(not set)";
                    var ends = (long)(metricRep.Metric(r, "eventCount") ?? 0);
                    var wins = Math.Clamp((long)Math.Round(metricRep.Metric(r, "customEvent:success") ?? 0), 0, ends);
                    rows.Add(new LevelEventRow(m, l, "level_end", "1", 0, wins));
                    rows.Add(new LevelEventRow(m, l, "level_end", "0", 0, ends - wins));
                }
                successSource = "metric";
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Not registered as a metric either: wins/losses stay unknown.
            }
        }

        return ProgressionMath.Build(propertyId, range.ToString(), rows, mode, rep.SubjectToThresholding, rep.DataLossFromOtherRow) with { SuccessSource = successSource };
    }

    /// <summary>GA4 returns numeric parameters as "1"/"0" or "1.0" (or empty when captured wrongly); map to "1"/"0", keep other text as-is.</summary>
    private static string? NormalizeSuccess(string? value) =>
        string.IsNullOrWhiteSpace(value) || value == "(not set)" ? null
        : decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? (d > 0 ? "1" : "0")
        : value;

    // ---------------------------------------------------------------- helpers

    private static object M(string name) => new { name };

    private static object DateRangeJson(DateRange r) => new { startDate = r.StartIso, endDate = r.EndIso };

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ParseGaDate(string value) =>
        DateOnly.ParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>Optional filter on GA4's appVersion dimension; accepts one version or a comma-separated list.</summary>
    public static object? AppVersionFilter(string? appVersion)
    {
        if (string.IsNullOrWhiteSpace(appVersion)) return null;
        var values = appVersion.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
        if (values.Length == 0) return null;
        return values.Length == 1
            ? (object)new { filter = new { fieldName = "appVersion", stringFilter = new { value = values[0], matchType = "EXACT", caseSensitive = false } } }
            : new { filter = new { fieldName = "appVersion", inListFilter = new { values, caseSensitive = false } } };
    }

    /// <summary>Combines optional filter expressions with AND; returns null when none are set.</summary>
    public static object? And(params object?[] filters)
    {
        var set = filters.Where(f => f is not null).ToArray();
        return set.Length switch { 0 => null, 1 => set[0], _ => new { andGroup = new { expressions = set } } };
    }

    /// <summary>Optional filter on GA4's platform dimension (Android, iOS, web).</summary>
    public static object? PlatformFilter(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform) || platform.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        var value = platform.Trim().ToLowerInvariant() switch
        {
            "android" => "Android",
            "ios" => "iOS",
            "web" => "web",
            _ => throw new ArgumentException($"Unknown platform '{platform}'. Use android, ios, web or all."),
        };
        return new { filter = new { fieldName = "platform", stringFilter = new { value, matchType = "EXACT", caseSensitive = false } } };
    }
}
