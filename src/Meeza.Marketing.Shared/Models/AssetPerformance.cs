namespace Meeza.Marketing.Shared.Models;

/// <summary>
/// Performance of one creative asset (headline, description, image, video) inside an ad that mixes assets,
/// such as a Google App campaign ad. One impression credits several assets at once (a headline AND a video),
/// so asset rows overlap: never add asset spend or installs together, or to ad/campaign totals.
/// </summary>
public sealed record AssetPerformance
{
    public required string Platform { get; init; }
    public required string AccountId { get; init; }
    public required string Currency { get; init; }
    public string? CampaignId { get; init; }
    public string? CampaignName { get; init; }
    public string? AdSetId { get; init; }
    public string? AdSetName { get; init; }
    public string? AdId { get; init; }

    public required string AssetId { get; init; }

    /// <summary>Asset type: text, image or video.</summary>
    public required string AssetType { get; init; }

    /// <summary>Where the asset is used in the ad, e.g. HEADLINE, DESCRIPTION, MARKETING_IMAGE, YOUTUBE_VIDEO.</summary>
    public required string FieldType { get; init; }

    /// <summary>Human-readable label: the text itself, the video title, or the image name and size.</summary>
    public string? Label { get; init; }

    /// <summary>YouTube video ID or image URL, when the asset has one.</summary>
    public string? PreviewUrl { get; init; }

    /// <summary>The platform's own rating, e.g. Google's LEARNING, LOW, GOOD, BEST. Null when not reported.</summary>
    public string? PerformanceLabel { get; init; }
    public bool? Enabled { get; init; }

    public decimal? Spend { get; init; }
    public long? Impressions { get; init; }
    public long? Clicks { get; init; }

    /// <summary>Installs the platform credits to this asset. Null when the account has no install tracking.</summary>
    public decimal? Installs { get; init; }

    public decimal? Ctr { get; init; }
    public decimal? Cpi { get; init; }

    public required DateOnly StartDate { get; init; }
    public required DateOnly EndDate { get; init; }

    public AssetPerformance WithDerivedMetrics() => this with
    {
        Ctr = MetricMath.Ctr(Clicks, Impressions),
        Cpi = MetricMath.CostPer(Spend, Installs),
    };
}
