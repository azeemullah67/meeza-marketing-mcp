using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>
/// Sorts ads into the stages of a creative-testing pipeline, the way large game studios run UA:
/// test new creatives on small budgets, scale the winners, cut the losers, refresh what's worn out.
/// All rules are relative to the account's own median CPI (per platform and currency), so they work
/// in any market and currency. Thresholds are explicit constants so results can be explained.
/// </summary>
public static class CreativeLab
{
    public const string Testing = "testing", Scale = "scale", Steady = "steady", Refresh = "refresh", Cut = "cut", Untracked = "untracked";

    /// <summary>Installs needed before an ad's CPI is trusted.</summary>
    public const int MinInstalls = 10;
    /// <summary>An ad with fewer installs that has spent this many median CPIs is no longer "testing".</summary>
    public const decimal TestBudgetInMedianCpis = 3m;
    public const decimal WinnerFactor = 0.8m;   // CPI at or below 0.8x median
    public const decimal LoserFactor = 1.5m;    // CPI at or above 1.5x median
    public const decimal ZeroInstallCutInMedianCpis = 2m; // spent 2 median CPIs with zero installs
    public const decimal FatigueFrequency = 3.5m;

    public sealed record Item(
        string Stage, string Reason, string Platform, string Currency,
        string? CampaignName, string? AdSetName, string? AdId, string? AdName, string? Status,
        decimal? Spend, long? Impressions, long? Clicks, decimal? Installs, decimal? Cpi, decimal? Ipm, decimal? Ctr, decimal? Frequency,
        decimal? MedianCpi, string Format);

    public sealed record StageSummary(string Stage, int Count, decimal Spend, decimal SpendSharePercent);

    public sealed record Board(string Currency, decimal? MedianCpi, IReadOnlyList<StageSummary> Stages, IReadOnlyList<Item> Items)
    {
        /// <summary>
        /// Assets inside asset-based ads (e.g. the headlines and videos of a Google App ad), judged by the platform's
        /// own rating. They overlap with each other and with <see cref="Items"/>, so they are never added to stage spend.
        /// </summary>
        public IReadOnlyList<AssetItem> Assets { get; init; } = [];

        /// <summary>Number of assets per stage.</summary>
        public IReadOnlyDictionary<string, int> AssetStages { get; init; } = new Dictionary<string, int>();
    }

    public sealed record AssetItem(
        string Stage, string Reason, string Platform, string Currency,
        string? CampaignName, string? AdSetName, string? AdId,
        string AssetId, string AssetType, string FieldType, string? Label, string? PreviewUrl, string? PerformanceLabel, bool? Enabled,
        decimal? Spend, long? Impressions, long? Clicks, decimal? Installs, decimal? Cpi, decimal? Ctr);

    public static readonly IReadOnlyDictionary<string, string> AssetRules = new Dictionary<string, string>
    {
        [Testing] = "Google is still learning (or has too little data to rate it). Leave it running.",
        [Scale] = "Google rates it Best among assets of the same kind: keep it and make more variations like it.",
        [Steady] = "Google rates it Good: keep it.",
        [Cut] = "Google rates it Low compared with the other assets of the same kind: replace it with a new variation.",
    };

    /// <summary>
    /// Sorts assets by the platform's own rating (Google's LOW / GOOD / BEST). Per-asset installs are too sparse
    /// and overlap too much to judge assets by CPI, so the rating is used instead.
    /// </summary>
    public static (string Stage, string Reason) DecideAsset(string? performanceLabel) =>
        (performanceLabel ?? "").ToUpperInvariant() switch
        {
            "BEST" => (Scale, AssetRules[Scale]),
            "GOOD" => (Steady, AssetRules[Steady]),
            "LOW" => (Cut, AssetRules[Cut]),
            _ => (Testing, AssetRules[Testing]), // LEARNING, PENDING, UNKNOWN, UNSPECIFIED or not reported
        };

    public static IReadOnlyList<AssetItem> ClassifyAssets(IEnumerable<AssetPerformance> assets) =>
        assets.Where(a => a.Enabled != false)
            .Select(a =>
            {
                var (stage, reason) = DecideAsset(a.PerformanceLabel);
                return new AssetItem(stage, reason, a.Platform, a.Currency, a.CampaignName, a.AdSetName, a.AdId,
                    a.AssetId, a.AssetType, a.FieldType, a.Label, a.PreviewUrl, a.PerformanceLabel, a.Enabled,
                    a.Spend, a.Impressions, a.Clicks, a.Installs, a.Cpi, a.Ctr);
            })
            .OrderBy(i => Array.IndexOf(new[] { Cut, Scale, Steady, Testing }, i.Stage))
            .ThenBy(i => i.FieldType)
            .ThenByDescending(i => i.Impressions ?? 0)
            .ToArray();

    /// <summary>Ad board plus the assets inside asset-based ads, matched by currency.</summary>
    public static IReadOnlyList<Board> Classify(IEnumerable<CampaignPerformance> adRows, IEnumerable<AssetPerformance> assets)
    {
        var boards = Classify(adRows).ToList();
        foreach (var g in ClassifyAssets(assets).GroupBy(a => a.Currency))
        {
            var idx = boards.FindIndex(b => b.Currency == g.Key);
            var board = idx >= 0 ? boards[idx] : new Board(g.Key, null, [], []);
            board = board with
            {
                Assets = g.ToArray(),
                AssetStages = new[] { Testing, Scale, Steady, Cut }.ToDictionary(s => s, s => g.Count(a => a.Stage == s)),
            };
            if (idx >= 0) boards[idx] = board; else boards.Add(board);
        }
        return boards;
    }

    public static readonly IReadOnlyDictionary<string, string> Rules = new Dictionary<string, string>
    {
        [Testing] = $"Fewer than {MinInstalls} installs and under {TestBudgetInMedianCpis}x the median CPI spent: not enough data yet. Keep it running on a small budget.",
        [Scale] = $"CPI at or below {WinnerFactor}x the median with {MinInstalls}+ installs: a winner. Move budget here.",
        [Steady] = "CPI between the winner and loser lines: keep it, and keep testing challengers against it.",
        [Refresh] = $"Meta frequency of {FatigueFrequency}+ (the same people see it repeatedly): make new variations before performance drops.",
        [Cut] = $"CPI at or above {LoserFactor}x the median, or {ZeroInstallCutInMedianCpis}x the median CPI spent with zero installs: pause it and reuse the budget.",
        [Untracked] = "Spends money but reports no install tracking (for example search or retargeting): judged on other goals.",
    };

    /// <summary>Guesses the creative format from the ad name, for display only.</summary>
    public static string FormatOf(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("playable")) return "playable";
        if (n.Contains("ugc") || n.Contains("creator") || n.Contains("influencer") || n.Contains("streamer")) return "ugc";
        if (n.Contains("video") || n.Contains("vid ") || n.Contains("youtube")) return "video";
        if (n.Contains("static") || n.Contains("image") || n.Contains("banner") || n.Contains("carousel")) return "static";
        return "other";
    }

    public static IReadOnlyList<Board> Classify(IEnumerable<CampaignPerformance> adRows) =>
        adRows.Where(r => r.Spend > 0)
            .GroupBy(r => r.Currency)
            .Select(g =>
            {
                // Median CPI per platform: platforms attribute differently, so each is judged against itself.
                var medians = g.GroupBy(r => r.Platform).ToDictionary(
                    p => p.Key,
                    p => Insights.Median(p.Where(r => r.Installs >= MinInstalls && r.Cpi is not null).Select(r => r.Cpi!.Value))
                         ?? Insights.Median(p.Where(r => r.Installs >= Insights.MinInstallsForCpi && r.Cpi is not null).Select(r => r.Cpi!.Value)));
                var items = g.Select(r => ToItem(r, medians.GetValueOrDefault(r.Platform))).ToList();
                var total = items.Sum(i => i.Spend ?? 0);
                var stages = new[] { Testing, Scale, Steady, Refresh, Cut, Untracked }
                    .Select(s =>
                    {
                        var spend = items.Where(i => i.Stage == s).Sum(i => i.Spend ?? 0);
                        return new StageSummary(s, items.Count(i => i.Stage == s), spend, total > 0 ? Math.Round(spend / total * 100m, 2) : 0);
                    }).ToArray();
                var overall = Insights.Median(items.Where(i => i.Installs >= MinInstalls && i.Cpi is not null).Select(i => i.Cpi!.Value));
                return new Board(g.Key, overall, stages, items.OrderBy(i => Array.IndexOf(new[] { Scale, Refresh, Cut, Testing, Steady, Untracked }, i.Stage)).ThenByDescending(i => i.Spend).ToArray());
            })
            .OrderByDescending(b => b.Items.Sum(i => i.Spend ?? 0))
            .ToArray();

    private static Item ToItem(CampaignPerformance r, decimal? median)
    {
        decimal? frequency = r is { Reach: > 0, Impressions: > 0 } ? Math.Round((decimal)r.Impressions.Value / r.Reach.Value, 2) : null;
        decimal? ipm = r is { Impressions: > 0, Installs: not null } ? Math.Round(r.Installs.Value / r.Impressions.Value * 1000m, 2) : null;
        var (stage, reason) = Decide(r, median, frequency);
        return new Item(stage, reason, r.Platform, r.Currency, r.CampaignName, r.AdSetName, r.AdId, r.AdName, r.Status,
            r.Spend, r.Impressions, r.Clicks, r.Installs, r.Cpi, ipm, r.Ctr, frequency, median, FormatOf(r.AdName));
    }

    internal static (string Stage, string Reason) Decide(CampaignPerformance r, decimal? median, decimal? frequency)
    {
        var spend = r.Spend ?? 0;
        if (r.Installs is null) return (Untracked, Rules[Untracked]);
        if (median is null or <= 0)
            return r.Installs >= MinInstalls ? (Steady, "No median CPI yet to compare against.") : (Testing, Rules[Testing]);

        if (r.Installs == 0)
            return spend >= median * ZeroInstallCutInMedianCpis
                ? (Cut, $"Spent {spend:0.##} {r.Currency} ({spend / median:0.#}x the median CPI) with zero installs.")
                : (Testing, Rules[Testing]);

        if (r.Installs < MinInstalls && spend < median * TestBudgetInMedianCpis) return (Testing, Rules[Testing]);

        var cpi = r.Cpi ?? spend / r.Installs.Value;
        if (cpi >= median * LoserFactor) return (Cut, $"CPI {cpi:0.##} is {cpi / median:0.#}x the median ({median:0.##}).");
        if (frequency >= FatigueFrequency) return (Refresh, $"Frequency {frequency:0.#}: the audience has seen it {frequency:0.#} times on average.");
        if (cpi <= median * WinnerFactor && r.Installs >= MinInstalls) return (Scale, $"CPI {cpi:0.##} is {median / cpi:0.#}x better than the median ({median:0.##}).");
        return (Steady, Rules[Steady]);
    }
}
