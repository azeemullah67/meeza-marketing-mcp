namespace Meeza.Marketing.Shared.Models;

/// <summary>
/// Shared metric formulas. Every function returns null when an input is unavailable or the
/// denominator is zero, so "no data" is never presented as "zero".
/// </summary>
public static class MetricMath
{
    /// <summary>Click-through rate as a percentage (clicks / impressions * 100).</summary>
    public static decimal? Ctr(long? clicks, long? impressions) =>
        clicks is null || impressions is null or 0 ? null : Round((decimal)clicks.Value / impressions.Value * 100m);

    /// <summary>Cost per thousand impressions.</summary>
    public static decimal? Cpm(decimal? spend, long? impressions) =>
        spend is null || impressions is null or 0 ? null : Round(spend.Value / impressions.Value * 1000m);

    /// <summary>Cost per unit (click, install, conversion). Null when the unit count is null or zero.</summary>
    public static decimal? CostPer(decimal? spend, decimal? units) =>
        spend is null || units is null or 0 ? null : Round(spend.Value / units.Value);

    public static decimal? CostPer(decimal? spend, long? units) => CostPer(spend, (decimal?)units);

    public static decimal? Sum(decimal? a, decimal? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);
    public static long? Sum(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    public static decimal Round(decimal v) => Math.Round(v, 4, MidpointRounding.AwayFromZero);
}
