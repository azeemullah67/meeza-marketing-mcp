using System.Globalization;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Adjust;

public sealed record AdjustPerformanceRow(
    string Segment, string? Network, string? CampaignId,
    long? Installs, long? Clicks, long? Impressions, long? Sessions,
    decimal? Cost, decimal? Cpi, decimal? Ctr, decimal? Ipm,
    decimal? AllRevenue, decimal? AdRevenue, decimal? IapRevenue);

public sealed record AdjustPerformanceReport(
    string AppToken, string? AppName, string Range, string Currency, string GroupBy,
    AdjustPerformanceRow Totals, IReadOnlyList<AdjustPerformanceRow> Rows, int OmittedRows, IReadOnlyList<string> Warnings);

public sealed record AdjustRetentionPoint(int Day, decimal? Percent, long CohortUsers, int Cohorts);

public sealed record AdjustRetentionSegment(string Segment, long Installs, IReadOnlyList<AdjustRetentionPoint> Curve);

public sealed record AdjustRetentionReport(
    string AppToken, string? AppName, string CohortRange, DateOnly LastCompleteDay, string? GroupBy,
    IReadOnlyList<AdjustRetentionSegment> Segments, IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only Adjust queries. Every report goes through <see cref="RunReportAsync"/>, which checks the app against the
/// ADJUST_APP_TOKENS allow-list first, so neither the tools nor the synchronizer can read an excluded app.
/// </summary>
public sealed class AdjustService(AdjustClient client)
{
    /// <summary>Retention days the live tool reports.</summary>
    public static readonly int[] ToolRetentionDays = [1, 3, 7, 14, 30];

    /// <summary>Group-by names accepted by the tools → Adjust dimensions. The first dimension is the label.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Groupings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["network"] = ["partner_name"],
        ["campaign"] = ["campaign_network", "campaign_id_network", "partner_name"],
        ["adgroup"] = ["adgroup_network", "adgroup_id_network", "campaign_network", "partner_name"],
        ["creative"] = ["creative_network", "creative_id_network", "partner_name"],
        ["country"] = ["country_code"],
        ["platform"] = ["os_name"],
        ["day"] = ["day"],
    };

    public static readonly string[] PerformanceMetrics = ["installs", "clicks", "impressions", "sessions", "cost", "all_revenue", "ad_revenue", "revenue"];

    public static string RetainedMetric(int day) => $"retained_users_d{day}";

    private volatile IReadOnlyList<AdjustApp>? _apps;

    public bool IsConfigured => client.Options.IsConfigured;
    public string Currency => client.Options.Currency;
    public IReadOnlyList<string> AllowedAppTokens => client.Options.AppTokens;
    public void ResetCache() => _apps = null;

    public string Authorize(string appToken)
    {
        var t = AdjustOptions.NormalizeAppToken(appToken);
        var allow = client.Options.AppTokens;
        if (allow.Count > 0 && !allow.Contains(t, StringComparer.Ordinal))
            throw new AccountAccessDeniedException(AdjustOptions.Platform, t, "it is not in ADJUST_APP_TOKENS");
        return t;
    }

    public async Task<IReadOnlyList<AdjustApp>> GetAppsAsync(CancellationToken ct)
    {
        if (_apps is { } cached) return cached;
        var visible = await client.ListAppsAsync(ct).ConfigureAwait(false);
        var allow = client.Options.AppTokens;
        IReadOnlyList<AdjustApp> result = allow.Count == 0
            ? visible
            : allow.Select(t => visible.FirstOrDefault(v => v.AppToken == t) ?? new AdjustApp(t, null)).ToArray();
        if (result.Count > 0) _apps = result;
        return result;
    }

    public async Task<IReadOnlyList<AdjustApp>> ResolveAppsAsync(string? appToken, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(appToken))
        {
            var t = Authorize(appToken);
            var known = (await GetAppsAsync(ct).ConfigureAwait(false)).FirstOrDefault(a => a.AppToken == t);
            return [known ?? new AdjustApp(t, null)];
        }
        var apps = await GetAppsAsync(ct).ConfigureAwait(false);
        if (apps.Count == 0) throw new PlatformApiException(AdjustOptions.Platform, "The Adjust API token can't see any app. Check the token's user has access to your apps in Adjust.");
        return apps;
    }

    /// <summary>
    /// Runs a report for one app. Dates are UTC calendar days; installs use first attribution (the install's original
    /// source); cohort metrics are returned for immature cohorts too, and callers blank the days a cohort has not reached.
    /// </summary>
    public Task<AdjustReport> RunReportAsync(string appToken, DateOnly from, DateOnly to, IEnumerable<string> dimensions, IEnumerable<string> metrics, CancellationToken ct)
    {
        var t = Authorize(appToken);
        if (to < from) throw new ArgumentException("The end date is before the start date.");
        return client.GetReportAsync(
        [
            new("app_token__in", t),
            new("date_period", $"{Iso(from)}:{Iso(to)}"),
            new("dimensions", string.Join(",", dimensions)),
            new("metrics", string.Join(",", metrics)),
            new("currency", client.Options.Currency),
            new("utc_offset", "+00:00"),
            new("attribution_source", "first"),
            new("cohort_maturity", "immature"),
        ], ct);
    }

    // ---------------------------------------------------------------- performance

    public async Task<AdjustPerformanceReport> GetPerformanceAsync(AdjustApp app, DateRange range, string? groupBy, int top, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(groupBy) ? "network" : groupBy.Trim();
        if (!Groupings.TryGetValue(key, out var dims))
            throw new ArgumentException($"Unknown groupBy '{groupBy}'. Use one of: {string.Join(", ", Groupings.Keys)}.");
        var rep = await RunReportAsync(app.AppToken, range.Start, range.End, dims, PerformanceMetrics, ct).ConfigureAwait(false);
        var rows = rep.Rows.Select(r => Row(SegmentLabel(key, r), AdjustReport.Text(r, "partner_name"),
            key is "campaign" ? AdjustReport.Text(r, "campaign_id_network") : null, r)).ToArray();
        var ordered = (key == "day" ? rows.OrderBy(r => r.Segment) : rows.OrderByDescending(r => r.Cost ?? 0).ThenByDescending(r => r.Installs ?? 0)).ToArray();
        var limit = Math.Clamp(top, 1, 500);
        return new AdjustPerformanceReport(app.AppToken, app.Name, range.ToString(), client.Options.Currency, key,
            Totals(rows), ordered.Take(limit).ToArray(), Math.Max(0, ordered.Length - limit), rep.Warnings);
    }

    private static AdjustPerformanceRow Row(string segment, string? network, string? campaignId, IReadOnlyDictionary<string, string?> r)
    {
        var installs = AdjustReport.Count(r, "installs");
        var clicks = AdjustReport.Count(r, "clicks");
        var impressions = AdjustReport.Count(r, "impressions");
        var cost = AdjustReport.Number(r, "cost");
        return new AdjustPerformanceRow(segment, network, campaignId, installs, clicks, impressions, AdjustReport.Count(r, "sessions"),
            Round(cost), MetricMath.CostPer(cost, installs), MetricMath.Ctr(clicks, impressions), Ipm(installs, impressions),
            Round(AdjustReport.Number(r, "all_revenue")), Round(AdjustReport.Number(r, "ad_revenue")), Round(AdjustReport.Number(r, "revenue")));
    }

    private static AdjustPerformanceRow Totals(IReadOnlyList<AdjustPerformanceRow> rows)
    {
        long? L(Func<AdjustPerformanceRow, long?> f) => rows.All(r => f(r) is null) ? null : rows.Sum(r => f(r) ?? 0);
        decimal? D(Func<AdjustPerformanceRow, decimal?> f) => rows.All(r => f(r) is null) ? null : rows.Sum(r => f(r) ?? 0);
        var installs = L(r => r.Installs);
        var clicks = L(r => r.Clicks);
        var impressions = L(r => r.Impressions);
        var cost = D(r => r.Cost);
        return new AdjustPerformanceRow("total", null, null, installs, clicks, impressions, L(r => r.Sessions), cost,
            MetricMath.CostPer(cost, installs), MetricMath.Ctr(clicks, impressions), Ipm(installs, impressions),
            D(r => r.AllRevenue), D(r => r.AdRevenue), D(r => r.IapRevenue));
    }

    /// <summary>Human label for a row: "Campaign name (id)" for campaign-like groupings, otherwise the dimension value.</summary>
    public static string SegmentLabel(string groupBy, IReadOnlyDictionary<string, string?> r)
    {
        var dims = Groupings[groupBy];
        var name = AdjustReport.Text(r, dims[0]);
        if (dims.Length > 1 && dims[1].EndsWith("_id_network", StringComparison.Ordinal))
            return WithId(name, AdjustReport.Text(r, dims[1]));
        return name ?? "(unknown)";
    }

    /// <summary>"Name (id)" when the network id is known, so campaigns can later be matched to Meta / Google Ads campaign ids.</summary>
    public static string WithId(string? name, string? id)
    {
        var hasId = !string.IsNullOrWhiteSpace(id) && !id.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        var n = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (!hasId) return n ?? "(unknown)";
        if (n is null || n == id) return id!;
        return n.EndsWith($"({id})", StringComparison.Ordinal) ? n : $"{n} ({id})";
    }

    /// <summary>The network id at the end of a "Name (id)" label, or null.</summary>
    public static string? IdOf(string label)
    {
        if (!label.EndsWith(')')) return null;
        var open = label.LastIndexOf('(');
        if (open < 0) return null;
        var id = label[(open + 1)..^1].Trim();
        return id.Length == 0 ? null : id;
    }

    // ---------------------------------------------------------------- retention

    public async Task<AdjustRetentionReport> GetRetentionAsync(AdjustApp app, DateRange cohorts, string? groupBy, DateOnly lastCompleteDay, CancellationToken ct)
    {
        string? key = null;
        string[] dims = ["day"];
        if (!string.IsNullOrWhiteSpace(groupBy) && !groupBy.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            key = groupBy.Trim();
            if (!Groupings.TryGetValue(key, out var g) || key == "day")
                throw new ArgumentException($"Unknown groupBy '{groupBy}'. Use all, {string.Join(", ", Groupings.Keys.Where(k => k != "day"))}.");
            dims = ["day", .. g];
        }
        var metrics = new[] { "installs" }.Concat(ToolRetentionDays.Select(RetainedMetric)).ToArray();
        var rep = await RunReportAsync(app.AppToken, cohorts.Start, cohorts.End, dims, metrics, ct).ConfigureAwait(false);

        var cells = new List<(string Segment, DateOnly Cohort, int Day, long Retained, long Installs)>();
        foreach (var r in rep.Rows)
        {
            if (!TryDay(AdjustReport.Text(r, "day"), out var cohort)) continue;
            var installs = AdjustReport.Count(r, "installs") ?? 0;
            var segment = key is null ? "all" : SegmentLabel(key, r);
            foreach (var d in ToolRetentionDays)
                if (Reached(cohort, d, lastCompleteDay) && AdjustReport.Count(r, RetainedMetric(d)) is { } retained)
                    cells.Add((segment, cohort, d, retained, installs));
            cells.Add((segment, cohort, 0, installs, installs)); // keeps cohorts with installs but no matured day in the size count
        }

        var segments = cells.GroupBy(c => c.Segment).Select(g =>
        {
            var size = g.Where(c => c.Day == 0).Sum(c => c.Installs);
            var curve = ToolRetentionDays.Select(d =>
            {
                var x = g.Where(c => c.Day == d).ToArray();
                var users = x.Sum(c => c.Installs);
                return new AdjustRetentionPoint(d, users > 0 ? Math.Round(x.Sum(c => c.Retained) * 100m / users, 1) : null, users, x.Length);
            }).ToArray();
            return new AdjustRetentionSegment(g.Key, size, curve);
        }).OrderByDescending(s => s.Installs).ToArray();
        return new AdjustRetentionReport(app.AppToken, app.Name, cohorts.ToString(), lastCompleteDay, key ?? "all", segments, rep.Warnings);
    }

    /// <summary>True when a cohort installed on <paramref name="cohort"/> has fully reached day <paramref name="day"/>.</summary>
    public static bool Reached(DateOnly cohort, int day, DateOnly lastCompleteDay) => cohort.AddDays(day) <= lastCompleteDay;

    public static bool TryDay(string? value, out DateOnly day) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static decimal? Ipm(long? installs, long? impressions) =>
        installs is null || impressions is not > 0 ? null : Math.Round(installs.Value * 1000m / impressions.Value, 2);

    private static decimal? Round(decimal? v) => v is null ? null : Math.Round(v.Value, 2);

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
