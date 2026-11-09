using System.Globalization;
using System.Text.Json;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Sync;

/// <summary>Synchronizes Google Ads ad-level, country-level and asset-level daily data (read-only GAQL).</summary>
public sealed class GoogleAdsSyncer(GoogleAdsPlatform platform, FactStore facts) : IIntegrationSyncer
{
    public string Integration => Integrations.GoogleAds;
    public bool IsConfigured => platform.IsConfigured;
    public string ConfigurationHint => platform.ConfigurationHint;
    // Google Ads keeps attributing conversions to the click date for the length of the conversion window, so the last
    // two weeks are re-read on every sync. Installs typically settle within a few days.
    public int LookbackDays => 14;
    public int ChunkDays => 31;

    public async Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct)
    {
        var a = (await platform.ResolveAccountsAsync(externalId, ct).ConfigureAwait(false)).Single();
        return new AccountInfo(a.AccountId, a.Name, a.Currency, a.TimeZone);
    }

    public async Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct)
    {
        var client = platform.Client;
        var id = account.ExternalId;
        var range = new DateRange(from, to);
        var currency = account.Currency ?? "UNKNOWN";
        var result = new WindowResult();
        var scope = new Dictionary<string, object?> { ["project_id"] = projectId, ["platform"] = Integration, ["account_id"] = id };
        var adAccount = (await platform.ResolveAccountsAsync(id, ct).ConfigureAwait(false)).Single();

        // ---- required: ad-level daily metrics and installs. Any failure here throws and nothing is written.
        var tracking = await platform.HasInstallTrackingAsync(adAccount, ct).ConfigureAwait(false);
        var metrics = await client.SearchAsync(id, GoogleAdsQueries.AdDaily(range), ct).ConfigureAwait(false);
        Dictionary<string, decimal>? installs = null;
        if (tracking)
        {
            installs = [];
            foreach (var r in await client.SearchAsync(id, GoogleAdsQueries.AdInstallsDaily(range), ct).ConfigureAwait(false))
            {
                var k = Key(r.Str("segments.date"), r.Str("adGroup.id"), r.Str("adGroupAd.ad.id"));
                installs[k] = installs.GetValueOrDefault(k) + (r.Dec("metrics.conversions") ?? 0);
            }
        }
        else result.Warnings.Add("No enabled app-install (DOWNLOAD) conversion action: installs are stored as unavailable (null), not zero.");

        // ---- optional: video views. Unsupported fields only produce a warning.
        Dictionary<string, (long? Views, long? P25, long? P100)>? video = null;
        try
        {
            video = [];
            foreach (var r in await client.SearchAsync(id, GoogleAdsQueries.AdVideoDaily(range), ct).ConfigureAwait(false))
            {
                var imps = r.Long("metrics.impressions");
                long? Q(string f) => r.Dec(f) is { } rate && imps is not null ? (long)Math.Round(rate * imps.Value) : null;
                video[Key(r.Str("segments.date"), r.Str("adGroup.id"), r.Str("adGroupAd.ad.id"))] =
                    (r.Long("metrics.videoTrueviewViews"), Q("metrics.videoQuartileP25Rate"), Q("metrics.videoQuartileP100Rate"));
            }
        }
        catch (PlatformApiException ex)
        {
            video = null;
            result.Warnings.Add("Video metrics unavailable: " + ex.Message);
        }

        var adRows = metrics
            // An ad can appear in several rows per day only if segmented further; merge defensively by key.
            .GroupBy(r => Key(r.Str("segments.date"), r.Str("adGroup.id"), r.Str("adGroupAd.ad.id")))
            .Select(g =>
            {
                var r = g.First();
                var date = ParseDate(r.Str("segments.date"));
                var spend = g.Sum(x => GoogleAdsMapping.Micros(x, "metrics.costMicros") ?? 0);
                var imps = g.Sum(x => x.Long("metrics.impressions") ?? 0);
                if (spend > 0 || imps > 0) result.Seen(date);
                var v = video?.GetValueOrDefault(g.Key);
                return new AdDailyRow(
                    date, r.Str("campaign.id")!, r.Str("campaign.name"), r.Str("campaign.status"),
                    Channel(r.Str("campaign.advertisingChannelType"), r.Str("campaign.advertisingChannelSubType")),
                    r.Str("adGroup.id")!, r.Str("adGroup.name"), r.Str("adGroup.status"),
                    r.Str("adGroupAd.ad.id")!, r.Str("adGroupAd.ad.name") is { Length: > 0 } n ? n : $"{Humanize(r.Str("adGroupAd.ad.type"))} #{r.Str("adGroupAd.ad.id")}",
                    r.Str("adGroupAd.status"), currency,
                    spend, imps, null, g.Sum(x => x.Long("metrics.clicks") ?? 0), g.Sum(x => x.Dec("metrics.conversions") ?? 0),
                    installs is null ? null : installs.GetValueOrDefault(g.Key), installs is null ? null : GoogleAdsMapping.InstallSource,
                    g.Sum(x => x.Dec("metrics.conversionsValue") ?? 0),
                    v?.Views, v?.P25, v?.P100, null);
            }).ToArray();

        // ---- required: country x campaign daily.
        var geo = await client.SearchAsync(id, GoogleAdsQueries.CountryDaily(range), ct).ConfigureAwait(false);
        Dictionary<string, decimal>? geoInstalls = null;
        if (tracking)
        {
            geoInstalls = [];
            foreach (var r in await client.SearchAsync(id, GoogleAdsQueries.CountryInstallsDaily(range), ct).ConfigureAwait(false))
            {
                var k = Key(r.Str("segments.date"), r.Str("campaign.id"), r.Long("geographicView.countryCriterionId")?.ToString(CultureInfo.InvariantCulture));
                geoInstalls[k] = geoInstalls.GetValueOrDefault(k) + (r.Dec("metrics.conversions") ?? 0);
            }
        }
        var codes = await platform.ResolveCountriesAsync(adAccount,
            geo.Select(r => r.Long("geographicView.countryCriterionId") ?? 0).Where(x => x > 0).Distinct(), ct).ConfigureAwait(false);
        var countryRows = geo
            .GroupBy(r => Key(r.Str("segments.date"), r.Str("campaign.id"), r.Long("geographicView.countryCriterionId")?.ToString(CultureInfo.InvariantCulture)))
            .Select(g =>
            {
                var r = g.First();
                var geoId = r.Long("geographicView.countryCriterionId") ?? 0;
                return new CountryDailyRow(ParseDate(r.Str("segments.date")), r.Str("campaign.id")!, r.Str("campaign.name"),
                    codes.GetValueOrDefault(geoId) ?? $"geo:{geoId}", currency,
                    g.Sum(x => GoogleAdsMapping.Micros(x, "metrics.costMicros") ?? 0), g.Sum(x => x.Long("metrics.impressions") ?? 0),
                    g.Sum(x => x.Long("metrics.clicks") ?? 0), g.Sum(x => x.Dec("metrics.conversions") ?? 0),
                    geoInstalls is null ? null : geoInstalls.GetValueOrDefault(g.Key));
            })
            // Several criteria can map to one country code: merge them.
            .GroupBy(x => (x.Date, x.CampaignId, x.Country))
            .Select(g => g.Count() == 1 ? g.First() : g.First() with
            {
                Spend = g.Sum(x => x.Spend), Impressions = g.Sum(x => x.Impressions), Clicks = g.Sum(x => x.Clicks),
                Conversions = g.Sum(x => x.Conversions), Installs = g.All(x => x.Installs is null) ? null : g.Sum(x => x.Installs ?? 0),
            }).ToArray();

        // ---- optional: asset-level daily + creative metadata (App campaigns).
        AssetDailyRow[]? assetRows = null;
        List<CreativeRow>? creativeRows = null;
        try
        {
            var assets = await client.SearchAsync(id, GoogleAdsQueries.AssetDaily(range), ct).ConfigureAwait(false);
            Dictionary<string, decimal>? assetInstalls = null;
            var actions = (await client.SearchAsync(id, GoogleAdsQueries.InstallConversionActions, ct).ConfigureAwait(false))
                .Select(r => $"customers/{id}/conversionActions/{r.Str("conversionAction.id")}").ToHashSet(StringComparer.Ordinal);
            if (actions.Count > 0)
            {
                assetInstalls = [];
                foreach (var r in await client.SearchAsync(id, GoogleAdsQueries.AssetConversionsDaily(range), ct).ConfigureAwait(false))
                {
                    if (!actions.Contains(r.Str("segments.conversionAction") ?? "")) continue;
                    var k = AssetKey(r);
                    assetInstalls[k] = assetInstalls.GetValueOrDefault(k) + (r.Dec("metrics.conversions") ?? 0);
                }
            }
            assetRows = assets.GroupBy(AssetKey).Select(g =>
            {
                var r = g.First();
                return new AssetDailyRow(ParseDate(r.Str("segments.date")), r.Str("campaign.id"), r.Str("adGroup.id")!, r.Str("adGroupAd.ad.id")!,
                    r.Str("asset.id")!, r.Str("adGroupAdAssetView.fieldType") ?? "UNKNOWN", currency,
                    g.Sum(x => GoogleAdsMapping.Micros(x, "metrics.costMicros") ?? 0), g.Sum(x => x.Long("metrics.impressions") ?? 0),
                    g.Sum(x => x.Long("metrics.clicks") ?? 0), assetInstalls is null ? null : assetInstalls.GetValueOrDefault(g.Key));
            }).ToArray();

            creativeRows = [];
            foreach (var r in (await client.SearchAsync(id, GoogleAdsQueries.AssetMetadata(range), ct).ConfigureAwait(false))
                     .GroupBy(r => $"{r.Str("asset.id")}~{r.Str("adGroupAdAssetView.fieldType")}").Select(g => g.First()))
            {
                var type = r.Str("asset.type") ?? "UNKNOWN";
                var videoId = r.Str("asset.youtubeVideoAsset.youtubeVideoId");
                var image = r.Str("asset.imageAsset.fullSize.url");
                var w = r.Long("asset.imageAsset.fullSize.widthPixels");
                var h = r.Long("asset.imageAsset.fullSize.heightPixels");
                creativeRows.Add(new CreativeRow(
                    $"asset:{r.Str("asset.id")}:{r.Str("adGroupAdAssetView.fieldType")}", r.Str("adGroupAd.ad.id"), r.Str("asset.id"),
                    r.Str("asset.textAsset.text") ?? r.Str("asset.youtubeVideoAsset.youtubeVideoTitle") ?? r.Str("asset.name") ?? $"{Humanize(type)} #{r.Str("asset.id")}",
                    type switch { "TEXT" => "text", "IMAGE" => "image", "YOUTUBE_VIDEO" => "video", "MEDIA_BUNDLE" => "playable", _ => type.ToLowerInvariant() },
                    r.Str("adGroupAdAssetView.fieldType"),
                    videoId is not null ? $"https://www.youtube.com/watch?v={videoId}" : image,
                    videoId is not null ? $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg" : image,
                    videoId, null,
                    w is not null && h is not null ? $"{w}×{h}" : null, r.Str("asset.textAsset.text"), r.Str("adGroupAdAssetView.performanceLabel")));
            }
        }
        catch (PlatformApiException ex)
        {
            assetRows = null;
            creativeRows = null;
            result.Warnings.Add("Asset-level data unavailable: " + ex.Message);
        }

        // ---- write everything for this window in one transaction.
        using var c = facts.Db.Open();
        using var tx = c.BeginTransaction();
        result.Add(facts.ReplaceWindow(c, tx, "ad_daily", scope, "date", from, to, adRows), "ads");
        result.Add(facts.ReplaceWindow(c, tx, "campaign_country_daily", scope, "date", from, to, countryRows), "countries");
        if (assetRows is not null) result.Add(facts.ReplaceWindow(c, tx, "asset_daily", scope, "date", from, to, assetRows), "assets");
        tx.Commit();
        if (creativeRows is { Count: > 0 }) result.Records += facts.Upsert("creatives", scope, creativeRows);
        return result;
    }

    private static string AssetKey(JsonElement r) =>
        $"{r.Str("segments.date")}~{r.Str("adGroup.id")}~{r.Str("adGroupAd.ad.id")}~{r.Str("asset.id")}~{r.Str("adGroupAdAssetView.fieldType")}";

    private static string Key(params string?[] parts) => string.Join("~", parts);

    private static DateOnly ParseDate(string? s) => DateOnly.ParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Channel(string? type, string? sub) =>
        type is null ? null : sub is null or "UNSPECIFIED" or "UNKNOWN" ? type : $"{type}/{sub}";

    private static string Humanize(string? e) =>
        string.IsNullOrEmpty(e) ? "Ad" : string.Join(' ', e.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
