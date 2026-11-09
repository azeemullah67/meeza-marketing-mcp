using System.Globalization;
using System.Text.Json;
using Meeza.Marketing.Meta;
using Meeza.Marketing.Meta.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Sync;

/// <summary>Synchronizes Meta Ads ad-level and country-level daily insights, creatives and window reach (read-only Graph API).</summary>
public sealed class MetaSyncer(MetaAdsPlatform platform, MetaAdsClient client, FactStore facts) : IIntegrationSyncer
{
    private const int MaxRowsPerWindow = 100_000;

    public string Integration => Integrations.Meta;
    public bool IsConfigured => platform.IsConfigured;
    public string ConfigurationHint => platform.ConfigurationHint;
    // Meta's default attribution (7-day click / 1-day view) keeps revising the last week; re-read 10 days.
    public int LookbackDays => 10;
    public int ChunkDays => 14;

    public async Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct)
    {
        var a = (await platform.ResolveAccountsAsync(externalId, ct).ConfigureAwait(false)).Single();
        return new AccountInfo(a.AccountId, a.Name, a.Currency, a.TimeZone);
    }

    public async Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct)
    {
        var id = account.ExternalId;
        var range = new DateRange(from, to);
        var result = new WindowResult();
        var scope = new Dictionary<string, object?> { ["project_id"] = projectId, ["platform"] = Integration, ["account_id"] = id };

        // ---- required: ad-level daily insights (with status joined from the ad list).
        var (insights, complete) = await client.GetInsightsForSyncAsync(id, "ad", range, MetaAdsClient.SyncInsightFields, null, "1", MaxRowsPerWindow, ct).ConfigureAwait(false);
        if (!complete) throw new PlatformApiException(Integration, $"Insights for {range} exceeded {MaxRowsPerWindow} rows; the window was not stored. Reduce the sync chunk size.");
        var ads = await client.GetAdCreativesAsync(id, 20_000, ct).ConfigureAwait(false);
        var status = ads.Where(a => a.Str("id") is not null).GroupBy(a => a.Str("id")!).ToDictionary(g => g.Key, g => g.First());

        var adRows = insights.Select(r => ToAdRow(r, account, status, result)).ToArray();

        // ---- required: campaign x country daily.
        var (geo, geoComplete) = await client.GetInsightsForSyncAsync(id, "campaign", range, MetaAdsClient.InsightFields, "country", "1", MaxRowsPerWindow, ct).ConfigureAwait(false);
        if (!geoComplete) throw new PlatformApiException(Integration, $"Country insights for {range} exceeded {MaxRowsPerWindow} rows.");
        var countryRows = geo.Select(r =>
        {
            var p = MetaMapping.ToPerformance(r, range, account.TimeZone, id);
            return new CountryDailyRow(Date(r.Str("date_start")), p.CampaignId ?? "", p.CampaignName, (r.Str("country") ?? "unknown").ToUpperInvariant(),
                r.Str("account_currency") ?? account.Currency ?? "UNKNOWN", p.Spend, p.Impressions, p.Clicks, null, p.Installs);
        }).ToArray();

        // ---- optional: creatives (metadata, thumbnails, video length).
        var creatives = new List<CreativeRow>();
        try
        {
            using var c0 = facts.Db.Open();
            var known = c0.Query("SELECT creative_key, duration_seconds FROM creatives WHERE project_id=@projectId AND platform='meta' AND account_id=@id",
                new { projectId, id }, r => (Key: r.GetString(0), Dur: r.Dec("duration_seconds"))).ToDictionary(x => x.Key, x => x.Dur);
            var lookups = 0;
            foreach (var ad in ads.Where(a => a.Str("creative.id") is not null && adRows.Any(x => x.AdId == a.Str("id"))))
            {
                if (ad.Path("creative") is not { } cr) continue;
                var videoId = cr.Str("video_id");
                var key = $"creative:{cr.Str("id")}";
                decimal? duration = known.GetValueOrDefault(key);
                if (videoId is not null && duration is null && lookups++ < 40)
                {
                    try { duration = (await client.GetVideoAsync(videoId, ct).ConfigureAwait(false)).Dec("length"); }
                    catch (PlatformApiException) { /* video length is optional */ }
                }
                creatives.Add(new CreativeRow(key, ad.Str("id"), cr.Str("id"), cr.Str("name") ?? ad.Str("name"),
                    Format(cr.Str("object_type"), videoId), null, cr.Str("image_url") ?? cr.Str("thumbnail_url"), cr.Str("thumbnail_url"),
                    videoId, duration, cr.Str("title"), cr.Str("body"), null));
            }
        }
        catch (PlatformApiException ex)
        {
            result.Warnings.Add("Creative details unavailable: " + ex.Message);
        }

        // ---- optional: reach and frequency for standard trailing windows that end inside this window.
        var reachRows = new List<ReachWindowRow>();
        try
        {
            var yesterday = TimeZones.Today(account.TimeZone).AddDays(-1);
            if (yesterday >= from && yesterday <= to)
                foreach (var days in new[] { 7, 14, 30 })
                {
                    var w = new DateRange(yesterday.AddDays(1 - days), yesterday);
                    var (rows, _) = await client.GetInsightsForSyncAsync(id, "account", w, "reach,impressions,frequency", null, null, 10, ct).ConfigureAwait(false);
                    var r = rows.FirstOrDefault();
                    reachRows.Add(new ReachWindowRow(w.Start, w.End, r.ValueKind == JsonValueKind.Undefined ? 0 : r.Long("reach"),
                        r.ValueKind == JsonValueKind.Undefined ? 0 : r.Long("impressions"), r.ValueKind == JsonValueKind.Undefined ? null : r.Dec("frequency")));
                }
        }
        catch (PlatformApiException ex)
        {
            result.Warnings.Add("Reach/frequency unavailable: " + ex.Message);
        }

        using var c = facts.Db.Open();
        using var tx = c.BeginTransaction();
        result.Add(facts.ReplaceWindow(c, tx, "ad_daily", scope, "date", from, to, adRows), "ads");
        result.Add(facts.ReplaceWindow(c, tx, "campaign_country_daily", scope, "date", from, to, countryRows), "countries");
        tx.Commit();
        if (creatives.Count > 0) result.Records += facts.Upsert("creatives", scope, creatives);
        if (reachRows.Count > 0) result.Records += facts.Upsert("reach_windows", scope, reachRows);
        return result;
    }

    internal static AdDailyRow ToAdRow(JsonElement r, AccountInfo account, IReadOnlyDictionary<string, JsonElement> ads, WindowResult result)
    {
        var date = Date(r.Str("date_start"));
        var actions = MetaMapping.Actions(r);
        var (installs, source) = MetaMapping.Installs(actions);
        var values = MetaMapping.Actions(r, "action_values");
        decimal? value = values.Count == 0 ? null : values.Where(kv => kv.Key.Contains("purchase", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
        var spend = r.Dec("spend");
        var imps = r.Long("impressions");
        if (spend > 0 || imps > 0) result.Seen(date);
        long? Sum(string field) { var a = MetaMapping.Actions(r, field); return a.Count == 0 ? null : (long)a.Values.Sum(); }
        var adId = r.Str("ad_id") ?? "";
        var ad = ads.TryGetValue(adId, out var x) ? x : default;
        string? AdStr(string f) => ad.ValueKind == JsonValueKind.Undefined ? null : ad.Str(f);
        return new AdDailyRow(
            date, r.Str("campaign_id") ?? "", r.Str("campaign_name"), null, r.Str("objective"),
            r.Str("adset_id") ?? "", r.Str("adset_name"), null, adId, r.Str("ad_name"), AdStr("effective_status"),
            r.Str("account_currency") ?? account.Currency ?? "UNKNOWN",
            spend, imps, r.Long("reach"), r.Long("clicks"), null,
            // Meta omits the action entirely when there were no installs. With install tracking that means zero, but we
            // cannot tell tracking apart from zero per row, so absence stays null ("not reported").
            installs, source, value,
            Sum("video_play_actions"), Sum("video_p25_watched_actions"), Sum("video_p100_watched_actions"), Sum("video_thruplay_watched_actions"));
    }

    private static string Format(string? objectType, string? videoId) => (objectType ?? "").ToUpperInvariant() switch
    {
        _ when videoId is not null => "video",
        "VIDEO" => "video",
        "PHOTO" or "IMAGE" or "SHARE" => "image",
        "PLAYABLE" => "playable",
        _ => objectType?.ToLowerInvariant() ?? "unknown",
    };

    private static DateOnly Date(string? s) => DateOnly.ParseExact(s ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
