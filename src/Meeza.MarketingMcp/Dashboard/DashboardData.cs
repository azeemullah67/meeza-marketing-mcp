using System.Collections.Concurrent;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.PlayConsole.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Services;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// Builds dashboard payloads from the same read-only platform adapters the MCP tools use.
/// Results are cached briefly so auto-refreshing browsers do not exhaust API rate limits.
/// </summary>
public sealed class DashboardData(PlatformRegistry registry, AnalyticsService analytics)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

    /// <summary>Serve generated sample data instead of calling any platform (dashboard --demo).</summary>
    public bool Demo { get; set; }
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, Lazy<Task<ToolResponse>> Value)> _cache = new();

    /// <summary>Drops every cached view (after connection settings change).</summary>
    public void ClearCache() => _cache.Clear();

    public Task<ToolResponse> OverviewAsync(DateRange range, ReportLevel level, string? platforms, bool refresh, CancellationToken ct) =>
        Demo ? Task.FromResult(DemoData.Overview(range, level, platforms)) : Cached($"overview|{range}|{level}|{platforms}", refresh, async () =>
        {
            var r = new ToolResponse { Tool = "dashboard_overview", DateRange = range.ToString() };
            var rowsTask = registry.CollectLevelAsync(platforms, range, level, r, ct);
            var dailyTask = registry.CollectDailyAsync(platforms, range, r, ct);
            // Previous period of equal length, campaign level only (totals). Its errors are not surfaced:
            // a failed comparison just hides the deltas.
            var previous = range.Previous();
            var prevTask = registry.CollectAsync(platforms, previous, byCountry: false, new ToolResponse { Tool = "previous" }, ct);
            await Task.WhenAll(rowsTask, dailyTask).ConfigureAwait(false);
            IReadOnlyList<PerformanceTotals>? previousTotals = null;
            try { previousTotals = Aggregation.ByPlatform(await prevTask.ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            var rows = rowsTask.Result;

            r.Data = new
            {
                level = level.Name(),
                platforms = registry.AdPlatforms.Select(p => new { platform = p.Platform, configured = p.IsConfigured, hint = p.IsConfigured ? null : p.ConfigurationHint }),
                totals = Aggregation.ByPlatform(rows),
                rows = rows.Select(Slim).OrderByDescending(x => x.spend ?? 0).ToArray(),
                daily = DailyByPlatform(dailyTask.Result),
                previousRange = previous.ToString(),
                previousTotals,
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution]);
            return r;
        });

    /// <summary>Creative-testing board: every ad sorted into test / scale / steady / refresh / cut.</summary>
    public Task<ToolResponse> CreativesAsync(DateRange range, string? platforms, bool refresh, CancellationToken ct) =>
        Cached($"creatives|{range}|{platforms}|{Demo}", refresh, async () =>
        {
            var r = new ToolResponse { Tool = "dashboard_creatives", DateRange = range.ToString() };
            IReadOnlyList<CampaignPerformance> rows;
            IReadOnlyList<AssetPerformance> assets;
            if (Demo)
            {
                rows = DemoData.AdLevelRows(range, platforms);
                assets = DemoData.AssetRows(range, platforms);
            }
            else
            {
                // Assets use their own response so an asset-only failure can't mark the whole board as failed.
                var assetResponse = new ToolResponse { Tool = "assets" };
                var rowsTask = registry.CollectLevelAsync(platforms, range, ReportLevel.Ad, r, ct);
                var assetsTask = registry.CollectAssetsAsync(platforms, range, assetResponse, ct);
                rows = await rowsTask.ConfigureAwait(false);
                assets = await assetsTask.ConfigureAwait(false);
                foreach (var e in assetResponse.Errors.Where(e => r.Errors.All(x => x.Platform != e.Platform)))
                    r.Notes.Add($"Asset-level results could not be loaded for {e.Platform}: {e.Message}");
            }
            r.Data = new { boards = CreativeLab.Classify(rows, assets), rules = CreativeLab.Rules, assetRules = CreativeLab.AssetRules };
            if (assets.Count > 0) r.Notes.Add(ReportingNotes.AssetOverlap);
            if (Demo) r.Notes.Add(DemoData.Note);
            return r;
        });

    public Task<ToolResponse> CountriesAsync(DateRange range, string? platforms, bool refresh, CancellationToken ct) =>
        Demo ? Task.FromResult(DemoData.CountriesResponse(range, platforms)) : Cached($"countries|{range}|{platforms}", refresh, async () =>
        {
            var r = new ToolResponse { Tool = "dashboard_countries", DateRange = range.ToString() };
            var rows = await registry.CollectAsync(platforms, range, byCountry: true, r, ct).ConfigureAwait(false);
            r.Data = Aggregation.ByCountry(rows);
            return r;
        });

    /// <summary>Players (GA4 / Firebase): active users, cohort retention and the game scorecard, per property.</summary>
    public Task<ToolResponse> PlayersAsync(DateRange range, bool refresh, CancellationToken ct) =>
        Demo ? Task.FromResult(DemoData.Players(range)) : Cached($"players|{range}", refresh, async () =>
        {
            var r = new ToolResponse { Tool = "dashboard_players", DateRange = range.ToString() };
            if (!analytics.IsConfigured)
            {
                r.Data = new { configured = false, hint = AnalyticsOptions.Hint, properties = Array.Empty<object>() };
                return r;
            }
            Dictionary<string, string?> names;
            IReadOnlyList<string> ids;
            try
            {
                names = (await analytics.GetPropertiesAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.PropertyId, p => p.DisplayName);
                ids = await analytics.ResolvePropertiesAsync(null, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                r.Errors.Add(new PlatformError(AnalyticsOptions.Platform, ToolRunner.Describe(ex)));
                r.Data = new { configured = true, properties = Array.Empty<object>() };
                return r;
            }
            var list = new List<object>();
            foreach (var id in ids.Take(5))
            {
                try
                {
                    var usersTask = analytics.GetActiveUsersAsync(id, range, null, null, ct);
                    var retTask = analytics.GetRetentionAsync(id, range, 30, null, AnalyticsTools.LastCompleteDay, ct);
                    var users = await usersTask.ConfigureAwait(false);
                    var ret = await retTask.ConfigureAwait(false);
                    var c = ret.Curve;
                    var card = Tools.GameScorecard.Evaluate(RetentionMath.On(c, 1), RetentionMath.UsersOn(c, 1),
                        RetentionMath.On(c, 7), RetentionMath.UsersOn(c, 7), RetentionMath.On(c, 30), RetentionMath.UsersOn(c, 30));
                    list.Add(new
                    {
                        propertyId = id, displayName = names.GetValueOrDefault(id),
                        users.AverageDau, users.PeakDau, users.ActiveUsersInRange, users.NewUsersInRange, users.StickinessPercent, users.EngagementSecondsPerUser,
                        daily = users.Daily,
                        retention = new[] { 1, 3, 7, 14, 30 }.Select(d => new { day = d, percent = RetentionMath.On(c, d), users = RetentionMath.UsersOn(c, d) }),
                        // Full Day 0-30 curve and per-install-date cohorts, for the retention charts.
                        curve = c.Where(x => x.RetentionPercent is not null && x.CohortUsers > 0)
                            .Select(x => new { day = x.Day, percent = x.RetentionPercent, users = x.CohortUsers }),
                        cohorts = ret.Cohorts.OrderBy(x => x.Cohort)
                            .Select(x => new { date = x.Cohort.ToString("yyyy-MM-dd"), users = x.Users, d1 = x.D1, d7 = x.D7 }),
                        scorecard = card,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.Errors.Add(new PlatformError(AnalyticsOptions.Platform, $"{id}: {ToolRunner.Describe(ex)}"));
                }
            }
            r.Data = new { configured = true, properties = list };
            r.Notes.Add(AnalyticsTools.FreshnessNote);
            return r;
        });

    public Task<ToolResponse> PlayAsync(DateRange range, bool refresh, CancellationToken ct) =>
        Demo ? Task.FromResult(DemoData.Play(range)) : Cached($"play|{range}", refresh, async () =>
        {
            var r = new ToolResponse { Tool = "dashboard_play", DateRange = range.ToString() };
            var play = registry.Play;
            if (!play.IsConfigured)
            {
                r.Data = new { configured = false, hint = PlayConsoleOptions.Hint, apps = Array.Empty<object>() };
                return r;
            }

            var apps = new List<object>();
            foreach (var pkg in await play.ResolvePackagesAsync(null, ct).ConfigureAwait(false))
                apps.Add(await PlayAppAsync(pkg, range, r, ct).ConfigureAwait(false));
            r.Data = new
            {
                configured = true,
                thresholds = new { crash = PlayConsoleTools.CrashThresholdPercent, anr = PlayConsoleTools.AnrThresholdPercent },
                apps,
            };
            return r;
        });

    private async Task<object> PlayAppAsync(string pkg, DateRange range, ToolResponse r, CancellationToken ct)
    {
        var play = registry.Play;
        var apps = await play.GetAppsAsync(ct).ConfigureAwait(false);

        async Task<T?> Try<T>(string what, Func<Task<T>> f) where T : class
        {
            try { return await f().ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (r) r.Errors.Add(new PlatformError(PlayConsoleOptions.Platform, $"{pkg} {what}: {ToolRunner.Describe(ex)}"));
                return null;
            }
        }

        var crashTask = Try("crash rate", async () =>
        {
            var (eff, rows, _) = await play.QueryVitalsAsync(pkg, "crashRateMetricSet", range, ["userPerceivedCrashRate", "crashRate", "distinctUsers"], [], ct);
            return new { range = eff.ToString(), summary = PlayReports.SummarizeVitals(rows, ["userPerceivedCrashRate", "crashRate"]).FirstOrDefault(), daily = DailyRate(rows, "userPerceivedCrashRate") };
        });
        var anrTask = Try("ANR rate", async () =>
        {
            var (eff, rows, _) = await play.QueryVitalsAsync(pkg, "anrRateMetricSet", range, ["userPerceivedAnrRate", "anrRate", "distinctUsers"], [], ct);
            return new { range = eff.ToString(), summary = PlayReports.SummarizeVitals(rows, ["userPerceivedAnrRate", "anrRate"]).FirstOrDefault(), daily = DailyRate(rows, "userPerceivedAnrRate") };
        });
        var reviewsTask = Try("reviews", async () =>
        {
            var reviews = await play.Client.GetReviewsAsync(pkg, 500, null, ct);
            var stars = reviews.Select(PlayReports.Stars).Where(s => s is not null).Select(s => s!.Value).ToList();
            return new
            {
                count = stars.Count,
                average = stars.Count > 0 ? MetricMath.Round((decimal)stars.Average()) : (decimal?)null,
                distribution = Enumerable.Range(1, 5).ToDictionary(s => s.ToString(), s => stars.Count(x => x == s)),
                latest = reviews.Take(8).Select(PlayReports.ToReview).ToArray(),
            };
        });
        var installsTask = play.Client.Options.ReportsBucket is null
            ? Task.FromResult<object?>(null)
            : Try<object>("store installs", () => play.GetStoreInstallsAsync(pkg, range, byCountry: false, ct));

        await Task.WhenAll(crashTask, anrTask, reviewsTask, installsTask).ConfigureAwait(false);
        return new
        {
            packageName = pkg,
            displayName = apps.FirstOrDefault(a => a.PackageName == pkg)?.DisplayName,
            crash = crashTask.Result,
            anr = anrTask.Result,
            reviews = reviewsTask.Result,
            storeInstalls = installsTask.Result,
            storeInstallsConfigured = play.Client.Options.ReportsBucket is not null,
        };
    }

    private static object[] DailyRate(IReadOnlyList<PlayReports.VitalsRow> rows, string metric) =>
        rows.OrderBy(x => x.Date)
            .Select(x => (object)new { date = x.Date, percent = x.Metrics.GetValueOrDefault(metric) is { } v ? MetricMath.Round(v * 100m) : (decimal?)null })
            .ToArray();

    private static object DailyByPlatform(IReadOnlyList<DailyPerformance> daily) =>
        daily.GroupBy(d => (d.Platform, d.Currency, d.Date))
            .Select(g => new
            {
                platform = g.Key.Platform,
                currency = g.Key.Currency,
                date = g.Key.Date,
                spend = g.Aggregate((decimal?)null, (a, x) => MetricMath.Sum(a, x.Spend)),
                impressions = g.Aggregate((long?)null, (a, x) => MetricMath.Sum(a, x.Impressions)),
                clicks = g.Aggregate((long?)null, (a, x) => MetricMath.Sum(a, x.Clicks)),
                installs = g.Aggregate((decimal?)null, (a, x) => MetricMath.Sum(a, x.Installs)),
            })
            .OrderBy(x => x.date)
            .ToArray();

    private static SlimRow Slim(CampaignPerformance p) => new(
        p.Platform, p.AccountId, p.AccountName, p.Level, p.CampaignId, p.CampaignName, p.AdSetId, p.AdSetName, p.AdId, p.AdName,
        p.Status ?? p.CampaignStatus, p.Objective, p.Currency, p.Spend, p.Impressions, p.Reach, p.Clicks, p.Installs, p.InstallSource,
        p.Ctr, p.Cpc, p.Cpm, p.Cpi);

    /// <summary>Row shape for the browser table (notes are dropped; the page explains install semantics once).</summary>
    public sealed record SlimRow(
        string platform, string accountId, string? accountName, string level,
        string? campaignId, string? campaignName, string? adSetId, string? adSetName, string? adId, string? adName,
        string? status, string? objective, string currency,
        decimal? spend, long? impressions, long? reach, long? clicks, decimal? installs, string? installSource,
        decimal? ctr, decimal? cpc, decimal? cpm, decimal? cpi);

    private async Task<ToolResponse> Cached(string key, bool refresh, Func<Task<ToolResponse>> factory)
    {
        var now = DateTimeOffset.UtcNow;
        if (!refresh && _cache.TryGetValue(key, out var hit) && now - hit.At < CacheTtl)
        {
            var cached = await hit.Value.Value.ConfigureAwait(false);
            if (cached.Errors.Count == 0) return cached; // never serve a cached failure
        }

        var entry = (now, new Lazy<Task<ToolResponse>>(factory));
        _cache[key] = entry;
        try
        {
            return await entry.Item2.Value.ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }
}
