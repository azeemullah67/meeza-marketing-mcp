using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Microsoft.Extensions.Logging;

namespace Meeza.Marketing.Meta;

/// <summary>
/// Read-only client for the Meta Marketing API (Graph API). Only GET requests are ever issued.
/// The access token is sent in the Authorization header, never in the URL.
/// </summary>
public sealed class MetaAdsClient(HttpClient http, MetaAdsOptions initialOptions, ILogger<MetaAdsClient> logger)
{
    public const string HttpClientName = "meta";
    private const string GraphHost = "https://graph.facebook.com";

    // Graph API throttling error codes (application, account, user and ads-management limits).
    private static readonly HashSet<int> ThrottleCodes = [4, 17, 32, 613, 80000, 80001, 80002, 80003, 80004, 80005, 80006, 80008, 80009, 80014];

    private volatile MetaAdsOptions _options = initialOptions;

    public MetaAdsOptions Options => _options;

    /// <summary>Applies new settings (from the dashboard's Settings page) without restarting.</summary>
    public void UpdateOptions(MetaAdsOptions options) => _options = options;

    public const string AccountFields = "id,account_id,name,currency,timezone_name,account_status,business_name";

    public const string InsightFields =
        "account_id,account_name,account_currency,campaign_id,campaign_name,adset_id,adset_name,ad_id,ad_name," +
        "objective,spend,impressions,reach,clicks,ctr,cpm,cpc,actions,cost_per_action_type,date_start,date_stop";

    public Task<IReadOnlyList<JsonElement>> GetAdAccountsAsync(CancellationToken ct) =>
        GetPagedAsync("me/adaccounts", new() { ["fields"] = AccountFields }, 500, ct);

    public async Task<JsonElement> GetAdAccountAsync(string accountId, CancellationToken ct) =>
        await GetObjectAsync(MetaAdsOptions.NormalizeAccountId(accountId), new() { ["fields"] = AccountFields }, ct).ConfigureAwait(false);

    public Task<IReadOnlyList<JsonElement>> GetCampaignsAsync(string accountId, IReadOnlyList<string>? effectiveStatuses, int limit, CancellationToken ct)
    {
        var q = new Dictionary<string, string>
        {
            ["fields"] = "id,name,status,effective_status,objective,buying_type,daily_budget,lifetime_budget,start_time,stop_time,created_time",
        };
        if (effectiveStatuses is { Count: > 0 }) q["effective_status"] = JsonSerializer.Serialize(effectiveStatuses);
        return GetPagedAsync($"{accountId}/campaigns", q, limit, ct);
    }

    public Task<IReadOnlyList<JsonElement>> GetAdSetsAsync(string accountId, string? campaignId, int limit, CancellationToken ct)
    {
        var parent = string.IsNullOrWhiteSpace(campaignId) ? accountId : ValidateId(campaignId);
        return GetPagedAsync($"{parent}/adsets", new()
        {
            ["fields"] = "id,name,status,effective_status,campaign_id,optimization_goal,billing_event,bid_strategy," +
                         "daily_budget,lifetime_budget,start_time,end_time,promoted_object",
        }, limit, ct);
    }

    public Task<IReadOnlyList<JsonElement>> GetAdsAsync(string accountId, string? adSetId, int limit, CancellationToken ct)
    {
        var parent = string.IsNullOrWhiteSpace(adSetId) ? accountId : ValidateId(adSetId);
        return GetPagedAsync($"{parent}/ads", new()
        {
            ["fields"] = "id,name,status,effective_status,campaign_id,adset_id,creative{id,name,title,object_type},created_time",
        }, limit, ct);
    }

    /// <summary>
    /// Queries the insights edge of an account, campaign, ad set or ad.
    /// </summary>
    /// <param name="objectId">act_… account ID or a campaign/ad set/ad ID.</param>
    /// <param name="level">account, campaign, adset or ad.</param>
    /// <param name="breakdowns">Optional comma-separated breakdowns, e.g. "country".</param>
    /// <param name="timeIncrement">"all_days" (default), "monthly" or a number of days such as "1".</param>
    public Task<IReadOnlyList<JsonElement>> GetInsightsAsync(
        string objectId, string level, DateRange range, string? breakdowns, string? timeIncrement, int limit, CancellationToken ct)
    {
        var q = new Dictionary<string, string>
        {
            ["level"] = level,
            ["fields"] = InsightFields,
            ["time_range"] = JsonSerializer.Serialize(new { since = range.StartIso, until = range.EndIso }),
            ["limit"] = "500",
        };
        if (!string.IsNullOrWhiteSpace(breakdowns)) q["breakdowns"] = breakdowns;
        if (!string.IsNullOrWhiteSpace(timeIncrement) && timeIncrement != "all_days") q["time_increment"] = timeIncrement;
        return GetPagedAsync($"{objectId}/insights", q, limit, ct);
    }

    /// <summary>Insight fields used by the synchronizer: the standard fields plus video engagement and conversion values.</summary>
    public const string SyncInsightFields = InsightFields +
        ",action_values,video_play_actions,video_p25_watched_actions,video_p100_watched_actions,video_thruplay_watched_actions";

    /// <summary>
    /// Insights for synchronization: follows every page (up to <paramref name="maxItems"/>) and reports whether the
    /// result is complete, so a truncated download is never stored as if it were the full window.
    /// </summary>
    public async Task<(IReadOnlyList<JsonElement> Rows, bool Complete)> GetInsightsForSyncAsync(
        string objectId, string level, DateRange range, string fields, string? breakdowns, string? timeIncrement, int maxItems, CancellationToken ct)
    {
        var q = new Dictionary<string, string>
        {
            ["level"] = level,
            ["fields"] = fields,
            ["time_range"] = JsonSerializer.Serialize(new { since = range.StartIso, until = range.EndIso }),
            ["limit"] = "500",
        };
        if (!string.IsNullOrWhiteSpace(breakdowns)) q["breakdowns"] = breakdowns;
        if (!string.IsNullOrWhiteSpace(timeIncrement) && timeIncrement != "all_days") q["time_increment"] = timeIncrement;
        var results = new List<JsonElement>();
        string? url = BuildUrl($"{objectId}/insights", q);
        var pages = 0;
        while (url is not null)
        {
            if (results.Count >= maxItems || pages++ >= 400) return (results, false);
            using var doc = await SendAsync(url, ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.Arr("data")) results.Add(item.Clone());
            url = doc.RootElement.Str("paging.next");
            if (url is not null && !url.StartsWith(GraphHost, StringComparison.OrdinalIgnoreCase)) url = null;
        }
        return (results, true);
    }

    /// <summary>Ads with their creative details (thumbnail, image, video, copy) for the creative library.</summary>
    public Task<IReadOnlyList<JsonElement>> GetAdCreativesAsync(string accountId, int limit, CancellationToken ct) =>
        GetPagedAsync($"{MetaAdsOptions.NormalizeAccountId(accountId)}/ads", new()
        {
            ["fields"] = "id,name,effective_status,campaign_id,adset_id,creative{id,name,title,body,object_type,thumbnail_url,image_url,video_id,call_to_action_type}",
        }, limit, ct);

    /// <summary>Length (seconds) and title of an ad video.</summary>
    public Task<JsonElement> GetVideoAsync(string videoId, CancellationToken ct) =>
        GetObjectAsync(ValidateId(videoId), new() { ["fields"] = "id,length,title,picture" }, ct);

    // ---------------------------------------------------------------- transport

    private async Task<JsonElement> GetObjectAsync(string path, Dictionary<string, string> query, CancellationToken ct)
    {
        using var doc = await SendAsync(BuildUrl(path, query), ct).ConfigureAwait(false);
        return doc.RootElement.Clone();
    }

    private async Task<IReadOnlyList<JsonElement>> GetPagedAsync(string path, Dictionary<string, string> query, int maxItems, CancellationToken ct)
    {
        var results = new List<JsonElement>();
        string? url = BuildUrl(path, query);
        var pages = 0;
        while (url is not null && results.Count < maxItems && pages++ < 200)
        {
            using var doc = await SendAsync(url, ct).ConfigureAwait(false);
            foreach (var item in doc.RootElement.Arr("data"))
            {
                results.Add(item.Clone());
                if (results.Count >= maxItems) break;
            }
            url = doc.RootElement.Str("paging.next");
            if (url is not null && !url.StartsWith(GraphHost, StringComparison.OrdinalIgnoreCase)) url = null; // never follow off-host links
        }
        return results;
    }

    private string BuildUrl(string path, Dictionary<string, string> query)
    {
        var sb = new StringBuilder($"{GraphHost}/{_options.ApiVersion}/{path.TrimStart('/')}?");
        foreach (var (k, v) in query) sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v)).Append('&');
        if (!string.IsNullOrEmpty(_options.AppSecret)) sb.Append("appsecret_proof=").Append(AppSecretProof(_options.AccessToken!, _options.AppSecret)).Append('&');
        return sb.ToString().TrimEnd('&', '?');
    }

    public static string AppSecretProof(string accessToken, string appSecret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(accessToken));
        return Convert.ToHexStringLower(hash);
    }

    private async Task<JsonDocument> SendAsync(string url, CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new PlatformNotConfiguredException(MetaAdsOptions.Platform, MetaAdsOptions.Hint);

        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body);

            var error = MetaError.Parse(body);
            if (error is not null && ThrottleCodes.Contains(error.Code) && attempt < 3)
            {
                var delay = TimeSpan.FromSeconds(10 * Math.Pow(2, attempt));
                logger.LogWarning("Meta API rate limit (code {Code}); retrying in {Delay}s.", error.Code, delay.TotalSeconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            throw ToException(resp.StatusCode, error);
        }
    }

    private static PlatformApiException ToException(HttpStatusCode status, MetaError? error)
    {
        if (error is null) return new PlatformApiException(MetaAdsOptions.Platform, $"HTTP {(int)status} with an unreadable error body.", status);
        var hint = error.Code switch
        {
            190 => " The access token is invalid or expired. Generate a new token (System User tokens do not expire).",
            200 or 10 or 294 => " The token lacks permission. It needs ads_read and access to this ad account.",
            100 => " Check the requested IDs, fields and date range.",
            _ when ThrottleCodes.Contains(error.Code) => " Rate limit reached; try again later or request fewer objects.",
            _ => "",
        };
        var trace = error.TraceId is null ? "" : $" (fbtrace_id {error.TraceId})";
        return new PlatformApiException(MetaAdsOptions.Platform, $"{error.Message} [code {error.Code}{(error.Subcode is null ? "" : $"/{error.Subcode}")}]{trace}.{hint}", status, error.Code.ToString());
    }

    private static string ValidateId(string id)
    {
        var t = id.Trim();
        if (t.Length == 0 || !t.All(char.IsAsciiDigit)) throw new ArgumentException($"'{id}' is not a valid Meta object ID.");
        return t;
    }

    private sealed record MetaError(string Message, int Code, int? Subcode, string? TraceId)
    {
        public static MetaError? Parse(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.Prop("error") is not { } e) return null;
                return new MetaError(e.Str("message") ?? "Unknown error", (int)(e.Long("code") ?? 0), (int?)e.Long("error_subcode"), e.Str("fbtrace_id"));
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
