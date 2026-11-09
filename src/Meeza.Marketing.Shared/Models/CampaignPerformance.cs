namespace Meeza.Marketing.Shared.Models;

/// <summary>
/// Normalized campaign performance row shared by every advertising platform.
/// Unavailable metrics are <c>null</c>, never zero. Currency is always the platform account's
/// original currency; amounts are never converted.
/// </summary>
public sealed record CampaignPerformance
{
    public required string Platform { get; init; }
    public required string AccountId { get; init; }
    public string? AccountName { get; init; }
    public string? CampaignId { get; init; }
    public string? CampaignName { get; init; }
    public string? CampaignStatus { get; init; }
    public string? Objective { get; init; }

    /// <summary>Reporting level of this row: campaign, adset (Meta ad set / Google ad group) or ad.</summary>
    public string Level { get; init; } = "campaign";
    public string? AdSetId { get; init; }
    public string? AdSetName { get; init; }
    public string? AdId { get; init; }
    public string? AdName { get; init; }

    /// <summary>Delivery status of the row's object (ad, ad set or campaign) when known.</summary>
    public string? Status { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code when the row is broken down by country; otherwise null.</summary>
    public string? Country { get; init; }

    public decimal? Spend { get; init; }
    public required string Currency { get; init; }

    public long? Impressions { get; init; }
    public long? Reach { get; init; }
    public long? Clicks { get; init; }

    /// <summary>Platform-attributed conversions, when reported.</summary>
    public decimal? Conversions { get; init; }

    /// <summary>
    /// Installs attributed by the ad platform itself (see <see cref="InstallSource"/>).
    /// These are NOT store-reported installs and must not be summed with Play Console installs.
    /// </summary>
    public decimal? Installs { get; init; }
    public string? InstallSource { get; init; }

    public decimal? Ctr { get; init; }
    public decimal? Cpc { get; init; }
    public decimal? Cpm { get; init; }
    public decimal? Cpi { get; init; }
    public decimal? CostPerConversion { get; init; }

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }
    public string? TimeZone { get; init; }

    public List<string>? Notes { get; init; }

    /// <summary>Recomputes derived ratio metrics from raw counts so every platform uses identical formulas.</summary>
    public CampaignPerformance WithDerivedMetrics() => this with
    {
        Ctr = MetricMath.Ctr(Clicks, Impressions),
        Cpc = MetricMath.CostPer(Spend, Clicks),
        Cpm = MetricMath.Cpm(Spend, Impressions),
        Cpi = MetricMath.CostPer(Spend, Installs),
        CostPerConversion = MetricMath.CostPer(Spend, Conversions),
    };
}
