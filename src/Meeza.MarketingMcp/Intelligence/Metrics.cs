using Meeza.Marketing.Shared.Models;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>
/// Additive performance totals plus derived ratios. Every ratio is null when its denominator is missing or zero, and
/// installs stay null when no row reported install tracking (unavailable ≠ zero).
/// </summary>
public sealed record Perf
{
    public decimal Spend { get; init; }
    public long Impressions { get; init; }
    public long Clicks { get; init; }
    public decimal? Installs { get; init; }
    public decimal? Conversions { get; init; }
    public decimal? ConversionValue { get; init; }
    public long? VideoViews { get; init; }
    public long? Thruplays { get; init; }
    public int Rows { get; init; }

    public decimal? Ctr => MetricMath.Ctr(Clicks, Impressions);
    public decimal? Cpc => MetricMath.CostPer(Spend, Clicks);
    public decimal? Cpm => MetricMath.Cpm(Spend, Impressions);
    public decimal? Cpi => MetricMath.CostPer(Spend, Installs);
    /// <summary>Installs per 1,000 impressions.</summary>
    public decimal? Ipm => Installs is null || Impressions == 0 ? null : MetricMath.Round(Installs.Value * 1000m / Impressions);
    /// <summary>Click-to-install conversion rate (%).</summary>
    public decimal? Cvr => Installs is null || Clicks == 0 ? null : MetricMath.Round(Installs.Value * 100m / Clicks);
    /// <summary>Platform-reported conversion value ÷ spend (%), as the ad platform attributes it.</summary>
    public decimal? PlatformRoas => ConversionValue is null || Spend == 0 ? null : MetricMath.Round(ConversionValue.Value * 100m / Spend);
    public decimal? ViewRate => VideoViews is null || Impressions == 0 ? null : MetricMath.Round(VideoViews.Value * 100m / Impressions);

    public static Perf Of(IEnumerable<AdFact> rows) => Sum(rows.Select(r => new Perf
    {
        Spend = r.Spend ?? 0, Impressions = r.Impressions ?? 0, Clicks = r.Clicks ?? 0, Installs = r.Installs, Conversions = r.Conversions,
        ConversionValue = r.ConversionValue, VideoViews = r.VideoViews, Thruplays = r.Thruplays, Rows = 1,
    }));

    public static Perf Of(IEnumerable<CountryFact> rows) => Sum(rows.Select(r => new Perf
    {
        Spend = r.Spend ?? 0, Impressions = r.Impressions ?? 0, Clicks = r.Clicks ?? 0, Installs = r.Installs, Conversions = r.Conversions, Rows = 1,
    }));

    public static Perf Of(IEnumerable<AssetFact> rows) => Sum(rows.Select(r => new Perf
    {
        Spend = r.Spend ?? 0, Impressions = r.Impressions ?? 0, Clicks = r.Clicks ?? 0, Installs = r.Installs, Rows = 1,
    }));

    public static Perf Sum(IEnumerable<Perf> parts)
    {
        var acc = new Perf();
        foreach (var p in parts)
            acc = acc with
            {
                Spend = acc.Spend + p.Spend, Impressions = acc.Impressions + p.Impressions, Clicks = acc.Clicks + p.Clicks,
                Installs = MetricMath.Sum(acc.Installs, p.Installs), Conversions = MetricMath.Sum(acc.Conversions, p.Conversions),
                ConversionValue = MetricMath.Sum(acc.ConversionValue, p.ConversionValue),
                VideoViews = MetricMath.Sum(acc.VideoViews, p.VideoViews), Thruplays = MetricMath.Sum(acc.Thruplays, p.Thruplays),
                Rows = acc.Rows + p.Rows,
            };
        return acc;
    }

    public object ToJson() => new
    {
        spend = MetricMath.Round(Spend), impressions = Impressions, clicks = Clicks, installs = Installs, conversions = Conversions,
        conversionValue = ConversionValue, videoViews = VideoViews, thruplays = Thruplays,
        ctr = Ctr, cpc = Cpc, cpm = Cpm, cpi = Cpi, ipm = Ipm, cvr = Cvr, platformRoas = PlatformRoas, viewRate = ViewRate,
    };
}

public static class Stats
{
    public static decimal? Median(IEnumerable<decimal> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return null;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    public static decimal? PctChange(decimal? current, decimal? previous) =>
        current is null || previous is null or 0 ? null : MetricMath.Round((current.Value - previous.Value) * 100m / Math.Abs(previous.Value));

    /// <summary>Least-squares slope of y over x = 0..n-1 (units per day). Null with fewer than 3 points.</summary>
    public static double? Slope(IReadOnlyList<double> y)
    {
        if (y.Count < 3) return null;
        var mx = (y.Count - 1) / 2.0;
        var my = y.Average();
        double num = 0, den = 0;
        for (var i = 0; i < y.Count; i++) { num += (i - mx) * (y[i] - my); den += (i - mx) * (i - mx); }
        return den == 0 ? null : num / den;
    }

    public static IEnumerable<DateOnly> Days(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1)) yield return d;
    }
}
