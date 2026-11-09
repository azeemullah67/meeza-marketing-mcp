using Meeza.Marketing.Meta.Models;
using Meeza.Marketing.Shared.Interfaces;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Meta;

/// <summary>Meta Ads adapter for the unified reporting tools. Enforces the account allow-list.</summary>
public sealed class MetaAdsPlatform(MetaAdsClient client) : IAdPlatform
{
    private const int MaxAccounts = 25;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private IReadOnlyList<AdAccount>? _accounts;
    private DateTimeOffset _accountsFetchedAt;

    public string Platform => MetaAdsOptions.Platform;
    public bool IsConfigured => client.Options.IsConfigured;
    public string ConfigurationHint => MetaAdsOptions.Hint;

    /// <summary>Forgets cached accounts (after settings change).</summary>
    public void ResetCache() => _accounts = null;

    /// <summary>
    /// Accounts this server may query: the allow-list when configured (verified against the token),
    /// otherwise every account the token can access. Cached for 10 minutes.
    /// </summary>
    public async Task<IReadOnlyList<AdAccount>> GetAccountsAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PlatformNotConfiguredException(Platform, ConfigurationHint);
        if (_accounts is not null && DateTimeOffset.UtcNow - _accountsFetchedAt < TimeSpan.FromMinutes(10)) return _accounts;

        await _cacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accounts is not null && DateTimeOffset.UtcNow - _accountsFetchedAt < TimeSpan.FromMinutes(10)) return _accounts;
            var allowed = client.Options.AdAccountIds;
            IReadOnlyList<AdAccount> accounts;
            if (allowed.Count > 0)
            {
                var list = new List<AdAccount>();
                foreach (var id in allowed)
                    list.Add(MetaMapping.ToAccount(await client.GetAdAccountAsync(id, ct).ConfigureAwait(false)));
                accounts = list;
            }
            else
            {
                accounts = (await client.GetAdAccountsAsync(ct).ConfigureAwait(false)).Select(MetaMapping.ToAccount).ToArray();
            }
            _accounts = accounts;
            _accountsFetchedAt = DateTimeOffset.UtcNow;
            return accounts;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>Resolves the target accounts for a request and validates access to an explicitly requested one.</summary>
    public async Task<IReadOnlyList<AdAccount>> ResolveAccountsAsync(string? accountId, CancellationToken ct)
    {
        var accounts = await GetAccountsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            if (accounts.Count > MaxAccounts)
                throw new ArgumentException($"The token can access {accounts.Count} ad accounts. Pass accountId, or set META_AD_ACCOUNT_IDS to limit the accounts queried.");
            return accounts;
        }

        var normalized = MetaAdsOptions.NormalizeAccountId(accountId);
        var match = accounts.FirstOrDefault(a => string.Equals(a.AccountId, normalized, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? throw new AccountAccessDeniedException(Platform, normalized,
                client.Options.AdAccountIds.Count > 0 ? "it is not in META_AD_ACCOUNT_IDS." : "the access token cannot access it.")
            : [match];
    }

    public async Task<IReadOnlyList<CampaignPerformance>> GetCampaignPerformanceAsync(
        string? accountId, DateRange range, bool byCountry, CancellationToken ct)
    {
        var rows = new List<CampaignPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var insights = await client.GetInsightsAsync(
                account.AccountId, "campaign", range, byCountry ? "country" : null, null, 10_000, ct).ConfigureAwait(false);
            rows.AddRange(insights.Select(r => MetaMapping.ToPerformance(r, range, account.TimeZone, account.AccountId) with
            {
                AccountName = account.Name,
                Currency = r.Str("account_currency") ?? account.Currency ?? "UNKNOWN",
            }));
        }
        return rows;
    }

    public async Task<IReadOnlyList<CampaignPerformance>> GetPerformanceAsync(
        string? accountId, DateRange range, ReportLevel level, CancellationToken ct)
    {
        var rows = new List<CampaignPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var insightsTask = client.GetInsightsAsync(account.AccountId, level.Name(), range, null, null, 20_000, ct);
            // Insights carry no delivery status, so join it from the object list.
            var statusTask = level switch
            {
                ReportLevel.Ad => client.GetAdsAsync(account.AccountId, null, 5_000, ct),
                ReportLevel.AdSet => client.GetAdSetsAsync(account.AccountId, null, 5_000, ct),
                _ => client.GetCampaignsAsync(account.AccountId, null, 5_000, ct),
            };
            await Task.WhenAll(insightsTask, statusTask).ConfigureAwait(false);

            var status = statusTask.Result
                .Where(o => o.Str("id") is not null)
                .GroupBy(o => o.Str("id")!)
                .ToDictionary(g => g.Key, g => g.First().Str("effective_status") ?? g.First().Str("status"));

            rows.AddRange(insightsTask.Result.Select(r =>
            {
                var p = MetaMapping.ToPerformance(r, range, account.TimeZone, account.AccountId, level);
                var objectId = level switch { ReportLevel.Ad => p.AdId, ReportLevel.AdSet => p.AdSetId, _ => p.CampaignId };
                return p with
                {
                    AccountName = account.Name,
                    Currency = r.Str("account_currency") ?? account.Currency ?? "UNKNOWN",
                    Status = objectId is null ? null : status.GetValueOrDefault(objectId),
                };
            }));
        }
        return rows;
    }

    public async Task<IReadOnlyList<DailyPerformance>> GetDailyAsync(string? accountId, DateRange range, CancellationToken ct)
    {
        var rows = new List<DailyPerformance>();
        foreach (var account in await ResolveAccountsAsync(accountId, ct).ConfigureAwait(false))
        {
            var insights = await client.GetInsightsAsync(account.AccountId, "account", range, null, "1", 2_000, ct).ConfigureAwait(false);
            rows.AddRange(insights.Select(r => MetaMapping.ToDaily(r, account.AccountId, account.Currency)));
        }
        return rows;
    }
}
