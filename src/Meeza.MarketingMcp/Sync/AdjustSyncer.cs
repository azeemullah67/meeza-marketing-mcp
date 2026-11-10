using Meeza.Marketing.Adjust;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Sync;

/// <summary>
/// Synchronizes one Adjust app: daily totals, network x campaign x country x platform facts, and install cohorts
/// (day 0..30) overall and per network, campaign, country and platform. Adjust reports UTC days.
/// </summary>
public sealed class AdjustSyncer(AdjustService adjust, FactStore facts) : IIntegrationSyncer
{
    public const int MaxCohortDay = 30;

    public static readonly string[] DailyMetrics = ["installs", "clicks", "impressions", "sessions", "daus", "cost", "revenue", "ad_revenue", "all_revenue"];
    public static readonly string[] CampaignDimensions = ["day", "partner_name", "campaign_id_network", "campaign_network", "country_code", "os_name"];
    public static readonly string[] CampaignMetrics = ["installs", "clicks", "impressions", "sessions", "cost", "all_revenue"];

    /// <summary>Cohort segments (segment type → Adjust dimensions besides day). "all" is the unsegmented cohort.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> CohortSegments = new Dictionary<string, string[]>
    {
        ["all"] = [],
        ["network"] = ["partner_name"],
        ["campaign"] = ["campaign_network", "campaign_id_network"],
        ["country"] = ["country_code"],
        ["platform"] = ["os_name"],
    };

    public string Integration => Integrations.Adjust;
    public bool IsConfigured => adjust.IsConfigured;
    public string ConfigurationHint => AdjustOptions.Hint;
    // Cohorts keep maturing for 30 days and networks restate cost for a few days, so the last 33 days are re-read.
    public int LookbackDays => MaxCohortDay + 3;
    // Network x campaign x country x platform rows get large; smaller windows keep Adjust queries well inside its time limit.
    public int ChunkDays => 14;

    public async Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct)
    {
        var app = (await adjust.ResolveAppsAsync(externalId, ct).ConfigureAwait(false)).Single();
        return new AccountInfo(app.AppToken, app.Name, adjust.Currency, "UTC");
    }

    public async Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct)
    {
        var token = account.ExternalId;
        var currency = adjust.Currency;
        var result = new WindowResult();
        var scope = new Dictionary<string, object?> { ["project_id"] = projectId, ["app_token"] = token };
        var lastComplete = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        // ---- required: daily totals.
        var daily = await adjust.RunReportAsync(token, from, to, ["day"], DailyMetrics, ct).ConfigureAwait(false);
        var dailyRows = MapDaily(daily, currency);
        foreach (var d in dailyRows.Where(d => d.Installs > 0 || d.Sessions > 0 || d.Cost > 0)) result.Seen(d.Date);
        Warn(result, daily);

        // ---- required: network x campaign x country x platform.
        var campaigns = await adjust.RunReportAsync(token, from, to, CampaignDimensions, CampaignMetrics, ct).ConfigureAwait(false);
        var campaignRows = MapCampaigns(campaigns, currency);
        Warn(result, campaigns);

        // ---- cohorts: "all" is required; a segmented cohort Adjust rejects only warns.
        var metrics = new[] { "installs" }.Concat(Enumerable.Range(0, MaxCohortDay + 1).Select(AdjustService.RetainedMetric)).ToArray();
        var cohortRows = new List<AdjustCohortRow>();
        var cohortTypes = new List<string>();
        foreach (var (segment, dims) in CohortSegments)
        {
            try
            {
                var rep = await adjust.RunReportAsync(token, from, to, ["day", .. dims], metrics, ct).ConfigureAwait(false);
                cohortRows.AddRange(MapCohorts(rep, segment, lastComplete));
                cohortTypes.Add(segment);
            }
            catch (PlatformApiException ex) when (segment != "all")
            {
                result.Warnings.Add($"Adjust retention by {segment} unavailable: {ex.Message}");
            }
        }

        using var c = facts.Db.Open();
        using var tx = c.BeginTransaction();
        result.Add(facts.ReplaceWindow(c, tx, "adjust_daily", scope, "date", from, to, dailyRows), "daily totals");
        result.Add(facts.ReplaceWindow(c, tx, "adjust_campaign_daily", scope, "date", from, to, campaignRows), "campaigns");
        foreach (var segment in cohortTypes)
            result.Add(facts.ReplaceWindow(c, tx, "adjust_cohorts", scope, "cohort_date", from, to,
                cohortRows.Where(x => x.SegmentType == segment).ToArray(), extraWhere: "segment_type=@seg", extraArgs: new { seg = segment }), $"cohorts ({segment})");
        tx.Commit();
        return result;
    }

    private static void Warn(WindowResult result, AdjustReport rep)
    {
        foreach (var w in rep.Warnings.Select(w => $"Adjust: {w}").Where(w => !result.Warnings.Contains(w))) result.Warnings.Add(w);
    }

    public static IReadOnlyList<AdjustDailyRow> MapDaily(AdjustReport rep, string currency) => rep.Rows
        .Select(r => (Ok: AdjustService.TryDay(AdjustReport.Text(r, "day"), out var d), Date: d, Row: r))
        .Where(x => x.Ok)
        .GroupBy(x => x.Date)
        .Select(g =>
        {
            long? L(string m) => g.All(x => AdjustReport.Count(x.Row, m) is null) ? null : g.Sum(x => AdjustReport.Count(x.Row, m) ?? 0);
            decimal? D(string m) => g.All(x => AdjustReport.Number(x.Row, m) is null) ? null : g.Sum(x => AdjustReport.Number(x.Row, m) ?? 0);
            return new AdjustDailyRow(g.Key, L("installs"), L("clicks"), L("impressions"), L("sessions"), L("daus"),
                D("cost"), D("revenue"), D("ad_revenue"), D("all_revenue"), currency);
        })
        .ToArray();

    public static IReadOnlyList<AdjustCampaignDailyRow> MapCampaigns(AdjustReport rep, string currency) => rep.Rows
        .Select(r =>
        {
            if (!AdjustService.TryDay(AdjustReport.Text(r, "day"), out var d)) return null;
            var id = AdjustReport.Text(r, "campaign_id_network");
            if (id is not null && id.Equals("unknown", StringComparison.OrdinalIgnoreCase)) id = null;
            return new AdjustCampaignDailyRow(d,
                AdjustReport.Text(r, "partner_name") ?? "(unknown)", id ?? "", AdjustReport.Text(r, "campaign_network") ?? "(unknown)",
                (AdjustReport.Text(r, "country_code") ?? "(unknown)").ToUpperInvariant(), (AdjustReport.Text(r, "os_name") ?? "(unknown)").ToLowerInvariant(),
                AdjustReport.Count(r, "installs"), AdjustReport.Count(r, "clicks"), AdjustReport.Count(r, "impressions"), AdjustReport.Count(r, "sessions"),
                AdjustReport.Number(r, "cost"), AdjustReport.Number(r, "all_revenue"), currency);
        })
        .Where(x => x is not null).Select(x => x!)
        // Normalising labels (case, unknown ids) can fold two Adjust rows into one key; merge them to honour the primary key.
        .GroupBy(x => (x.Date, x.Network, x.CampaignId, x.CampaignName, x.Country, x.Platform))
        .Select(g => g.Count() == 1 ? g.First() : g.First() with
        {
            Installs = Sum(g.Select(x => x.Installs)), Clicks = Sum(g.Select(x => x.Clicks)), Impressions = Sum(g.Select(x => x.Impressions)),
            Sessions = Sum(g.Select(x => x.Sessions)), Cost = Sum(g.Select(x => x.Cost)), AllRevenue = Sum(g.Select(x => x.AllRevenue)),
        })
        .ToArray();

    /// <summary>Cohort cells for days the cohort has fully reached (immature days are not stored, so they can never read as 0%).</summary>
    public static IReadOnlyList<AdjustCohortRow> MapCohorts(AdjustReport rep, string segment, DateOnly lastComplete)
    {
        var dims = CohortSegments[segment];
        var rows = new List<AdjustCohortRow>();
        foreach (var r in rep.Rows)
        {
            if (!AdjustService.TryDay(AdjustReport.Text(r, "day"), out var cohort)) continue;
            var installs = AdjustReport.Count(r, "installs") ?? 0;
            if (installs <= 0) continue;
            var value = segment switch
            {
                "all" => "",
                "campaign" => AdjustService.WithId(AdjustReport.Text(r, "campaign_network"), AdjustReport.Text(r, "campaign_id_network")),
                "country" => (AdjustReport.Text(r, dims[0]) ?? "(unknown)").ToUpperInvariant(),
                "platform" => (AdjustReport.Text(r, dims[0]) ?? "(unknown)").ToLowerInvariant(),
                _ => AdjustReport.Text(r, dims[0]) ?? "(unknown)",
            };
            for (var n = 0; n <= MaxCohortDay; n++)
            {
                if (!AdjustService.Reached(cohort, n, lastComplete)) break;
                if (AdjustReport.Count(r, AdjustService.RetainedMetric(n)) is { } active)
                    rows.Add(new AdjustCohortRow(segment, value, cohort, n, active, installs));
            }
        }
        return rows.GroupBy(x => (x.SegmentValue, x.CohortDate, x.DayN))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with { ActiveUsers = g.Sum(x => x.ActiveUsers), CohortUsers = g.Sum(x => x.CohortUsers) })
            .ToArray();
    }

    private static long? Sum(IEnumerable<long?> v) { var a = v.ToArray(); return a.All(x => x is null) ? null : a.Sum(x => x ?? 0); }
    private static decimal? Sum(IEnumerable<decimal?> v) { var a = v.ToArray(); return a.All(x => x is null) ? null : a.Sum(x => x ?? 0); }
}
