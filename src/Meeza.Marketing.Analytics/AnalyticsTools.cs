using System.ComponentModel;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.Analytics;

/// <summary>Read-only MCP tools for Google Analytics 4 (Firebase Analytics for apps).</summary>
[McpServerToolType]
public sealed class AnalyticsTools(AnalyticsService service)
{
    private const string PropertyHelp = "GA4 property ID (digits, from GA4 → Admin → Property details). Omit to use every allowed property.";
    private const string PlatformHelp = "Optional: android, ios, web or all (default).";
    private const string DateHelp = "Date preset: yesterday, last_7_days, last_14_days, last_30_days (default), last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    public const string FreshnessNote =
        "GA4 app data can take 24-48 hours to finish processing, so the last 1-2 days are usually incomplete and look lower than they will end up.";

    /// <summary>Last day whose data counts as complete for cohort maturity (yesterday, local time).</summary>
    public static DateOnly LastCompleteDay => DateOnly.FromDateTime(DateTime.Now).AddDays(-1);

    [McpServerTool(Name = "ga4_get_properties", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List the Google Analytics 4 / Firebase properties this server can read, and the service account email that needs Viewer access.")]
    public Task<string> GetProperties(CancellationToken ct) =>
        ToolRunner.RunAsync("ga4_get_properties", async r =>
        {
            var props = await service.GetPropertiesAsync(ct);
            r.Data = new { serviceAccount = service.SafeEmail(), usesPlayKey = service.Client.Options.UsesPlayKey, properties = props };
            if (props.Count == 0)
                r.Notes.Add("No properties are visible. Add the service account email as a Viewer in GA4 → Admin → Property access management.");
        });

    [McpServerTool(Name = "ga4_get_active_users", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Daily active users (DAU), new users, sessions and engagement time per user from Google Analytics 4 / Firebase, plus unique users in the range, average and peak DAU and stickiness (average DAU ÷ users in range; DAU/MAU over a 28-31 day range). Optional breakdown by country, app_version, platform, source, medium, campaign, device or os_version.")]
    public Task<string> GetActiveUsers(
        [Description(PropertyHelp)] string? propertyId = null,
        [Description("Optional breakdown: country, app_version, platform, source, medium, campaign, device, os_version.")] string? breakdown = null,
        [Description(PlatformHelp)] string? platform = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("ga4_get_active_users", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<ActiveUsersReport>();
            foreach (var p in await service.ResolvePropertiesAsync(propertyId, ct))
                data.Add(await service.GetActiveUsersAsync(p, range, breakdown, platform, ct));
            r.Data = data;
            r.Notes.Add(FreshnessNote);
            r.Notes.Add("GA4 'active users' only counts users with an engaged session or a first_open/first_visit event. Players who open the app for a few seconds can be missing, so GA4 is usually lower than Play Console's device counts.");
            if (data.Any(d => d.Thresholded))
                r.Notes.Add("GA4 applied data thresholds (small counts hidden for privacy, usually because Google signals is on). Set Admin → Reporting identity to Device-based to reduce this.");
        });

    [McpServerTool(Name = "ga4_get_retention", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Cohort retention from Google Analytics 4 / Firebase: players grouped by first-open day, and the % still active on day 1, 3, 7, 14, 30 etc. Returns the blended curve (each day uses only cohorts old enough to have reached it), per-cohort D1/D3/D7/D14/D30, and a fitted retention curve. This is the key signal for whether a game can work.")]
    public Task<string> GetRetention(
        [Description(PropertyHelp)] string? propertyId = null,
        [Description(PlatformHelp)] string? platform = null,
        [Description("Install (first-open) dates to include. Preset, default last_30_days.")] string? datePreset = "last_30_days",
        [Description("Cohort start date yyyy-MM-dd.")] string? startDate = null,
        [Description("Cohort end date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Last day offset to report, 1-60 (default 30).")] int maxDay = 30,
        [Description("Optional app version filter, e.g. 1.1.23, or a comma-separated list (1.1.22,1.1.23). Use it to compare builds, e.g. a new tutorial.")] string? appVersion = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("ga4_get_retention", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<RetentionReport>();
            foreach (var p in await service.ResolvePropertiesAsync(propertyId, ct))
                data.Add(await service.GetRetentionAsync(p, range, maxDay, platform, LastCompleteDay, ct, appVersion));
            r.Data = data.Select(d => new
            {
                d.PropertyId, d.CohortRange, d.LastCompleteDay, d.MaxDay,
                headline = new
                {
                    d1 = RetentionMath.On(d.Curve, 1), d3 = RetentionMath.On(d.Curve, 3), d7 = RetentionMath.On(d.Curve, 7),
                    d14 = RetentionMath.On(d.Curve, 14), d30 = RetentionMath.On(d.Curve, 30),
                    d1Users = RetentionMath.UsersOn(d.Curve, 1), d7Users = RetentionMath.UsersOn(d.Curve, 7),
                },
                curve = d.Curve,
                cohorts = d.Cohorts,
                fittedCurve = d.Fit is null ? null : new
                {
                    formula = "R(day) = a × day^b",
                    a = Math.Round(d.Fit.A, 4), b = Math.Round(d.Fit.B, 4), points = d.Fit.Points,
                    predictedD30 = Math.Round(d.Fit.At(30) * 100, 2), predictedD90 = Math.Round(d.Fit.At(90) * 100, 2),
                    expectedActiveDays30 = Math.Round(RetentionMath.ExpectedActiveDays(d.Fit, 30), 2),
                    expectedActiveDays90 = Math.Round(RetentionMath.ExpectedActiveDays(d.Fit, 90), 2),
                },
                d.Thresholded,
            }).ToArray();
            r.Notes.Add("Retention % = cohort users active on day N ÷ users whose first session was on the cohort day. null = no cohort has reached that day yet.");
            r.Notes.Add("Fewer than ~100 users per day point makes retention noisy; a single cohort can swing by 10+ points.");
            if (!string.IsNullOrWhiteSpace(appVersion))
                r.Notes.Add($"Filtered to app version {appVersion}: a player counts on a day only for activity on that version, so players who updated in or out of it mid-cohort are partly counted. Use cohorts that start after the build went live.");
            r.Notes.Add("predicted* and expectedActiveDays* are estimates from the fitted curve, not observed data.");
            r.Notes.Add(FreshnessNote);
        });

    [McpServerTool(Name = "ga4_get_level_progression", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Level progression funnel from the game's own GA4 / Firebase events: per level, how many distinct players started it, attempts per player, first-move rate, win/loss/quit counts, win rate, % of level-1 players who reached it, and the drop to the next level. Finds the level where most players quit (early = difficulty or confusion; late = boredom or no hook). Needs level_start / level_end(success) / level_quit events with mode and level_name registered as custom dimensions.")]
    public Task<string> GetLevelProgression(
        [Description(PropertyHelp)] string? propertyId = null,
        [Description("Game mode to list levels for: campaign (default), clear-board, daily or lesson. All modes are summarised regardless.")] string? mode = "campaign",
        [Description(PlatformHelp)] string? platform = null,
        [Description("Optional app version filter, e.g. 1.1.23, or a comma-separated list. Compare builds.")] string? appVersion = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("ga4_get_level_progression", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var chosenMode = string.IsNullOrWhiteSpace(mode) ? "campaign" : mode.Trim();
            var data = new List<object>();
            foreach (var p in await service.ResolvePropertiesAsync(propertyId, ct))
            {
                var d = await service.GetLevelProgressionAsync(p, range, platform, appVersion, chosenMode, ct);
                var first = d.Levels.FirstOrDefault();
                var drop = ProgressionMath.BiggestDrop(d.Levels);
                data.Add(new
                {
                    d.PropertyId, d.Range, d.Mode, d.NewUsers, d.TutorialCompleted,
                    summary = new
                    {
                        playersStartedFirstLevel = first?.PlayersStarted ?? 0,
                        playersWonFirstLevel = first?.PlayersWon ?? 0,
                        reachedLevel5 = ProgressionMath.PlayersAt(d.Levels, "5"),
                        reachedLevel10 = ProgressionMath.PlayersAt(d.Levels, "10"),
                        reachedLevel20 = ProgressionMath.PlayersAt(d.Levels, "20"),
                        reachedLevel50 = ProgressionMath.PlayersAt(d.Levels, "50"),
                        deepestLevelPlayed = d.Levels.LastOrDefault()?.Level,
                        biggestDrop = drop is null ? null : new
                        {
                            afterLevel = drop.Level, drop.PlayersStarted, drop.DropToNextPercent, drop.WinRatePercent, drop.AttemptsPerPlayer,
                            meaning = Meaning(drop),
                        },
                    },
                    levels = d.Levels,
                    modes = d.Modes,
                    coverage = new
                    {
                        d.UnattributedLevelEvents, d.UnattributedLevelPlayers,
                        successTracked = d.SuccessSource is not null, d.SuccessSource,
                    },
                    d.Thresholded, d.DataLoss,
                });
                if (d.UnattributedLevelEvents > 0)
                    r.Notes.Add($"{d.UnattributedLevelEvents} level events ({d.UnattributedLevelPlayers} players on level_start) have no level_name and are excluded from the per-level table. That happens for events sent before mode/level_name were registered as custom dimensions, or by app versions that do not send level_name, so the per-level numbers cover only attributed events.");
                if (d.SuccessSource is null && d.Levels.Count > 0)
                    r.Notes.Add("level_end events carry no readable success value, so wins and losses are unknown (winRatePercent null). A numeric parameter registered as a custom *dimension* arrives empty; register 'success' as an event-scoped custom *metric* in GA4 instead (data accrues from registration).");
                else if (d.SuccessSource == "metric")
                    r.Notes.Add("Wins/losses come from the 'success' custom metric: they are event counts (wins, losses), not distinct players, so playersWon/playersLost are 0.");
                if (d.Levels.Count == 0)
                    r.Notes.Add($"No level events for mode '{chosenMode}' in this range. Available modes: {string.Join(", ", d.Modes.Select(m => m.Mode))}. If none, check that level_start/level_end are being logged and that mode and level_name are registered as event-scoped custom dimensions (data only accrues from the registration date).");
            }
            r.Data = data;
            r.Notes.Add("Players are distinct users in the date range (GA4 totalUsers); attempts are level_start events. reachPercent = players who started this level ÷ players who started the first listed level.");
            r.Notes.Add("dropToNextPercent = players who started this level but never started the next one in the range. Players still mid-progress on the last day inflate the drop for the latest levels they reached; read the early levels first.");
            r.Notes.Add("Low win rate + many attempts per player = difficulty wall. High win rate + high drop = players win and leave: boredom, no next-level pull, or an interstitial/upsell in the way.");
            r.Notes.Add(FreshnessNote);
        });

    private static string Meaning(LevelStat l) =>
        l.WinRatePercent is null && l.AttemptsPerPlayer is > 1.5m
            ? "Players restart this level repeatedly and many never reach the next one. Win/loss is not tracked (no success value), so this could be a difficulty wall or players replaying; check the 'success' custom dimension."
            : l.WinRatePercent is null
            ? "Players leave after this level, but win/loss is not tracked (no success value), so difficulty vs boredom cannot be separated yet."
            : l.WinRatePercent is < 50 && l.AttemptsPerPlayer is > 1.5m
            ? "Players are failing this level repeatedly and giving up: difficulty wall. Ease it, add a hint, or give more moves."
            : l.PlayersFirstMove < l.PlayersStarted * 0.8m
            ? "Many players open this level and never make a move: confusion or a UI block at level start."
            : l.WinRatePercent is >= 70
            ? "Players beat this level and then stop: not difficulty. Check what happens after the win (interstitial, prompt, no clear next step) and whether the game has shown anything new by this point."
            : "Mixed signals: some losses and some quits after wins. Look at level_quit vs level_end for this level.";

    [McpServerTool(Name = "ga4_get_revenue", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Revenue Google Analytics 4 / Firebase has recorded (in-app purchases, and ad revenue when AdMob or another ad network is linked), user-days, and ARPDAU (revenue per daily active user) in the property's currency.")]
    public Task<string> GetRevenue(
        [Description(PropertyHelp)] string? propertyId = null,
        [Description(PlatformHelp)] string? platform = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("ga4_get_revenue", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<RevenueReport>();
            foreach (var p in await service.ResolvePropertiesAsync(propertyId, ct))
                data.Add(await service.GetRevenueAsync(p, range, platform, ct));
            r.Data = data;
            r.Notes.Add("Ad revenue only appears if AdMob is linked to Firebase or the app logs ad_impression events with value (e.g. from AppLovin MAX / LevelPlay). A revenue of 0 can mean 'not tracked', not 'no revenue'.");
            r.Notes.Add("Amounts are in the GA4 property's currency and are never converted or combined with ad-spend currencies.");
        });
}
