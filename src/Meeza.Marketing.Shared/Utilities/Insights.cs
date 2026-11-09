using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>Period-over-period change of one metric. Null when either side is unavailable or the base is zero.</summary>
public sealed record MetricChange(decimal? Current, decimal? Previous, decimal? ChangePercent);

public sealed record PlatformComparison(
    string Platform, string Currency,
    MetricChange Spend, MetricChange Installs, MetricChange Cpi, MetricChange Ctr, MetricChange Cpm, MetricChange Ipm);

/// <summary>A rule-based observation that needs attention. Severity: critical, warning, info, good.</summary>
public sealed record Alert(string Severity, string Rule, string Platform, string Currency, string Message, IReadOnlyList<string>? Items = null);

/// <summary>
/// Pure, deterministic analysis over already-fetched rows: period comparison and alert rules.
/// Never mixes currencies and never adds installs across platforms.
/// </summary>
public static class Insights
{
    public const decimal CpiChangeAlertPercent = 20m;
    public const decimal SpendChangeAlertPercent = 30m;
    public const decimal CpiOutlierFactor = 2m;
    public const int MinInstallsForCpi = 3;

    public static decimal? Ipm(PerformanceTotals? t) =>
        t is { Impressions: > 0, Installs: not null } ? MetricMath.Round(t.Installs.Value / t.Impressions.Value * 1000m) : null;

    public static MetricChange Change(decimal? current, decimal? previous) =>
        new(current, previous,
            current is null || previous is null or 0 ? null : MetricMath.Round((current.Value - previous.Value) / Math.Abs(previous.Value) * 100m));

    public static IReadOnlyList<PlatformComparison> Compare(IReadOnlyList<PerformanceTotals> current, IReadOnlyList<PerformanceTotals> previous) =>
        current.Select(c =>
        {
            var p = previous.FirstOrDefault(x => x.Platform == c.Platform && x.Currency == c.Currency);
            return new PlatformComparison(c.Platform, c.Currency,
                Change(c.Spend, p?.Spend), Change(c.Installs, p?.Installs), Change(c.Cpi, p?.Cpi),
                Change(c.Ctr, p?.Ctr), Change(c.Cpm, p?.Cpm), Change(Ipm(c), Ipm(p)));
        }).ToArray();

    public static decimal? Median(IEnumerable<decimal> values)
    {
        var v = values.Order().ToArray();
        if (v.Length == 0) return null;
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2m;
    }

    public const int AnomalyMinHistoryDays = 7;
    public const int AnomalyLookbackDays = 14;
    public const decimal AnomalyZ = 3m;
    public const decimal AnomalyMinChangePercent = 30m;

    /// <summary>
    /// Flags the latest day of each platform/currency series when spend, installs or CPI sits more than
    /// 3 robust standard deviations (median ± 3 × 1.4826 × MAD) from the previous 14 days and moved 30%+.
    /// Needs 7+ days of history. Pure and deterministic.
    /// </summary>
    public static IReadOnlyList<Alert> DetectDailyAnomalies(IReadOnlyList<DailyPerformance> daily)
    {
        var alerts = new List<Alert>();
        foreach (var g in daily.GroupBy(d => (d.Platform, d.Currency)))
        {
            var days = g.GroupBy(d => d.Date)
                .Select(x => (Date: x.Key, Spend: x.Sum(d => d.Spend ?? 0), Installs: x.Sum(d => d.Installs ?? 0)))
                .OrderBy(x => x.Date)
                .ToArray();
            if (days.Length < AnomalyMinHistoryDays + 1) continue;
            var last = days[^1];
            var history = days[..^1].TakeLast(AnomalyLookbackDays).ToArray();
            if (history.Count(h => h.Spend > 0) < AnomalyMinHistoryDays) continue;

            decimal? Cpi((DateOnly Date, decimal Spend, decimal Installs) d) => d.Installs > 0 ? d.Spend / d.Installs : null;
            Check("spend", last.Spend, history.Select(h => h.Spend), higherIsBad: false);
            Check("installs", last.Installs, history.Select(h => h.Installs), higherIsBad: false);
            if (Cpi(last) is { } cpi) Check("CPI", cpi, history.Select(Cpi).OfType<decimal>(), higherIsBad: true);

            void Check(string metric, decimal value, IEnumerable<decimal> baselineValues, bool higherIsBad)
            {
                var baseline = baselineValues.ToArray();
                if (baseline.Length < AnomalyMinHistoryDays) return;
                var median = Median(baseline)!.Value;
                if (median <= 0) return;
                var mad = Median(baseline.Select(v => Math.Abs(v - median)))!.Value;
                var scale = Math.Max(1.4826m * mad, median * 0.05m); // floor stops a flat history flagging every wiggle
                var z = (value - median) / scale;
                var change = (value - median) / median * 100m;
                if (Math.Abs(z) < AnomalyZ || Math.Abs(change) < AnomalyMinChangePercent) return;
                var up = value > median;
                var severity = metric switch
                {
                    "spend" => "info",
                    _ when up == higherIsBad => "warning",
                    _ => "good",
                };
                alerts.Add(new Alert(severity, $"daily_{metric.ToLowerInvariant()}_{(up ? "spike" : "drop")}", g.Key.Platform, g.Key.Currency,
                    $"{last.Date:yyyy-MM-dd}: {metric} {value:0.##} is {Math.Abs(change):0}% {(up ? "above" : "below")} the usual {median:0.##} " +
                    $"(median of the previous {baseline.Length} days, {Math.Abs(z):0.0} robust SDs)."));
            }
        }
        return alerts;
    }

    /// <param name="rows">Rows at one level (ad, ad set or campaign) for the current period.</param>
    public static IReadOnlyList<Alert> Detect(
        IReadOnlyList<CampaignPerformance> rows, IReadOnlyList<PerformanceTotals> current, IReadOnlyList<PerformanceTotals>? previous)
    {
        var alerts = new List<Alert>();
        static string Label(CampaignPerformance r) => r.AdName ?? r.AdSetName ?? r.CampaignName ?? r.CampaignId ?? "(unnamed)";

        foreach (var g in rows.GroupBy(r => (r.Platform, r.Currency)))
        {
            var noInstalls = g.Where(r => r.Spend > 0 && r.Installs is null or 0).OrderByDescending(r => r.Spend).ToArray();
            if (noInstalls.Length > 0)
                alerts.Add(new Alert("warning", "spend_without_installs", g.Key.Platform, g.Key.Currency,
                    $"{noInstalls.Length} item(s) spent {noInstalls.Sum(r => r.Spend ?? 0):0.##} {g.Key.Currency} with no installs reported.",
                    noInstalls.Take(10).Select(r => $"{Label(r)}: {r.Spend:0.##} {g.Key.Currency}").ToArray()));

            var eligible = g.Where(r => r.Installs >= MinInstallsForCpi && r.Cpi is not null).ToArray();
            var median = Median(eligible.Select(r => r.Cpi!.Value));
            if (median is > 0)
            {
                var outliers = eligible.Where(r => r.Cpi > median * CpiOutlierFactor).OrderByDescending(r => r.Cpi).ToArray();
                if (outliers.Length > 0)
                    alerts.Add(new Alert("warning", "cpi_outlier", g.Key.Platform, g.Key.Currency,
                        $"{outliers.Length} item(s) have CPI above {CpiOutlierFactor}x the median ({median:0.##} {g.Key.Currency}).",
                        outliers.Take(10).Select(r => $"{Label(r)}: CPI {r.Cpi:0.##} {g.Key.Currency}").ToArray()));
            }
        }

        if (previous is not null)
        {
            foreach (var c in Compare(current, previous))
            {
                if (c.Cpi.ChangePercent >= CpiChangeAlertPercent)
                    alerts.Add(new Alert("critical", "cpi_increase", c.Platform, c.Currency,
                        $"CPI up {c.Cpi.ChangePercent:0}% ({c.Cpi.Previous:0.##} -> {c.Cpi.Current:0.##} {c.Currency}) vs the previous period."));
                else if (c.Cpi.ChangePercent <= -CpiChangeAlertPercent)
                    alerts.Add(new Alert("good", "cpi_decrease", c.Platform, c.Currency,
                        $"CPI down {-c.Cpi.ChangePercent:0}% ({c.Cpi.Previous:0.##} -> {c.Cpi.Current:0.##} {c.Currency}) vs the previous period."));
                if (c.Spend.ChangePercent is { } s && Math.Abs(s) >= SpendChangeAlertPercent)
                    alerts.Add(new Alert("info", s > 0 ? "spend_increase" : "spend_decrease", c.Platform, c.Currency,
                        $"Spend {(s > 0 ? "up" : "down")} {Math.Abs(s):0}% ({c.Spend.Previous:0.##} -> {c.Spend.Current:0.##} {c.Currency}) vs the previous period."));
            }
        }

        var order = new Dictionary<string, int> { ["critical"] = 0, ["warning"] = 1, ["info"] = 2, ["good"] = 3 };
        return alerts.OrderBy(a => order[a.Severity]).ToArray();
    }
}
