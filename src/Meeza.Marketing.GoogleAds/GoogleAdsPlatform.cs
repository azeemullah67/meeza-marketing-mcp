using System.Collections.Concurrent;
using System.Text.Json;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Shared.Interfaces;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Microsoft.Extensions.Logging;

namespace Meeza.Marketing.GoogleAds;

/// <summary>Google Ads adapter for the unified reporting tools. Enforces the customer allow-list.</summary>
public sealed class GoogleAdsPlatform(GoogleAdsClient client, ILogger<GoogleAdsPlatform> logger) : IAdPlatform
{
    private const int MaxAccounts = 25;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly ConcurrentDictionary<long, string?> _geoCache = new();
    private IReadOnlyList<AdAccount>? _accounts;
    private DateTimeOffset _fetchedAt;

    public string Platform => GoogleAdsOptions.Platform;
    public bool IsConfigured => client.Options.IsConfigured;
    public string ConfigurationHint => GoogleAdsOptions.Hint;

    /// <summary>Forgets cached accounts (after settings change).</summary>
    public void ResetCache() => _accounts = null;
    public GoogleAdsClient Client => client;

    public async Task<IReadOnlyList<AdAccount>> GetAccountsAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PlatformNotConfiguredException(Platform, ConfigurationHint);
        if (_accounts is not null && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromMinutes(10)) return _accounts;

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accounts is not null && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromMinutes(10)) return _accounts;
            _accounts = await LoadAccountsAsync(ct).ConfigureAwait(false);
            _fetchedAt = DateTimeOffset.UtcNow;
            return _accounts;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private async Task<IReadOnlyList<AdAccount>> LoadAccountsAsync(CancellationToken ct)
    {
        var opts = client.Options;
        var result = new Dictionary<string, AdAccount>();

        if (opts.CustomerIds.Count > 0)
        {
            foreach (var id in opts.CustomerIds)
            {
                var rows = await client.SearchAsync(id, GoogleAdsQueries.Customer, ct).ConfigureAwait(false);
                if (rows.Count > 0) result[id] = GoogleAdsMapping.FromCustomer(rows[0]);
            }
            return result.Values.ToArray();
        }

        if (opts.LoginCustomerId is { } manager)
        {
            // Manager account: enumerate its direct client accounts (and the manager itself, level 0).
            var rows = await client.SearchAsync(manager, GoogleAdsQueries.CustomerClients, ct).ConfigureAwait(false);
            foreach (var r in rows.Select(GoogleAdsMapping.FromCustomerClient)) result[r.AccountId] = r;
            return result.Values.ToArray();
        }

        foreach (var id in await client.ListAccessibleCustomersAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var rows = await client.SearchAsync(id, GoogleAdsQueries.Customer, ct, loginCustomerIdOverride: id).ConfigureAwait(false);
                if (rows.Count > 0) result[id] = GoogleAdsMapping.FromCustomer(rows[0]);
            }
            catch (PlatformApiException ex)
            {
                logger.LogInformation("Skipping customer {Id}: {Error}", id, ex.Message);
            }
        }
        return result.Values.ToArray();
    }

    /// <summary>Reporting accounts (manager accounts excluded, since they have no metrics of their own).</summary>
    public async Task<IReadOnlyList<AdAccount>> ResolveAccountsAsync(string? customerId, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(customerId))
        {
            var reporting = accounts.Where(a => a.IsManager != true).ToArray();
            if (reporting.Length > MaxAccounts)
                throw new ArgumentException($"{reporting.Length} Google Ads accounts are accessible. Pass customerId or set GOOGLE_ADS_CUSTOMER_IDS.");
            return reporting;
        }

        var id = GoogleAdsOptions.NormalizeCustomerId(customerId);
        var match = accounts.FirstOrDefault(a => a.AccountId == id);
        if (match is null)
            throw new AccountAccessDeniedException(Platform, id,
                client.Options.CustomerIds.Count > 0 ? "it is not in GOOGLE_ADS_CUSTOMER_IDS." : "it is not accessible with the configured credentials.");
        if (match.IsManager == true)
            throw new ArgumentException($"{id} is a manager account; metrics must be requested for its client accounts.");
        return [match];
    }

    public async Task<IReadOnlyList<CampaignPerformance>> GetCampaignPerformanceAsync(string? accountId, DateRange range, bool byCountry, CancellationToken ct)
    {
        var rows = new List<CampaignPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
            rows.AddRange(byCountry
                ? await CountryRowsAsync(account, range, ct).ConfigureAwait(false)
                : await CampaignRowsAsync(account, range, ct).ConfigureAwait(false));
        return rows;
    }

    /// <summary>True when the account has an enabled app-install conversion action (so a missing value means zero installs).</summary>
    public async Task<bool> HasInstallTrackingAsync(AdAccount account, CancellationToken ct) =>
        (await client.SearchAsync(account.AccountId, GoogleAdsQueries.InstallConversionActions, ct).ConfigureAwait(false)).Count > 0;

    private async Task<IReadOnlyList<CampaignPerformance>> CampaignRowsAsync(AdAccount account, DateRange range, CancellationToken ct)
    {
        var metrics = await client.SearchAsync(account.AccountId, GoogleAdsQueries.CampaignPerformance(range, daily: false), ct).ConfigureAwait(false);
        Dictionary<string, decimal>? installs = null;
        if (await HasInstallTrackingAsync(account, ct).ConfigureAwait(false))
        {
            installs = [];
            foreach (var r in await client.SearchAsync(account.AccountId, GoogleAdsQueries.CampaignInstalls(range), ct).ConfigureAwait(false))
            {
                var id = r.Str("campaign.id")!;
                installs[id] = installs.GetValueOrDefault(id) + (r.Dec("metrics.conversions") ?? 0);
            }
        }

        return metrics.Select(r =>
        {
            var id = r.Str("campaign.id")!;
            decimal? inst = installs is null ? null : installs.GetValueOrDefault(id);
            return GoogleAdsMapping.ToPerformance(r, account, range, null, inst);
        }).ToArray();
    }

    private async Task<IReadOnlyList<CampaignPerformance>> CountryRowsAsync(AdAccount account, DateRange range, CancellationToken ct)
    {
        var metrics = await client.SearchAsync(account.AccountId, GoogleAdsQueries.CountryPerformance(range), ct).ConfigureAwait(false);
        Dictionary<(string, long), decimal>? installs = null;
        string? installNote = null;
        if (await HasInstallTrackingAsync(account, ct).ConfigureAwait(false))
        {
            try
            {
                installs = [];
                foreach (var r in await client.SearchAsync(account.AccountId, GoogleAdsQueries.CountryInstalls(range), ct).ConfigureAwait(false))
                {
                    var key = (r.Str("campaign.id")!, r.Long("geographicView.countryCriterionId") ?? 0);
                    installs[key] = installs.GetValueOrDefault(key) + (r.Dec("metrics.conversions") ?? 0);
                }
            }
            catch (PlatformApiException ex)
            {
                installs = null;
                installNote = "Installs by country could not be retrieved: " + ex.Message;
            }
        }

        // Several geographic_view rows can exist per campaign/country (e.g. per location type); merge them.
        var grouped = metrics.GroupBy(r => (Campaign: r.Str("campaign.id")!, Geo: r.Long("geographicView.countryCriterionId") ?? 0));
        var geoCodes = await ResolveCountriesAsync(account, grouped.Select(g => g.Key.Geo).Where(g => g > 0).Distinct(), ct).ConfigureAwait(false);

        var rows = new List<CampaignPerformance>();
        foreach (var g in grouped)
        {
            var parts = g.Select(r => GoogleAdsMapping.ToPerformance(r, account, range, geoCodes.GetValueOrDefault(g.Key.Geo) ?? $"geo:{g.Key.Geo}", null)).ToList();
            var merged = Aggregation.MergeCountries(parts).Single();
            decimal? inst = installs is null ? null : installs.GetValueOrDefault(g.Key);
            rows.Add((merged with
            {
                Country = parts[0].Country,
                Installs = inst,
                InstallSource = inst is null ? null : GoogleAdsMapping.InstallSource,
                Notes = inst is null ? [installNote ?? "No enabled app-install (DOWNLOAD) conversion action in this account, so installs are unavailable."] : null,
            }).WithDerivedMetrics());
        }
        return rows;
    }

    public async Task<Dictionary<long, string?>> ResolveCountriesAsync(AdAccount account, IEnumerable<long> ids, CancellationToken ct)
    {
        var missing = ids.Where(i => !_geoCache.ContainsKey(i)).ToArray();
        foreach (var chunk in missing.Chunk(200))
        {
            foreach (var r in await client.SearchAsync(account.AccountId, GoogleAdsQueries.GeoTargets(chunk), ct).ConfigureAwait(false))
                if (r.Long("geoTargetConstant.id") is { } id) _geoCache[id] = r.Str("geoTargetConstant.countryCode");
        }
        return ids.ToDictionary(i => i, i => _geoCache.GetValueOrDefault(i));
    }

    public async Task<IReadOnlyList<CampaignPerformance>> GetPerformanceAsync(string? accountId, DateRange range, ReportLevel level, CancellationToken ct)
    {
        if (level == ReportLevel.Campaign)
            return (await GetCampaignPerformanceAsync(accountId, range, byCountry: false, ct).ConfigureAwait(false))
                .Select(p => p with { Status = p.CampaignStatus }).ToArray();

        var rows = new List<CampaignPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var metricsQuery = level == ReportLevel.Ad ? GoogleAdsQueries.AdPerformance(range) : GoogleAdsQueries.AdGroupPerformance(range);
            var metrics = await client.SearchAsync(account.AccountId, metricsQuery, ct).ConfigureAwait(false);

            Dictionary<string, decimal>? installs = null;
            if (await HasInstallTrackingAsync(account, ct).ConfigureAwait(false))
            {
                installs = [];
                var q = level == ReportLevel.Ad ? GoogleAdsQueries.AdInstalls(range) : GoogleAdsQueries.AdGroupInstalls(range);
                foreach (var r in await client.SearchAsync(account.AccountId, q, ct).ConfigureAwait(false))
                {
                    var key = Key(r, level);
                    installs[key] = installs.GetValueOrDefault(key) + (r.Dec("metrics.conversions") ?? 0);
                }
            }

            rows.AddRange(metrics.Select(r =>
            {
                decimal? inst = installs is null ? null : installs.GetValueOrDefault(Key(r, level));
                return GoogleAdsMapping.ToLevelPerformance(r, account, range, level, inst);
            }));
        }
        return rows;

        static string Key(JsonElement r, ReportLevel level) =>
            level == ReportLevel.Ad ? $"{r.Str("adGroup.id")}~{r.Str("adGroupAd.ad.id")}" : r.Str("adGroup.id") ?? "";
    }

    /// <summary>
    /// Per-asset results (headlines, descriptions, images, videos) for asset-based ads such as App campaigns,
    /// with Google's performance label. Installs are conversions from the account's enabled DOWNLOAD actions.
    /// </summary>
    public async Task<IReadOnlyList<AssetPerformance>> GetAssetPerformanceAsync(string? accountId, DateRange range, CancellationToken ct)
    {
        var rows = new List<AssetPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var metrics = await client.SearchAsync(account.AccountId, GoogleAdsQueries.AssetPerformance(range), ct).ConfigureAwait(false);
            if (metrics.Count == 0) continue;

            Dictionary<string, decimal>? installs = null;
            var installActions = (await client.SearchAsync(account.AccountId, GoogleAdsQueries.InstallConversionActions, ct).ConfigureAwait(false))
                .Select(r => r.Str("conversionAction.id")).Where(id => id is not null)
                .Select(id => $"customers/{account.AccountId}/conversionActions/{id}")
                .ToHashSet(StringComparer.Ordinal);
            if (installActions.Count > 0)
            {
                installs = [];
                foreach (var r in await client.SearchAsync(account.AccountId, GoogleAdsQueries.AssetConversionsByAction(range), ct).ConfigureAwait(false))
                {
                    if (!installActions.Contains(r.Str("segments.conversionAction") ?? "")) continue;
                    var key = GoogleAdsMapping.AssetKey(r);
                    installs[key] = installs.GetValueOrDefault(key) + (r.Dec("metrics.conversions") ?? 0);
                }
            }

            rows.AddRange(metrics.Select(r =>
            {
                decimal? inst = installs is null ? null : installs.GetValueOrDefault(GoogleAdsMapping.AssetKey(r));
                return GoogleAdsMapping.ToAssetPerformance(r, account, range, inst);
            }));
        }
        return rows;
    }

    public async Task<IReadOnlyList<DailyPerformance>> GetDailyAsync(string? accountId, DateRange range, CancellationToken ct)
    {
        var rows = new List<DailyPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var metrics = await client.SearchAsync(account.AccountId, GoogleAdsQueries.DailyAccount(range), ct).ConfigureAwait(false);
            Dictionary<string, decimal>? installs = null;
            if (await HasInstallTrackingAsync(account, ct).ConfigureAwait(false))
            {
                installs = [];
                foreach (var r in await client.SearchAsync(account.AccountId, GoogleAdsQueries.DailyInstalls(range), ct).ConfigureAwait(false))
                {
                    var d = r.Str("segments.date") ?? "";
                    installs[d] = installs.GetValueOrDefault(d) + (r.Dec("metrics.conversions") ?? 0);
                }
            }
            foreach (var r in metrics)
            {
                var date = r.Str("segments.date");
                if (!DateOnly.TryParse(date, System.Globalization.CultureInfo.InvariantCulture, out var day)) continue;
                rows.Add(new DailyPerformance(Platform, account.AccountId, account.Currency ?? "UNKNOWN", day,
                    GoogleAdsMapping.Micros(r, "metrics.costMicros") ?? 0, r.Long("metrics.impressions") ?? 0, r.Long("metrics.clicks") ?? 0,
                    installs is null ? null : installs.GetValueOrDefault(date!)));
            }
        }
        return rows;
    }

    /// <summary>Runs an arbitrary validated query per resolved account (used by the tools layer).</summary>
    public async Task<IReadOnlyList<(AdAccount Account, IReadOnlyList<JsonElement> Rows)>> QueryAsync(string? customerId, string gaql, CancellationToken ct)
    {
        var result = new List<(AdAccount, IReadOnlyList<JsonElement>)>();
        foreach (var account in await ResolveAccountsAsync(customerId, ct).ConfigureAwait(false))
            result.Add((account, await client.SearchAsync(account.AccountId, gaql, ct).ConfigureAwait(false)));
        return result;
    }
}
