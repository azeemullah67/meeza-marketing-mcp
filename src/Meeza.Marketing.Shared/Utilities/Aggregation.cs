using System.Globalization;
using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>Aggregated totals. Always grouped by currency so amounts in different currencies are never added.</summary>
public sealed record PerformanceTotals
{
    public required string Platform { get; init; }
    public string? AccountId { get; init; }
    public string? Country { get; init; }
    public string? CountryName { get; init; }
    public required string Currency { get; init; }
    public int CampaignCount { get; init; }
    public decimal? Spend { get; init; }
    public long? Impressions { get; init; }
    public long? Clicks { get; init; }
    public decimal? Conversions { get; init; }
    public decimal? Installs { get; init; }
    public decimal? Ctr { get; init; }
    public decimal? Cpc { get; init; }
    public decimal? Cpm { get; init; }
    public decimal? Cpi { get; init; }
    public decimal? CostPerConversion { get; init; }
}

public static class Aggregation
{
    private static readonly Lazy<Dictionary<string, string>> CountryNameToCode = new(BuildCountryIndex);

    /// <summary>
    /// Parses "PK, PH, US" or "Pakistan, Philippines, United States" into ISO alpha-2 codes. Returns null when empty.
    /// </summary>
    public static IReadOnlySet<string>? ParseCountries(string? countries)
    {
        if (string.IsNullOrWhiteSpace(countries)) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in countries.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 2) { set.Add(raw.Equals("UK", StringComparison.OrdinalIgnoreCase) ? "GB" : raw.ToUpperInvariant()); continue; }
            if (CountryNameToCode.Value.TryGetValue(raw, out var code)) { set.Add(code); continue; }
            throw new ArgumentException($"Unknown country '{raw}'. Use ISO 3166-1 alpha-2 codes such as PK, PH, US.");
        }
        return set;
    }

    public static string? CountryName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 2) return null;
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return null; }
    }

    public static IReadOnlyList<PerformanceTotals> ByCountry(IEnumerable<CampaignPerformance> rows, IReadOnlySet<string>? filter = null) =>
        rows.Where(r => filter is null || (r.Country is not null && filter.Contains(r.Country)))
            .GroupBy(r => (r.Platform, Country: r.Country ?? "UNKNOWN", r.Currency))
            .Select(g => Total(g, g.Key.Platform, g.Key.Currency, country: g.Key.Country))
            .OrderBy(t => t.Currency).ThenByDescending(t => t.Spend ?? 0)
            .ToArray();

    public static IReadOnlyList<PerformanceTotals> ByPlatform(IEnumerable<CampaignPerformance> rows) =>
        rows.GroupBy(r => (r.Platform, r.Currency))
            .Select(g => Total(g, g.Key.Platform, g.Key.Currency))
            .OrderBy(t => t.Platform)
            .ToArray();

    public static IReadOnlyList<PerformanceTotals> ByAccount(IEnumerable<CampaignPerformance> rows) =>
        rows.GroupBy(r => (r.Platform, r.AccountId, r.Currency))
            .Select(g => Total(g, g.Key.Platform, g.Key.Currency, accountId: g.Key.AccountId))
            .OrderBy(t => t.Platform).ThenBy(t => t.AccountId)
            .ToArray();

    /// <summary>Merges per-country rows back into one row per campaign.</summary>
    public static IReadOnlyList<CampaignPerformance> MergeCountries(IEnumerable<CampaignPerformance> rows) =>
        rows.GroupBy(r => (r.Platform, r.AccountId, r.CampaignId, r.Currency))
            .Select(g =>
            {
                var first = g.First();
                return (first with
                {
                    Country = null,
                    Spend = g.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Spend)),
                    Impressions = g.Aggregate((long?)null, (a, r) => MetricMath.Sum(a, r.Impressions)),
                    Clicks = g.Aggregate((long?)null, (a, r) => MetricMath.Sum(a, r.Clicks)),
                    Reach = null, // reach is not additive across countries
                    Conversions = g.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Conversions)),
                    Installs = g.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Installs)),
                }).WithDerivedMetrics();
            })
            .ToArray();

    /// <summary>
    /// CPI report: campaigns with installs ranked by CPI, plus campaigns that spent money without any reported install.
    /// </summary>
    public static object CpiReport(IReadOnlyList<CampaignPerformance> rows)
    {
        var withInstalls = rows.Where(r => r.Installs > 0)
            .OrderBy(r => r.Currency).ThenBy(r => r.Cpi)
            .Select(r => new
            {
                r.Platform, r.AccountId, r.CampaignId, r.CampaignName, r.Country, r.Currency,
                r.Spend, r.Installs, r.Cpi, r.InstallSource, r.Clicks, r.Impressions,
                installRatePerClick = (r.Clicks > 0 && r.Installs.HasValue) ? MetricMath.Round(r.Installs!.Value / r.Clicks!.Value * 100m) : (decimal?)null,
            })
            .ToArray();

        var spendWithoutInstalls = rows.Where(r => r.Spend > 0 && (r.Installs is null or 0))
            .OrderByDescending(r => r.Spend)
            .Select(r => new
            {
                r.Platform, r.AccountId, r.CampaignId, r.CampaignName, r.Country, r.Currency, r.Spend, r.Clicks, r.Impressions,
                installs = r.Installs,
                reason = r.Installs is null ? "no install events reported (tracking missing or not an install campaign)" : "zero installs reported",
            })
            .ToArray();

        return new
        {
            totalsByPlatformAndCurrency = ByPlatform(rows),
            campaignsWithInstalls = withInstalls,
            spendWithoutInstalls,
        };
    }

    private static PerformanceTotals Total(IEnumerable<CampaignPerformance> group, string platform, string currency, string? country = null, string? accountId = null)
    {
        var list = group.ToList();
        var spend = list.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Spend));
        var impressions = list.Aggregate((long?)null, (a, r) => MetricMath.Sum(a, r.Impressions));
        var clicks = list.Aggregate((long?)null, (a, r) => MetricMath.Sum(a, r.Clicks));
        var conversions = list.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Conversions));
        var installs = list.Aggregate((decimal?)null, (a, r) => MetricMath.Sum(a, r.Installs));
        return new PerformanceTotals
        {
            Platform = platform,
            AccountId = accountId,
            Country = country,
            CountryName = CountryName(country),
            Currency = currency,
            CampaignCount = list.Select(r => r.CampaignId).Distinct().Count(),
            Spend = spend,
            Impressions = impressions,
            Clicks = clicks,
            Conversions = conversions,
            Installs = installs,
            Ctr = MetricMath.Ctr(clicks, impressions),
            Cpc = MetricMath.CostPer(spend, clicks),
            Cpm = MetricMath.Cpm(spend, impressions),
            Cpi = MetricMath.CostPer(spend, installs),
            CostPerConversion = MetricMath.CostPer(spend, conversions),
        };
    }

    private static Dictionary<string, string> BuildCountryIndex()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                if (region.TwoLetterISORegionName.Length != 2) continue;
                map.TryAdd(region.EnglishName, region.TwoLetterISORegionName);
                map.TryAdd(region.ThreeLetterISORegionName, region.TwoLetterISORegionName);
            }
            catch (ArgumentException) { }
        }
        map.TryAdd("USA", "US");
        map.TryAdd("United States of America", "US");
        map.TryAdd("UK", "GB");
        map.TryAdd("Britain", "GB");
        map.TryAdd("Great Britain", "GB");
        return map;
    }
}
