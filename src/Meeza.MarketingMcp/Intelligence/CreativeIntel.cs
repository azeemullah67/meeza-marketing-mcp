using System.Text.RegularExpressions;
using Meeza.Marketing.Shared.Models;

namespace Meeza.MarketingMcp.Intelligence;

public sealed record CreativeItem(
    string Key, string Platform, string CreativeId, string Name, string Format, string CampaignId, string? CampaignName, string? Status,
    string? ThumbnailUrl, string? PreviewUrl, decimal? DurationSeconds, string? Title, string? Body, string? PlatformLabel,
    IReadOnlyDictionary<DateOnly, Perf> Daily, decimal? AvgFrequency)
{
    public string? FieldType { get; init; }
}

public sealed record ScoredCreative(
    CreativeItem Item, Perf Total, decimal? Score, string Classification, string Confidence, IReadOnlyList<string> Reasons, bool Fatigued, string? FatigueEvidence)
{
    public object ToJson() => new
    {
        key = Item.Key, platform = Item.Platform, creativeId = Item.CreativeId, name = Item.Name, format = Item.Format, fieldType = Item.FieldType,
        campaignId = Item.CampaignId, campaignName = Item.CampaignName, status = Item.Status, thumbnailUrl = Item.ThumbnailUrl, previewUrl = Item.PreviewUrl,
        durationSeconds = Item.DurationSeconds, title = Item.Title, body = Item.Body, platformLabel = Item.PlatformLabel, frequency = Item.AvgFrequency,
        metrics = Total.ToJson(), score = Score, classification = Classification, confidence = Confidence, reasons = Reasons,
        fatigued = Fatigued, fatigueEvidence = FatigueEvidence, theme = CreativeIntel.Theme(Item), hook = CreativeIntel.Hook(Item),
        d1 = (decimal?)null, d7 = (decimal?)null, retentionNote = "Unavailable: creative is not passed to GA4.",
        history = Item.Daily.OrderBy(kv => kv.Key).Select(kv => new { date = kv.Key, spend = MetricMath.Round(kv.Value.Spend), impressions = kv.Value.Impressions, ctr = kv.Value.Ctr, ipm = kv.Value.Ipm, cpi = kv.Value.Cpi, installs = kv.Value.Installs }),
    };
}

/// <summary>
/// Creative scoring. Creatives are ranked on a composite of cost efficiency (CPI, 50%), install rate (IPM, 35%) and
/// CTR (15%), each relative to the median of comparable creatives (same platform and format). CTR alone never ranks a
/// creative, and creatives below a minimum volume are labelled "testing" instead of being judged.
/// </summary>
public static class CreativeIntel
{
    public const long MinImpressions = 1000;
    public const decimal MinInstallsForConfidence = 10;

    public static readonly object Rules = new
    {
        score = "100 = median creative of the same platform and format. Score = 50% CPI efficiency (median CPI ÷ CPI) + 35% IPM (IPM ÷ median) + 15% CTR (CTR ÷ median), each capped at 3×.",
        best = "Score ≥ 120 with ≥ 10 installs.",
        underperforming = "Score ≤ 70 after spending at least 2× the median CPI.",
        fatigue = "Over its active days, CTR in the second half fell ≥ 25% vs the first half AND IPM fell ≥ 25% or frequency is ≥ 3, with ≥ 3,000 impressions.",
        testing = $"Fewer than {MinImpressions:N0} impressions or fewer than 5 installs: not enough data to judge.",
        neverCtrOnly = "CTR is only 15% of the score: a high-CTR creative that does not produce installs cheaply is not a winner.",
    };

    public static IReadOnlyList<ScoredCreative> Score(IReadOnlyList<CreativeItem> items, DateOnly today)
    {
        var totals = items.ToDictionary(i => i.Key, i => Perf.Sum(i.Daily.Values));
        var result = new List<ScoredCreative>();
        foreach (var group in items.GroupBy(i => (i.Platform, i.Format == "text" ? "text" : "visual")))
        {
            var eligible = group.Where(i => totals[i.Key].Impressions >= MinImpressions && totals[i.Key].Installs is not null && totals[i.Key].Spend > 0).ToArray();
            var medCpi = Stats.Median(eligible.Where(i => totals[i.Key].Installs > 0).Select(i => totals[i.Key].Cpi!.Value));
            var medIpm = Stats.Median(eligible.Select(i => totals[i.Key].Ipm ?? 0).Where(x => x > 0));
            var medCtr = Stats.Median(eligible.Select(i => totals[i.Key].Ctr ?? 0).Where(x => x > 0));
            foreach (var item in group)
            {
                var t = totals[item.Key];
                var reasons = new List<string>();
                var (fatigued, evidence) = Fatigue(item);
                decimal? score = null;
                var isEligible = eligible.Contains(item);
                if (isEligible && medCpi is > 0 && medIpm is > 0 && medCtr is > 0)
                {
                    var cpiPart = t.Installs > 0 && t.Cpi is > 0 ? Math.Min(3m, medCpi.Value / t.Cpi.Value) : 0m;
                    var ipmPart = Math.Min(3m, (t.Ipm ?? 0) / medIpm.Value);
                    var ctrPart = Math.Min(3m, (t.Ctr ?? 0) / medCtr.Value);
                    score = Math.Round(100m * (0.5m * cpiPart + 0.35m * ipmPart + 0.15m * ctrPart), 0);
                    reasons.Add($"CPI {Fmt(t.Cpi)} vs median {Fmt(medCpi)}; IPM {Fmt(t.Ipm)} vs {Fmt(medIpm)}; CTR {Fmt(t.Ctr)}% vs {Fmt(medCtr)}%.");
                }
                string cls;
                if (t.Installs is null && t.Impressions > 0) { cls = "no_install_data"; reasons.Add("No install tracking for this creative; judged on delivery only."); }
                else if (!isEligible || t.Installs < 5) { cls = "testing"; reasons.Add($"Only {t.Impressions:N0} impressions and {t.Installs ?? 0:0.#} installs: keep testing before judging."); }
                else if (fatigued) cls = "fatigued";
                else if (score >= 120 && t.Installs >= MinInstallsForConfidence) cls = "best";
                else if (score <= 70 && medCpi is not null && t.Spend >= medCpi * 2) cls = "underperforming";
                else cls = "steady";
                if (fatigued && evidence is not null) reasons.Add(evidence);
                var confidence = t.Installs >= 30 ? "high" : t.Installs >= MinInstallsForConfidence ? "medium" : "low";
                result.Add(new ScoredCreative(item, t, score, cls, confidence, reasons, fatigued, evidence));
            }
        }
        return result.OrderByDescending(r => r.Score ?? -1).ThenByDescending(r => r.Total.Spend).ToArray();
    }

    /// <summary>Second half of the creative's active days vs the first half.</summary>
    public static (bool Fatigued, string? Evidence) Fatigue(CreativeItem item)
    {
        var days = item.Daily.Where(kv => kv.Value.Impressions > 0).OrderBy(kv => kv.Key).ToArray();
        var total = days.Sum(d => d.Value.Impressions);
        if (days.Length < 6 || total < 3000) return (false, null);
        var half = days.Length / 2;
        var a = Perf.Sum(days.Take(half).Select(d => d.Value));
        var b = Perf.Sum(days.Skip(days.Length - half).Select(d => d.Value));
        var ctrDrop = Stats.PctChange(b.Ctr, a.Ctr);
        var ipmDrop = Stats.PctChange(b.Ipm, a.Ipm);
        var highFreq = item.AvgFrequency >= 3;
        if (ctrDrop <= -25 && (ipmDrop <= -25 || highFreq))
            return (true, $"CTR {Fmt(a.Ctr)}% → {Fmt(b.Ctr)}% ({ctrDrop:0}%), IPM {Fmt(a.Ipm)} → {Fmt(b.Ipm)}{(highFreq ? $", frequency {item.AvgFrequency}" : "")} between the first and second half of its {days.Length} active days.");
        return (false, null);
    }

    private static readonly string[] GameplayWords = ["gameplay", "level", "move", "moves", "puzzle", "solve", "clear", "board", "match", "slide", "challenge", "combo", "satisfying", "asmr", "fail", "can you"];
    private static readonly string[] PromoWords = ["free", "download", "install", "now", "sale", "offer", "new", "update", "play on", "available", "rated", "#1", "best"];

    /// <summary>Heuristic theme from the creative's name, title and text: gameplay, promotional or mixed. A hypothesis, not a fact.</summary>
    public static string Theme(CreativeItem i)
    {
        var text = $" {i.Name} {i.Title} {i.Body} ".ToLowerInvariant();
        var g = GameplayWords.Count(w => text.Contains(w));
        var p = PromoWords.Count(w => Regex.IsMatch(text, $@"\b{Regex.Escape(w)}\b"));
        return g == 0 && p == 0 ? "unclassified" : g > p ? "gameplay" : p > g ? "promotional" : "mixed";
    }

    /// <summary>The opening line of a creative (text before a separator, first six words): what the viewer reads or hears first.</summary>
    public static string Hook(CreativeItem i)
    {
        var src = (i.Format == "text" ? i.Name : i.Title ?? i.Name) ?? "";
        var head = Regex.Split(src, @"\s[|•\-–—]\s|#").FirstOrDefault()?.Trim() ?? src;
        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6);
        return string.Join(' ', words);
    }

    /// <summary>Hooks grouped by opening phrase pattern (questions, numbers, challenges) with aggregate results.</summary>
    public static IReadOnlyList<object> Hooks(IReadOnlyList<ScoredCreative> scored) =>
        scored.Where(s => s.Total.Impressions > 0)
            .GroupBy(s => HookPattern(Hook(s.Item)))
            .Select(g => (Pattern: g.Key, Perf: Perf.Sum(g.Select(x => x.Total)), Examples: g.OrderByDescending(x => x.Score ?? 0).Select(x => Hook(x.Item)).Distinct().Take(3).ToArray(), Count: g.Count()))
            .OrderBy(x => x.Perf.Cpi ?? decimal.MaxValue)
            .Select(x => (object)new { pattern = x.Pattern, count = x.Count, examples = x.Examples, metrics = x.Perf.ToJson() })
            .ToArray();

    public static string HookPattern(string hook)
    {
        var h = hook.ToLowerInvariant();
        if (h.EndsWith('?') || Regex.IsMatch(h, @"^(can|could|will|are|do|how|what|why)\b")) return "question / challenge";
        if (Regex.IsMatch(h, @"\d")) return "number / specific goal";
        if (Regex.IsMatch(h, @"\b(free|download|install|play)\b")) return "call to action";
        if (Regex.IsMatch(h, @"\b(new|fresh|first)\b")) return "novelty";
        return "statement";
    }

    /// <summary>Data-driven next steps for the creative pipeline. Every suggestion names the evidence it is based on.</summary>
    public static IReadOnlyList<object> Suggestions(IReadOnlyList<ScoredCreative> scored, IReadOnlyList<string> formats)
    {
        var s = new List<object>();
        var visual = scored.Where(x => x.Item.Format is not "text").ToArray();
        var best = visual.Where(x => x.Classification == "best").OrderByDescending(x => x.Score).FirstOrDefault()
                   ?? visual.Where(x => x.Score is not null).OrderByDescending(x => x.Score).FirstOrDefault();
        if (best is not null)
            s.Add(new { type = "iterate_winner", title = $"Make 3 variations of \"{Short(best.Item.Name)}\"",
                detail = $"Keep its hook (\"{Hook(best.Item)}\") and change one element per variation: first 3 seconds, level shown, ending/CTA. Test each against the original.",
                evidence = best.Reasons.FirstOrDefault() ?? "", confidence = best.Confidence });
        foreach (var f in visual.Where(x => x.Fatigued).Take(3))
            s.Add(new { type = "refresh", title = $"Refresh \"{Short(f.Item.Name)}\"", detail = "Same concept, new opening shot and new level footage; keep the original running at lower budget until the new one beats it.", evidence = f.FatigueEvidence ?? "", confidence = "medium" });
        foreach (var u in visual.Where(x => x.Classification == "underperforming").Take(3))
            s.Add(new { type = "cut", title = $"Stop or reduce \"{Short(u.Item.Name)}\"", detail = "It has spent enough to judge and is well below the median creative.", evidence = u.Reasons.FirstOrDefault() ?? "", confidence = u.Confidence });
        var themes = visual.Where(x => x.Score is not null).GroupBy(x => Theme(x.Item)).ToDictionary(g => g.Key, g => g.Average(x => x.Score!.Value));
        if (themes.TryGetValue("gameplay", out var gp) && themes.TryGetValue("promotional", out var pr))
            s.Add(new { type = "theme", title = gp >= pr ? "Lean into gameplay footage" : "Promotional framing is outperforming",
                detail = $"Average score: gameplay {gp:0}, promotional {pr:0}. Themes are inferred from creative names/titles, so verify by watching the top creatives.", evidence = "Theme classification (heuristic)", confidence = "low" });
        if (!formats.Contains("video")) s.Add(new { type = "format", title = "Test video creatives", detail = "No video creative has delivered. Short gameplay videos (15-30 s, gameplay in the first 2 s) are the standard format for mobile game UA.", evidence = "No video in the selected range", confidence = "medium" });
        if (!formats.Contains("playable")) s.Add(new { type = "format", title = "Consider a playable ad", detail = "Playables let users try the core mechanic before installing; they often lift install intent for puzzle games. Test against your best video.", evidence = "No playable creative in the selected range", confidence = "low" });
        if (visual.Count(x => x.Classification == "testing") == 0 && visual.Length > 0)
            s.Add(new { type = "pipeline", title = "Keep 2-3 new creatives in testing", detail = "Nothing is in test right now; winners fatigue, so a steady test pipeline protects CPI.", evidence = "0 creatives in testing", confidence = "medium" });
        return s;
    }

    private static string Short(string s) => s.Length > 60 ? s[..57] + "…" : s;
    private static string Fmt(decimal? v) => v is null ? "n/a" : v.Value.ToString(v.Value >= 100 ? "0" : "0.##", System.Globalization.CultureInfo.InvariantCulture);
}
