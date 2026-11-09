using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.Shared.Interfaces;

/// <summary>
/// Read-only advertising platform adapter used by the unified reporting tools.
/// Implementations must never expose write operations.
/// </summary>
public interface IAdPlatform
{
    /// <summary>Stable platform identifier, e.g. "meta" or "google_ads".</summary>
    string Platform { get; }

    /// <summary>True when the credentials needed to call this platform are present.</summary>
    bool IsConfigured { get; }

    /// <summary>Explains which settings are missing when the platform is not configured.</summary>
    string ConfigurationHint { get; }

    Task<IReadOnlyList<AdAccount>> GetAccountsAsync(CancellationToken ct);

    /// <summary>
    /// Campaign-level performance for every configured account (or only <paramref name="accountId"/>).
    /// When <paramref name="byCountry"/> is true, rows are broken down by country.
    /// </summary>
    Task<IReadOnlyList<CampaignPerformance>> GetCampaignPerformanceAsync(
        string? accountId, DateRange range, bool byCountry, CancellationToken ct);

    /// <summary>Performance at campaign, ad set / ad group or ad level, including delivery status where available.</summary>
    Task<IReadOnlyList<CampaignPerformance>> GetPerformanceAsync(
        string? accountId, DateRange range, ReportLevel level, CancellationToken ct);

    /// <summary>Account-level daily series for trend charts.</summary>
    Task<IReadOnlyList<DailyPerformance>> GetDailyAsync(string? accountId, DateRange range, CancellationToken ct);

    /// <summary>
    /// Per-asset results for ads that mix several creative assets (e.g. Google App campaigns).
    /// Platforms whose ads are single creatives (Meta) report creatives at ad level instead and return nothing here.
    /// </summary>
    Task<IReadOnlyList<AssetPerformance>> GetAssetPerformanceAsync(string? accountId, DateRange range, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AssetPerformance>>([]);
}
