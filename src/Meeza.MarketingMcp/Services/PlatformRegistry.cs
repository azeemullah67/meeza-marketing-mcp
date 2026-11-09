using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.Meta;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Interfaces;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.MarketingMcp.Services;

/// <summary>Fans requests out to the configured ad platforms and collects per-platform failures without aborting.</summary>
public sealed class PlatformRegistry(MetaAdsPlatform meta, GoogleAdsPlatform googleAds, PlayConsoleService play)
{
    public IReadOnlyList<IAdPlatform> AdPlatforms { get; } = [meta, googleAds];
    public PlayConsoleService Play => play;

    public static readonly string[] KnownPlatforms = [MetaAdsOptions.Platform, GoogleAdsOptions.Platform, PlayConsoleOptions.Platform, Meeza.Marketing.Analytics.AnalyticsOptions.Platform];

    /// <summary>Parses a platform filter such as "meta,google_ads". Null/empty means all.</summary>
    public IReadOnlyList<IAdPlatform> Select(string? platforms)
    {
        if (string.IsNullOrWhiteSpace(platforms)) return AdPlatforms;
        var wanted = platforms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant() switch
            {
                "facebook" or "instagram" or "meta_ads" => MetaAdsOptions.Platform,
                "google" or "googleads" or "google-ads" or "adwords" => GoogleAdsOptions.Platform,
                var other => other,
            })
            .ToHashSet();
        var unknown = wanted.Where(w => AdPlatforms.All(p => p.Platform != w)).ToArray();
        if (unknown.Length > 0)
            throw new ArgumentException($"Unknown ad platform(s): {string.Join(", ", unknown)}. Use: {string.Join(", ", AdPlatforms.Select(p => p.Platform))}.");
        return AdPlatforms.Where(p => wanted.Contains(p.Platform)).ToArray();
    }

    /// <summary>
    /// Collects campaign performance from each selected platform in parallel. Unconfigured platforms are
    /// skipped with a note; failures are recorded in <paramref name="response"/> and the rest still return.
    /// </summary>
    public Task<IReadOnlyList<CampaignPerformance>> CollectAsync(
        string? platforms, DateRange range, bool byCountry, ToolResponse response, CancellationToken ct) =>
        FanOutAsync(platforms, response, p => p.GetCampaignPerformanceAsync(null, range, byCountry, ct));

    /// <summary>Campaign, ad set / ad group or ad level rows from every selected platform.</summary>
    public Task<IReadOnlyList<CampaignPerformance>> CollectLevelAsync(
        string? platforms, DateRange range, ReportLevel level, ToolResponse response, CancellationToken ct) =>
        FanOutAsync(platforms, response, p => p.GetPerformanceAsync(null, range, level, ct));

    /// <summary>Daily account-level series from every selected platform.</summary>
    public Task<IReadOnlyList<DailyPerformance>> CollectDailyAsync(string? platforms, DateRange range, ToolResponse response, CancellationToken ct) =>
        FanOutAsync(platforms, response, p => p.GetDailyAsync(null, range, ct));

    /// <summary>Per-asset rows (headlines, images, videos) from platforms that report them.</summary>
    public Task<IReadOnlyList<AssetPerformance>> CollectAssetsAsync(string? platforms, DateRange range, ToolResponse response, CancellationToken ct) =>
        FanOutAsync(platforms, response, p => p.GetAssetPerformanceAsync(null, range, ct));

    private async Task<IReadOnlyList<T>> FanOutAsync<T>(string? platforms, ToolResponse response, Func<IAdPlatform, Task<IReadOnlyList<T>>> fetch)
    {
        var selected = Select(platforms);
        var tasks = selected.Select(async p =>
        {
            if (!p.IsConfigured)
            {
                lock (response)
                {
                    var note = $"{p.Platform} is not configured and was skipped. {p.ConfigurationHint}";
                    if (!response.Notes.Contains(note)) response.Notes.Add(note);
                }
                return Array.Empty<T>();
            }
            try
            {
                return (await fetch(p).ConfigureAwait(false)).ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lock (response) response.Errors.Add(new PlatformError(p.Platform, ToolRunner.Describe(ex)));
                return Array.Empty<T>();
            }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var rows = results.SelectMany(r => r).ToArray();
        var configured = selected.Count(p => p.IsConfigured);
        if (configured == 0)
        {
            response.Ok = false;
            response.Errors.Add(new PlatformError("server", "No requested ad platform is configured. Run get_connected_accounts for setup hints."));
        }
        else
        {
            // Ok=false only when EVERY configured platform failed. Count distinct failed platforms, not error
            // entries: several calls sharing one response (rows + daily) can each log an error for the same platform,
            // which used to mark a response failed even though another platform returned fresh data.
            var failedPlatforms = response.Errors.Select(e => e.Platform).Where(p => selected.Any(s => s.IsConfigured && s.Platform == p)).Distinct().Count();
            if (failedPlatforms >= configured) response.Ok = false;
        }
        return rows;
    }
}
