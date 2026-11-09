using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.GoogleAds.Models;

public static class GoogleAdsMapping
{
    public const string InstallSource = "google_ads_attributed:DOWNLOAD_conversions";

    /// <summary>Google Ads money values are micros (1/1,000,000 of the account currency).</summary>
    public static decimal? Micros(JsonElement row, string path) =>
        row.Dec(path) is { } micros ? MetricMath.Round(micros / 1_000_000m) : null;

    public static AdAccount FromCustomer(JsonElement row) => new(
        GoogleAdsOptions.Platform,
        row.Str("customer.id")!,
        row.Str("customer.descriptiveName"),
        row.Str("customer.currencyCode"),
        row.Str("customer.timeZone"),
        row.Str("customer.status"),
        row.Bool("customer.manager"));

    public static AdAccount FromCustomerClient(JsonElement row) => new(
        GoogleAdsOptions.Platform,
        row.Str("customerClient.id")!,
        row.Str("customerClient.descriptiveName"),
        row.Str("customerClient.currencyCode"),
        row.Str("customerClient.timeZone"),
        row.Str("customerClient.status"),
        row.Bool("customerClient.manager"));

    /// <summary>
    /// Maps a campaign (or geographic_view) metrics row. <paramref name="installs"/> is null when the account has
    /// no enabled app-install (DOWNLOAD) conversion action, so install data is genuinely unavailable.
    /// </summary>
    public static CampaignPerformance ToPerformance(JsonElement row, AdAccount account, DateRange range, string? country, decimal? installs)
    {
        var cost = Micros(row, "metrics.costMicros") ?? 0m;
        return new CampaignPerformance
        {
            Platform = GoogleAdsOptions.Platform,
            AccountId = account.AccountId,
            AccountName = account.Name,
            CampaignId = row.Str("campaign.id"),
            CampaignName = row.Str("campaign.name"),
            CampaignStatus = row.Str("campaign.status"),
            Objective = JoinChannel(row.Str("campaign.advertisingChannelType"), row.Str("campaign.advertisingChannelSubType")),
            Country = country,
            Spend = cost,
            Currency = account.Currency ?? "UNKNOWN",
            Impressions = row.Long("metrics.impressions") ?? 0,
            Clicks = row.Long("metrics.clicks") ?? 0,
            Conversions = row.Dec("metrics.conversions") ?? 0,
            Installs = installs,
            InstallSource = installs is null ? null : InstallSource,
            StartDate = range.Start,
            EndDate = range.End,
            TimeZone = account.TimeZone,
            Notes = installs is null ? ["No enabled app-install (DOWNLOAD) conversion action in this account, so installs are unavailable."] : null,
        }.WithDerivedMetrics();
    }

    /// <summary>Maps an ad_group or ad_group_ad row, keeping ad group / ad identity and status.</summary>
    public static CampaignPerformance ToLevelPerformance(JsonElement row, AdAccount account, DateRange range, ReportLevel level, decimal? installs)
    {
        var p = ToPerformance(row, account, range, null, installs);
        var adType = row.Str("adGroupAd.ad.type");
        var adId = row.Str("adGroupAd.ad.id");
        return p with
        {
            Level = level.Name(),
            AdSetId = row.Str("adGroup.id"),
            AdSetName = row.Str("adGroup.name"),
            AdId = adId,
            // Many Google ad types (App, Responsive Search) have no name; fall back to type + ID.
            AdName = level == ReportLevel.Ad ? row.Str("adGroupAd.ad.name") is { Length: > 0 } n ? n : $"{Humanize(adType)} #{adId}" : null,
            Status = level switch
            {
                ReportLevel.Ad => row.Str("adGroupAd.status"),
                ReportLevel.AdSet => row.Str("adGroup.status"),
                _ => row.Str("campaign.status"),
            },
        };
    }

    /// <summary>Key that identifies one asset in one ad and one field (the same asset can be both a headline and a description).</summary>
    public static string AssetKey(JsonElement row) =>
        $"{row.Str("adGroup.id")}~{row.Str("adGroupAd.ad.id")}~{row.Str("asset.id")}~{row.Str("adGroupAdAssetView.fieldType")}";

    /// <summary>Maps an ad_group_ad_asset_view row. <paramref name="installs"/> is null when the account has no install tracking.</summary>
    public static AssetPerformance ToAssetPerformance(JsonElement row, AdAccount account, DateRange range, decimal? installs)
    {
        var type = row.Str("asset.type") ?? "UNKNOWN";
        var videoId = row.Str("asset.youtubeVideoAsset.youtubeVideoId");
        var imageUrl = row.Str("asset.imageAsset.fullSize.url");
        var w = row.Long("asset.imageAsset.fullSize.widthPixels");
        var h = row.Long("asset.imageAsset.fullSize.heightPixels");
        var label = row.Str("asset.textAsset.text")
            ?? row.Str("asset.youtubeVideoAsset.youtubeVideoTitle")
            ?? JoinNonEmpty(row.Str("asset.name"), w is not null && h is not null ? $"{w}×{h}" : null)
            ?? $"{Humanize(type)} #{row.Str("asset.id")}";
        return new AssetPerformance
        {
            Platform = GoogleAdsOptions.Platform,
            AccountId = account.AccountId,
            Currency = account.Currency ?? "UNKNOWN",
            CampaignId = row.Str("campaign.id"),
            CampaignName = row.Str("campaign.name"),
            AdSetId = row.Str("adGroup.id"),
            AdSetName = row.Str("adGroup.name"),
            AdId = row.Str("adGroupAd.ad.id"),
            AssetId = row.Str("asset.id") ?? "",
            AssetType = type switch { "TEXT" => "text", "IMAGE" => "image", "YOUTUBE_VIDEO" => "video", _ => type.ToLowerInvariant() },
            FieldType = row.Str("adGroupAdAssetView.fieldType") ?? "UNKNOWN",
            Label = label,
            PreviewUrl = videoId is not null ? $"https://www.youtube.com/watch?v={videoId}" : imageUrl,
            PerformanceLabel = row.Str("adGroupAdAssetView.performanceLabel"),
            Enabled = row.Bool("adGroupAdAssetView.enabled"),
            Spend = Micros(row, "metrics.costMicros") ?? 0m,
            Impressions = row.Long("metrics.impressions") ?? 0,
            Clicks = row.Long("metrics.clicks") ?? 0,
            Installs = installs,
            StartDate = range.Start,
            EndDate = range.End,
        }.WithDerivedMetrics();
    }

    private static string? JoinNonEmpty(params string?[] parts)
    {
        var p = parts.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        return p.Length == 0 ? null : string.Join(" · ", p);
    }

    private static string Humanize(string? enumName) =>
        string.IsNullOrEmpty(enumName) ? "Ad" : string.Join(' ', enumName.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    private static string? JoinChannel(string? type, string? sub) =>
        type is null ? null : sub is null or "UNSPECIFIED" or "UNKNOWN" ? type : $"{type}/{sub}";
}
