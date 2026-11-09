using System.ComponentModel;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Services;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

[McpServerToolType]
public sealed class InsightTools(PlatformRegistry registry)
{
    [McpServerTool(Name = "compare_periods", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Compare a period with the previous period of equal length (e.g. last 7 days vs the 7 days before): spend, installs, CPI, CTR, CPM and IPM per platform with % change, plus the campaigns whose CPI moved most.")]
    public Task<string> ComparePeriods(
        [Description("Optional comma-separated platforms: meta, google_ads.")] string? platforms = null,
        [Description("Date preset for the current period: yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month.")] string? datePreset = "last_7_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("compare_periods", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            var previous = range.Previous();
            r.DateRange = range.ToString();
            var curTask = registry.CollectAsync(platforms, range, byCountry: false, r, ct);
            var prevTask = registry.CollectAsync(platforms, previous, byCountry: false, r, ct);
            await Task.WhenAll(curTask, prevTask);
            var cur = curTask.Result; var prev = prevTask.Result;

            var movers = cur.Where(c => c.Cpi is not null && c.Installs >= Insights.MinInstallsForCpi)
                .Select(c => (c, p: prev.FirstOrDefault(p => p.Platform == c.Platform && p.CampaignId == c.CampaignId && p.Currency == c.Currency)))
                .Where(x => x.p is { Cpi: not null, Installs: >= Insights.MinInstallsForCpi })
                .Select(x => new { platform = x.c.Platform, campaignId = x.c.CampaignId, campaignName = x.c.CampaignName, currency = x.c.Currency, change = Insights.Change(x.c.Cpi, x.p!.Cpi) })
                .OrderByDescending(x => Math.Abs(x.change.ChangePercent ?? 0))
                .Take(10)
                .ToArray();

            r.Data = new
            {
                currentRange = range.ToString(),
                previousRange = previous.ToString(),
                platforms = Insights.Compare(Aggregation.ByPlatform(cur), Aggregation.ByPlatform(prev)),
                biggestCpiMoves = movers,
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution,
                "changePercent is (current - previous) / previous x 100. It is null when either period has no value.",
                "Recent days can still change as platforms finalise attribution, so the latest period may look slightly worse than it ends up."]);
        });

    [McpServerTool(Name = "get_creative_test_board", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Creative-testing board in the style large game studios use: every ad sorted into testing (not enough data), scale (winner, CPI <= 0.8x median), steady, refresh (Meta frequency >= 3.5) or cut (CPI >= 1.5x median, or spend with zero installs), with the reason, spend share per stage, CPI, IPM, CTR and frequency. For Google App campaigns, whose single ad mixes many assets, each board also lists the individual headlines, descriptions, images and videos (assets) sorted by Google's own Low / Good / Best rating.")]
    public Task<string> GetCreativeTestBoard(
        [Description("Optional comma-separated platforms: meta, google_ads.")] string? platforms = null,
        [Description("Optional stage filter: testing, scale, steady, refresh, cut, untracked.")] string? stage = null,
        [Description("Date preset: last_7_days (default), last_14_days, last_30_days, last_N_days, this_month, last_month.")] string? datePreset = "last_7_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_creative_test_board", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var assetResponse = new ToolResponse { Tool = "assets" };
            var rowsTask = registry.CollectLevelAsync(platforms, range, ReportLevel.Ad, r, ct);
            var assetsTask = registry.CollectAssetsAsync(platforms, range, assetResponse, ct);
            var rows = await rowsTask;
            var assets = await assetsTask;
            foreach (var e in assetResponse.Errors.Where(e => r.Errors.All(x => x.Platform != e.Platform)))
                r.Notes.Add($"Asset-level results could not be loaded for {e.Platform}: {e.Message}");
            var boards = CreativeLab.Classify(rows, assets);
            var s = stage?.Trim().ToLowerInvariant();
            r.Data = new
            {
                boards = string.IsNullOrEmpty(s) ? boards : boards.Select(b => b with
                {
                    Items = b.Items.Where(i => i.Stage == s).ToArray(),
                    Assets = b.Assets.Where(a => a.Stage == s).ToArray(),
                }).ToArray(),
                rules = CreativeLab.Rules,
                assetRules = CreativeLab.AssetRules,
            };
            if (assets.Count > 0) r.Notes.Add(ReportingNotes.AssetOverlap);
            r.Notes.Add("Each ad is judged against the median CPI of its own platform and currency, using ads with 10+ installs. Thresholds are rules of thumb, not statistical significance tests; check the reason on each item.");
            r.Notes.Add("Format (video, playable, ugc, static) is guessed from the ad name for display only.");
        });

    [McpServerTool(Name = "get_marketing_alerts", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Things that need attention right now across Meta and Google Ads: spend without installs, CPI outliers (above 2x the median), CPI up or down 20%+, spend swings of 30%+ vs the previous period, and daily anomalies (the latest day's spend, installs or CPI far outside the previous 14 days). Ranked by severity.")]
    public Task<string> GetMarketingAlerts(
        [Description("Level to scan: ad (default), adset or campaign.")] string level = "ad",
        [Description("Optional comma-separated platforms: meta, google_ads.")] string? platforms = null,
        [Description("Date preset: yesterday, last_7_days (default), last_30_days, last_N_days, this_month, last_month.")] string? datePreset = "last_7_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_marketing_alerts", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rowsTask = registry.CollectLevelAsync(platforms, range, ReportLevels.Parse(level), r, ct);
            var prevTask = registry.CollectAsync(platforms, range.Previous(), byCountry: false, new ToolResponse { Tool = "previous" }, ct);
            var dailyTask = registry.CollectDailyAsync(platforms, new DateRange(range.End.AddDays(-(Insights.AnomalyLookbackDays + 1)), range.End), new ToolResponse { Tool = "daily" }, ct);
            var rows = await rowsTask;
            IReadOnlyList<PerformanceTotals>? previous = null;
            try { previous = Aggregation.ByPlatform(await prevTask); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r.Notes.Add("The previous period could not be loaded, so change-based alerts were skipped."); }

            var alerts = Insights.Detect(rows, Aggregation.ByPlatform(rows), previous).ToList();
            try { alerts.AddRange(Insights.DetectDailyAnomalies(await dailyTask)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { r.Notes.Add("Daily series could not be loaded, so anomaly alerts were skipped."); }
            var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2, ["good"] = 3 };
            alerts = alerts.OrderBy(a => order.GetValueOrDefault(a.Severity, 9)).ToList();
            r.Data = new { level = ReportLevels.Parse(level).Name(), previousRange = range.Previous().ToString(), alerts };
            r.Notes.Add("Rules: spend with no installs; CPI above 2x the median of items with 3+ installs; CPI change of 20%+; spend change of 30%+; daily anomaly = latest day beyond median ± 3 robust SDs of the previous 14 days and 30%+ off. Google Play vitals alerts are in play_get_android_vitals.");
            r.Notes.Add(ReportingNotes.PlatformAttribution);
        });
}
