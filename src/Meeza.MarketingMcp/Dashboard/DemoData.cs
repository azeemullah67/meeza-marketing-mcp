using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// Deterministic sample data for previewing the dashboard without connecting any account
/// (`meeza-marketing-mcp dashboard --demo`). Every response is labelled as demo data.
/// </summary>
public static class DemoData
{
    public const string Note = "DEMO DATA: generated sample numbers, not your accounts.";

    private static readonly (string Country, double Weight, double CpiFactor)[] Countries =
        [("PK", .30, .35), ("PH", .22, .45), ("US", .18, 3.2), ("IN", .14, .3), ("BR", .10, .7), ("GB", .06, 2.6)];

    private sealed record DemoAd(string Platform, string Campaign, string AdSet, string Ad, string Status, double Budget, double Ctr, double Cvr);

    private static readonly DemoAd[] Ads =
    [
        new("meta", "Puzzle Quest - Installs - Tier 3", "PK+PH Broad 18-34", "Video - Level 42 fail", "ACTIVE", 14, 1.9, 9.5),
        new("meta", "Puzzle Quest - Installs - Tier 3", "PK+PH Broad 18-34", "Playable - Swipe tutorial", "ACTIVE", 11, 2.4, 11.0),
        new("meta", "Puzzle Quest - Installs - Tier 3", "IN Lookalike 1%", "Static - Rewards chest", "ACTIVE", 7, 1.1, 6.0),
        new("meta", "Puzzle Quest - Installs - Tier 1", "US Interests Puzzle", "Video - Satisfying merges", "ACTIVE", 18, 0.9, 3.2),
        new("meta", "Puzzle Quest - Installs - Tier 1", "US Interests Puzzle", "UGC - Creator review", "PAUSED", 4, 0.6, 0.0),
        new("meta", "Retargeting - Lapsed players", "Lapsed 30d", "Carousel - New levels", "ACTIVE", 3, 1.4, 0.0),
        new("google_ads", "UAC - Installs - Asia", "Ad group 1", "App Ad #8812", "ENABLED", 16, 3.1, 8.0),
        new("google_ads", "UAC - Installs - Asia", "Ad group 2 (video)", "App Ad #8813", "ENABLED", 9, 2.2, 7.1),
        new("google_ads", "UAC - Installs - Tier 1", "Ad group 1", "App Ad #9001", "ENABLED", 13, 1.6, 2.9),
        new("google_ads", "Search - Brand", "Brand exact", "Responsive Search Ad #7710", "ENABLED", 2, 7.5, 0.0),
        new("meta", "Creative Testing - Tier 3", "Test cell PK+PH", "Video - Hook test: 'Can you beat 42?' (new)", "ACTIVE", 1.2, 2.1, 8.0),
        new("meta", "Creative Testing - Tier 3", "Test cell PK+PH", "UGC - Streamer reaction (new)", "ACTIVE", 1.0, 2.6, 9.0),
        new("meta", "Creative Testing - Tier 3", "Test cell PK+PH", "Playable - Merge challenge (new)", "ACTIVE", 0.9, 2.9, 12.0),
    ];

    public static ToolResponse Overview(DateRange range, ReportLevel level, string? platforms)
    {
        var rows = AdRows(range, platforms).ToList();
        var levelRows = level switch
        {
            ReportLevel.Ad => rows,
            ReportLevel.AdSet => Roll(rows, r => (r.Platform, r.CampaignId, r.AdSetId), r => r with { Level = "adset", AdId = null, AdName = null }),
            _ => Roll(rows, r => (r.Platform, r.CampaignId, null), r => r with { Level = "campaign", AdSetId = null, AdSetName = null, AdId = null, AdName = null }),
        };
        var r = new ToolResponse { Tool = "dashboard_overview", DateRange = range.ToString() };
        r.Data = new
        {
            demo = true,
            level = level.Name(),
            platforms = new[] { new { platform = "meta", configured = true, hint = (string?)null }, new { platform = "google_ads", configured = true, hint = (string?)null } },
            totals = Aggregation.ByPlatform(rows),
            rows = levelRows.Select(x => x with { Notes = null }).OrderByDescending(x => x.Spend ?? 0),
            daily = Daily(range, platforms),
            previousRange = range.Previous().ToString(),
            previousTotals = Aggregation.ByPlatform(AdRows(range.Previous(), platforms)),
        };
        r.Notes.Add(Note);
        return r;
    }

    public static IReadOnlyList<CampaignPerformance> AdLevelRows(DateRange range, string? platforms) => AdRows(range, platforms).ToList();

    private static readonly (string Type, string Field, string Label, string Rating, double Share)[] DemoAssets =
    [
        ("video", "YOUTUBE_VIDEO", "Level 42: can you beat it? (15s)", "BEST", .30),
        ("video", "YOUTUBE_VIDEO", "Gameplay montage (30s)", "LOW", .12),
        ("image", "MARKETING_IMAGE", "Rewards chest · 1200×628", "GOOD", .15),
        ("image", "MARKETING_IMAGE", "Logo on purple · 1200×1200", "LEARNING", .04),
        ("text", "HEADLINE", "Can you clear the board?", "BEST", .25),
        ("text", "HEADLINE", "Best puzzle game 2026", "LOW", .10),
        ("text", "HEADLINE", "Slide. Match. Solve.", "GOOD", .18),
        ("text", "DESCRIPTION", "Satisfying merges and 500+ handmade levels. Play free.", "GOOD", .22),
    ];

    /// <summary>Sample assets for the first demo Google App ad.</summary>
    public static IReadOnlyList<AssetPerformance> AssetRows(DateRange range, string? platforms)
    {
        var ad = AdRows(range, platforms).FirstOrDefault(r => r.Platform == "google_ads" && r.Objective == "MULTI_CHANNEL/APP_CAMPAIGN");
        if (ad is null) return [];
        return DemoAssets.Select((a, i) => new AssetPerformance
        {
            Platform = ad.Platform,
            AccountId = ad.AccountId,
            Currency = ad.Currency,
            CampaignId = ad.CampaignId,
            CampaignName = ad.CampaignName,
            AdSetId = ad.AdSetId,
            AdSetName = ad.AdSetName,
            AdId = ad.AdId,
            AssetId = (70000 + i).ToString(),
            AssetType = a.Type,
            FieldType = a.Field,
            Label = a.Label,
            PerformanceLabel = a.Rating,
            Enabled = true,
            Spend = Math.Round((ad.Spend ?? 0) * (decimal)a.Share, 2),
            Impressions = (long)((ad.Impressions ?? 0) * a.Share),
            Clicks = (long)((ad.Clicks ?? 0) * a.Share * (a.Rating == "BEST" ? 1.3 : a.Rating == "LOW" ? 0.6 : 1.0)),
            Installs = ad.Installs is null ? null : Math.Round(ad.Installs.Value * (decimal)(a.Share * (a.Rating == "BEST" ? 1.4 : a.Rating == "LOW" ? 0.5 : 1.0))),
            StartDate = range.Start,
            EndDate = range.End,
        }.WithDerivedMetrics()).ToArray();
    }

    public static ToolResponse CountriesResponse(DateRange range, string? platforms)
    {
        var rows = AdRows(range, platforms).SelectMany(ad => Countries.Select(c => (ad with
        {
            Country = c.Country,
            Spend = Math.Round((ad.Spend ?? 0) * (decimal)c.Weight, 2),
            Impressions = (long)((ad.Impressions ?? 0) * c.Weight),
            Clicks = (long)((ad.Clicks ?? 0) * c.Weight),
            Installs = ad.Installs is null ? null : Math.Round(ad.Installs.Value * (decimal)(c.Weight / c.CpiFactor / 0.82)),
        }).WithDerivedMetrics()));
        var r = new ToolResponse { Tool = "dashboard_countries", DateRange = range.ToString(), Data = Aggregation.ByCountry(rows) };
        r.Notes.Add(Note);
        return r;
    }

    public static ToolResponse Players(DateRange range)
    {
        var days = Enumerable.Range(0, Math.Min(range.Days, 60)).Select(i => range.End.AddDays(-i)).Reverse().ToArray();
        var daily = days.Select((d, i) =>
        {
            var dau = (long)(1800 + 25 * i + 220 * Math.Sin(i / 3.5) + 90 * Noise(i * 5 + 3));
            return new Meeza.Marketing.Analytics.Ga4Daily(d, dau, (long)(dau * 0.28), (long)(dau * 2.1), Math.Round(612m + (decimal)(40 * Noise(i)), 1));
        }).ToArray();
        var card = Tools.GameScorecard.Evaluate(33.4m, 9120, 8.1m, 6480, 2.2m, 1910);
        return new ToolResponse
        {
            Tool = "dashboard_players",
            DateRange = range.ToString(),
            Data = new
            {
                configured = true,
                demo = true,
                properties = new object[]
                {
                    new
                    {
                        propertyId = "123456789", displayName = "Puzzle Quest (demo)",
                        averageDau = Math.Round((decimal)daily.Average(d => d.ActiveUsers), 1), peakDau = daily.Max(d => d.ActiveUsers),
                        activeUsersInRange = 14230L, newUsersInRange = daily.Sum(d => d.NewUsers),
                        stickinessPercent = 14.6m, engagementSecondsPerUser = 618.2m,
                        daily,
                        retention = new[] { (1, 33.4m, 9120L), (3, 17.9m, 8400L), (7, 8.1m, 6480L), (14, 4.6m, 4100L), (30, 2.2m, 1910L) }
                            .Select(x => new { day = x.Item1, percent = (decimal?)x.Item2, users = x.Item3 }),
                        curve = Enumerable.Range(0, 31).Where(d => d <= Math.Max(1, days.Length - 1))
                            .Select(d => new { day = d, percent = (decimal?)(d == 0 ? 100m : Math.Round((decimal)(33.4 * Math.Pow(d, -0.78)), 1)), users = (long)Math.Max(300, 9120 - 260 * d) }),
                        cohorts = days.Take(Math.Max(0, days.Length - 1)).Select((d, i) => new
                        {
                            date = d.ToString("yyyy-MM-dd"), users = daily[i].NewUsers,
                            d1 = (decimal?)Math.Round((decimal)(31 + 4 * Math.Sin(i / 2.7) + 2.5 * Noise(i * 7 + 1)), 1),
                            d7 = i < days.Length - 7 ? (decimal?)Math.Round((decimal)(8 + 1.5 * Noise(i * 3 + 2)), 1) : null,
                        }),
                        scorecard = card,
                    },
                },
            },
        };
    }

    public static ToolResponse Play(DateRange range)
    {
        var days = Enumerable.Range(0, Math.Min(range.Days, 60)).Select(i => range.End.AddDays(-i)).Reverse().ToArray();
        object Daily(double baseRate, double amp, int seed) =>
            days.Select((d, i) => new { date = d, percent = Math.Round((decimal)(baseRate + amp * Math.Sin((i + seed) / 3.0) + amp * 0.4 * Noise(i * 7 + seed)), 3) }).ToArray();
        var r = new ToolResponse
        {
            Tool = "dashboard_play",
            DateRange = range.ToString(),
            Data = new
            {
                configured = true,
                demo = true,
                thresholds = new { crash = 1.09m, anr = 0.47m },
                apps = new object[]
                {
                    new
                    {
                        packageName = "com.example.puzzlequest",
                        displayName = "Puzzle Quest (demo)",
                        crash = new { range = range.ToString(), summary = new { rates = new Dictionary<string, decimal> { ["userPerceivedCrashRatePercent"] = 0.62m, ["crashRatePercent"] = 0.91m } }, daily = Daily(0.62, 0.12, 1) },
                        anr = new { range = range.ToString(), summary = new { rates = new Dictionary<string, decimal> { ["userPerceivedAnrRatePercent"] = 0.53m, ["anrRatePercent"] = 0.74m } }, daily = Daily(0.50, 0.08, 4) },
                        reviews = new
                        {
                            count = 64,
                            average = 3.92m,
                            distribution = new Dictionary<string, int> { ["1"] = 7, ["2"] = 4, ["3"] = 6, ["4"] = 12, ["5"] = 35 },
                            latest = new object[]
                            {
                                new { starRating = 5, author = "Ayesha K.", text = "Addictive and relaxing. Level 40+ gets really clever.", lastModified = DateTimeOffset.UtcNow.AddHours(-5), appVersionName = "2.4.1" },
                                new { starRating = 1, author = "Mark", text = "Game freezes after watching the rewarded ad on level 42.", lastModified = DateTimeOffset.UtcNow.AddHours(-9), appVersionName = "2.4.1" },
                                new { starRating = 4, author = "Jun", text = "Fun but too many ads between levels.", lastModified = DateTimeOffset.UtcNow.AddDays(-1), appVersionName = "2.4.0" },
                                new { starRating = 2, author = "Carlos R.", text = "Lost my progress after the update.", lastModified = DateTimeOffset.UtcNow.AddDays(-2), appVersionName = "2.4.0" },
                            },
                        },
                        storeInstalls = new
                        {
                            latestDateInReports = range.End.AddDays(-2),
                            breakdown = new[] { new { primaryInstalls = new { Column = "Daily User Installs", Value = 1840 * range.Days / 7 } } },
                        },
                        storeInstallsConfigured = true,
                    },
                },
            },
        };
        r.Notes.Add(Note);
        return r;
    }

    // ---------------------------------------------------------------- generators

    private static IEnumerable<CampaignPerformance> AdRows(DateRange range, string? platforms)
    {
        var allowed = platforms?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var days = range.Days;
        for (var i = 0; i < Ads.Length; i++)
        {
            var a = Ads[i];
            if (allowed is { Length: > 0 } && !allowed.Contains(a.Platform)) continue;
            var activeFactor = a.Status == "PAUSED" ? 0.25 : 1.0;
            var drift = 0.8 + 0.4 * Noise(i * 31 + range.Start.DayNumber % 97); // varies by period so comparisons move
            var spend = Math.Round(a.Budget * days * activeFactor * drift, 2);
            var cpm = (a.Platform == "meta" ? 3.1 + Noise(i + 3) : 4.2 + Noise(i + 5)) * (0.9 + 0.2 * Noise(i + range.Start.DayNumber % 53));
            var impressions = (long)(spend / cpm * 1000);
            var clicks = (long)(impressions * a.Ctr / 100);
            decimal? installs = a.Cvr > 0 ? Math.Round((decimal)(clicks * a.Cvr / 100)) : (a.Campaign.StartsWith("Search") ? null : 0);
            yield return new CampaignPerformance
            {
                Platform = a.Platform,
                AccountId = a.Platform == "meta" ? "act_1000000001" : "1234567890",
                AccountName = a.Platform == "meta" ? "Demo Games - Meta" : "Demo Games - Google",
                Level = "ad",
                CampaignId = (100 + Array.IndexOf(Ads.Select(x => x.Campaign).Distinct().ToArray(), a.Campaign)).ToString(),
                CampaignName = a.Campaign,
                CampaignStatus = a.Status,
                AdSetId = (500 + Array.IndexOf(Ads.Select(x => x.Campaign + a.Platform + x.AdSet).Distinct().ToArray(), a.Campaign + a.Platform + a.AdSet)).ToString(),
                AdSetName = a.AdSet,
                AdId = (9000 + i).ToString(),
                AdName = a.Ad,
                Status = a.Status,
                Objective = a.Platform == "meta" ? "OUTCOME_APP_PROMOTION" : "MULTI_CHANNEL/APP_CAMPAIGN",
                Currency = "USD",
                Spend = (decimal)spend,
                Impressions = impressions,
                Reach = a.Platform == "meta" ? (long)(impressions * (0.2 + 0.55 * Noise(i + 11))) : null, // frequency varies per ad (1.3 to 5)
                Clicks = clicks,
                Installs = installs,
                InstallSource = installs is null ? null : a.Platform == "meta" ? "meta_attributed:mobile_app_install" : "google_ads_attributed:DOWNLOAD_conversions",
                StartDate = range.Start,
                EndDate = range.End,
            }.WithDerivedMetrics();
        }
    }

    private static List<CampaignPerformance> Roll(List<CampaignPerformance> rows, Func<CampaignPerformance, (string, string?, string?)> key, Func<CampaignPerformance, CampaignPerformance> shape) =>
        rows.GroupBy(key).Select(g =>
        {
            var first = shape(g.First());
            return (first with
            {
                Spend = g.Sum(x => x.Spend),
                Impressions = g.Sum(x => x.Impressions),
                Reach = null,
                Clicks = g.Sum(x => x.Clicks),
                Installs = g.All(x => x.Installs is null) ? null : g.Sum(x => x.Installs ?? 0),
                Status = g.Any(x => x.Status is "ACTIVE" or "ENABLED") ? first.Platform == "meta" ? "ACTIVE" : "ENABLED" : "PAUSED",
            }).WithDerivedMetrics();
        }).ToList();

    private static object Daily(DateRange range, string? platforms)
    {
        var allowed = platforms?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var result = new List<object>();
        foreach (var (platform, baseSpend, cpi) in new[] { ("meta", 57.0, 0.52), ("google_ads", 40.0, 0.61) })
        {
            if (allowed is { Length: > 0 } && !allowed.Contains(platform)) continue;
            var i = 0;
            for (var d = range.Start; d <= range.End; d = d.AddDays(1), i++)
            {
                var weekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1.12 : 1.0;
                var trend = 1 + 0.004 * i;
                var spend = Math.Round(baseSpend * weekend * trend * (0.88 + 0.24 * Noise(i * 3 + platform.Length)), 2);
                result.Add(new
                {
                    platform,
                    currency = "USD",
                    date = d,
                    spend = (decimal)spend,
                    impressions = (long)(spend / 3.4 * 1000),
                    clicks = (long)(spend / 3.4 * 1000 * 0.019),
                    installs = Math.Round((decimal)(spend / (cpi * (0.85 + 0.3 * Noise(i * 5 + 11))))),
                });
            }
        }
        return result;
    }

    /// <summary>Deterministic pseudo-noise in [0, 1).</summary>
    private static double Noise(int seed)
    {
        var x = Math.Sin(seed * 12.9898 + 78.233) * 43758.5453;
        return x - Math.Floor(x);
    }
}
