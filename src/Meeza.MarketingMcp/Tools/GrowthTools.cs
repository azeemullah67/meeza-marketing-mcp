using System.ComponentModel;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Services;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

/// <summary>Tools that join ad data with GA4 player data: can the game work, and is tracking complete.</summary>
[McpServerToolType]
public sealed class GrowthTools(PlatformRegistry registry, AnalyticsService analytics)
{
    [McpServerTool(Name = "get_game_scorecard", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Soft-launch go / no-go scorecard that answers 'can this game work?'. Grades D1/D7/D30 retention (GA4/Firebase cohorts) against industry percentiles, gives a verdict (insufficient_data, fix_the_game, iterate, promising, strong) with reasons and next steps, and puts CPI from the ad platforms next to an estimated LTV (ARPDAU × expected active days from the retention curve) when the currencies match.")]
    public Task<string> GetGameScorecard(
        [Description("GA4 property ID. Omit to use every allowed property.")] string? propertyId = null,
        [Description("GA4 platform filter: android (default), ios, web or all.")] string? platform = "android",
        [Description("Install-cohort dates to judge. Preset, default last_30_days.")] string? datePreset = "last_30_days",
        [Description("Cohort start date yyyy-MM-dd.")] string? startDate = null,
        [Description("Cohort end date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Optional comma-separated ad platforms for CPI: meta, google_ads.")] string? adPlatforms = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_game_scorecard", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            if (!analytics.IsConfigured)
                throw new PlatformNotConfiguredException(AnalyticsOptions.Platform, "The scorecard needs retention from Google Analytics 4 / Firebase. " + AnalyticsOptions.Hint);

            var adTask = registry.CollectAsync(adPlatforms, range, byCountry: false, new ToolResponse { Tool = "ads" }, ct);
            var cards = new List<ScoredProperty>();
            foreach (var p in await analytics.ResolvePropertiesAsync(propertyId, ct))
            {
                var retTask = analytics.GetRetentionAsync(p, range, 30, platform, AnalyticsTools.LastCompleteDay, ct);
                var revTask = analytics.GetRevenueAsync(p, range, platform, ct);
                var ret = await retTask;
                RevenueReport? rev = null;
                try { rev = await revTask; }
                catch (Exception ex) when (ex is not OperationCanceledException) { r.Notes.Add($"Revenue for property {p} could not be read: {ToolRunner.Describe(ex)}"); }

                var c = ret.Curve;
                var card = GameScorecard.Evaluate(
                    RetentionMath.On(c, 1), RetentionMath.UsersOn(c, 1),
                    RetentionMath.On(c, 7), RetentionMath.UsersOn(c, 7),
                    RetentionMath.On(c, 30), RetentionMath.UsersOn(c, 30));
                var arpdau = rev?.TotalRevenue is > 0 ? rev.Arpdau : null;
                var ltv90 = GameScorecard.Ltv(arpdau, ret.Fit, 90);
                cards.Add(new ScoredProperty(p, rev?.Currency, ltv90, new
                {
                    propertyId = p,
                    cohortRange = ret.CohortRange,
                    card.Verdict, card.Headline, card.Provisional,
                    retention = card.Retention,
                    card.Reasons,
                    card.NextSteps,
                    monetisation = new
                    {
                        currency = rev?.Currency,
                        revenue = rev?.TotalRevenue,
                        adRevenue = rev?.AdRevenue,
                        purchaseRevenue = rev?.PurchaseRevenue,
                        arpdau,
                        estimatedLtvD30 = GameScorecard.Ltv(arpdau, ret.Fit, 30),
                        estimatedLtvD90 = ltv90,
                        estimatedLtvD180 = GameScorecard.Ltv(arpdau, ret.Fit, 180),
                        tracked = rev?.TotalRevenue is > 0,
                    },
                }));
            }

            var ads = Aggregation.ByPlatform(await adTask);
            r.Data = new
            {
                scorecards = cards.Select(c => c.Card).ToArray(),
                acquisition = ads.Select(a => new { a.Platform, a.Currency, a.Spend, a.Installs, a.Cpi }).ToArray(),
                payback = cards.SelectMany(c => ads
                    .Where(a => a.Cpi is > 0 && c.LtvD90 is not null && string.Equals(a.Currency, c.Currency, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new
                    {
                        propertyId = c.PropertyId, a.Platform, a.Currency, a.Cpi, estimatedLtvD90 = c.LtvD90,
                        estimatedRoasD90Percent = Math.Round(c.LtvD90!.Value / a.Cpi!.Value * 100, 1),
                    })).ToArray(),
            };
            r.Notes.Add("Verdict rules: below D1 25% (or D7 below the median game) = fix_the_game; D1 ≥ top-25% (and D7 ≥ top-25% when known) = promising; top-10% on both = strong; otherwise iterate. Grades need 100+ users per day point.");
            r.Notes.Add("Percentiles are from GameAnalytics' 2026 report (2025 data, all genres). Publishers often ask for more before scaling a puzzle game (roughly D1 40%, D7 15%).");
            r.Notes.Add("LTV and ROAS are estimates: ARPDAU over the cohort range × expected active days from the fitted retention curve. They are only compared with CPI when the GA4 currency equals the ad account currency; amounts are never converted.");
            r.Notes.Add(AnalyticsTools.FreshnessNote);
        });

    [McpServerTool(Name = "check_analytics_tracking", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Explains why Google Analytics / Firebase shows fewer users than expected: compares GA4 new users per day with installs reported by Meta and Google Ads, lists which app versions send data, flags data thresholds, and returns a diagnosis with the likely causes to check.")]
    public Task<string> CheckAnalyticsTracking(
        [Description("GA4 property ID. Omit to use every allowed property.")] string? propertyId = null,
        [Description("GA4 platform filter: android (default), ios, web or all.")] string? platform = "android",
        [Description("Date preset, default last_14_days.")] string? datePreset = "last_14_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Optional comma-separated ad platforms: meta, google_ads.")] string? adPlatforms = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("check_analytics_tracking", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            if (!analytics.IsConfigured) throw new PlatformNotConfiguredException(AnalyticsOptions.Platform, AnalyticsOptions.Hint);

            var adDaily = await registry.CollectDailyAsync(adPlatforms, range, r, ct);
            // CollectDailyAsync marks the response failed when no ad platform works; GA4 data is still useful.
            r.Ok = true;
            var installsByDay = adDaily.GroupBy(d => d.Date)
                .ToDictionary(g => g.Key, g => g.GroupBy(d => d.Platform).ToDictionary(x => x.Key, x => x.Sum(d => d.Installs ?? 0)));
            var settledEnd = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);

            var results = new List<object>();
            foreach (var p in await analytics.ResolvePropertiesAsync(propertyId, ct))
            {
                var usersTask = analytics.GetActiveUsersAsync(p, range, null, platform, ct);
                var versionsTask = analytics.GetActiveUsersAsync(p, new DateRange(range.End.AddDays(-6), range.End), "app_version", platform, ct);
                var users = await usersTask;
                ActiveUsersReport? versions = null;
                try { versions = await versionsTask; } catch (Exception ex) when (ex is not OperationCanceledException) { }

                var days = users.Daily.Select(d =>
                {
                    var inst = installsByDay.GetValueOrDefault(d.Date) ?? new Dictionary<string, decimal>();
                    var maxPaid = inst.Count == 0 ? 0 : inst.Values.Max();
                    return new
                    {
                        date = d.Date, ga4NewUsers = d.NewUsers, ga4ActiveUsers = d.ActiveUsers,
                        adInstalls = inst, largestPlatformInstalls = maxPaid,
                        ga4VsAdsPercent = maxPaid > 0 ? Math.Round(d.NewUsers * 100m / maxPaid, 0) : (decimal?)null,
                        settled = d.Date <= settledEnd,
                    };
                }).ToArray();

                var comparable = days.Where(d => d.settled && d.largestPlatformInstalls >= 10).ToArray();
                decimal? ratio = comparable.Length == 0 ? null
                    : Math.Round(comparable.Sum(d => (decimal)d.ga4NewUsers) * 100m / comparable.Sum(d => d.largestPlatformInstalls), 0);

                string status, diagnosis;
                if (users.ActiveUsersInRange == 0)
                {
                    status = "critical";
                    diagnosis = "GA4 received no app users at all in this range. The Firebase SDK is probably missing from the live build, the wrong google-services.json is used, or the property/stream is wrong.";
                }
                else if (ratio is null)
                {
                    status = "unknown";
                    diagnosis = "No settled day has 10+ ad-reported installs to compare against yet (GA4 needs ~2-3 days to settle). Check again in a few days.";
                }
                else if (ratio < 50)
                {
                    status = "critical";
                    diagnosis = $"GA4 counted only {ratio}% as many new users as the ad platforms reported installs. New users should be at least equal (GA4 also includes organic). Tracking is losing most players.";
                }
                else if (ratio < 80)
                {
                    status = "warning";
                    diagnosis = $"GA4 counted {ratio}% as many new users as ad-reported installs. Some loss is normal (ad platforms over-attribute, and some installers never open the app), but this gap is larger than usual.";
                }
                else
                {
                    status = "ok";
                    diagnosis = $"GA4 new users are {ratio}% of ad-reported installs, which is normal. If 'active users' still looks low, the players are installing but not coming back: that is retention, not tracking (see ga4_get_retention).";
                }

                results.Add(new
                {
                    propertyId = p, status, diagnosis,
                    ga4VsAdsPercent = ratio,
                    ga4 = new { users.ActiveUsersInRange, users.NewUsersInRange, users.AverageDau, users.StickinessPercent, users.EngagementSecondsPerUser, users.Thresholded },
                    appVersionsLast7Days = versions?.Breakdown,
                    daily = days,
                    checks = new[]
                    {
                        "Firebase → Analytics → DebugView: install the Play build on a phone, open it, and confirm first_open and session_start arrive.",
                        "Every live Play build includes the Firebase Analytics SDK with the right google-services.json (compare appVersionsLast7Days with the versions live in Play).",
                        "Consent: if the app shows a GDPR/consent prompt, users who decline aren't counted unless Consent Mode modelling is enabled.",
                        "GA4 → Admin → Reporting identity → Device-based, to avoid thresholds hiding small numbers.",
                        "The GA4 'Active users' card counts only engaged users; compare 'New users' or the first_open event count with installs instead.",
                        "Ad-platform installs are self-attributed (and can double count between Meta and Google), so a 10-20% gap is normal.",
                    },
                });
            }

            r.Data = results;
            r.Notes.Add("ga4VsAdsPercent = GA4 new users ÷ installs of the ad platform reporting the most installs that day, over settled days (3+ days old) with 10+ installs. Ad installs from different platforms are never added together.");
            r.Notes.Add(AnalyticsTools.FreshnessNote);
        });

    private sealed record ScoredProperty(string PropertyId, string? Currency, decimal? LtvD90, object Card);
}
