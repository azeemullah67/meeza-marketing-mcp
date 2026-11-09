using Meeza.Marketing.Shared.Security;

namespace Meeza.Marketing.GoogleAds;

public sealed class GoogleAdsOptions
{
    public const string Platform = "google_ads";
    public const string DefaultApiVersion = "v26";
    public const string Scope = "https://www.googleapis.com/auth/adwords";

    /// <summary>
    /// Service account key (JSON). Since 2026 this is Google's recommended way to call the Google Ads API:
    /// access is controlled by the Cloud project's API access level and by adding the service account as a
    /// user on the Google Ads account. No developer token is needed.
    /// </summary>
    public string? ServiceAccountJson { get; init; }

    /// <summary>Legacy developer token. Sent only when set (older setups still use one).</summary>
    public string? DeveloperToken { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? RefreshToken { get; init; }

    /// <summary>Manager (MCC) account used to access client accounts. Digits only.</summary>
    public string? LoginCustomerId { get; init; }

    /// <summary>Optional allow-list of customer IDs (digits only). When set, no other account can be queried.</summary>
    public IReadOnlyList<string> CustomerIds { get; init; } = [];

    public string ApiVersion { get; init; } = DefaultApiVersion;

    public bool UsesServiceAccount => !string.IsNullOrWhiteSpace(ServiceAccountJson);

    public bool UsesUserOAuth =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RefreshToken);

    public bool IsConfigured => UsesServiceAccount || UsesUserOAuth;

    public const string Hint =
        "Store a service account key as GOOGLE_ADS_SERVICE_ACCOUNT_JSON and add that service account as a Read only user in your Google Ads account " +
        "(or use GOOGLE_ADS_CLIENT_ID/SECRET and `auth google-ads`). The Cloud project needs Explorer access or higher. See docs/google-ads-setup.md.";

    public static GoogleAdsOptions FromSettings(ISettingsResolver s) => new()
    {
        ServiceAccountJson = s.Get(SettingKeys.GoogleAdsServiceAccountJson),
        DeveloperToken = s.Get(SettingKeys.GoogleAdsDeveloperToken),
        ClientId = s.Get(SettingKeys.GoogleAdsClientId),
        ClientSecret = s.Get(SettingKeys.GoogleAdsClientSecret),
        RefreshToken = s.Get(SettingKeys.GoogleAdsRefreshToken),
        LoginCustomerId = s.Get(SettingKeys.GoogleAdsLoginCustomerId) is { } l ? NormalizeCustomerId(l) : null,
        CustomerIds = SettingsResolver.SplitList(s.Get(SettingKeys.GoogleAdsCustomerIds)).Select(NormalizeCustomerId).ToArray(),
        ApiVersion = s.Get(SettingKeys.GoogleAdsApiVersion) ?? DefaultApiVersion,
    };

    /// <summary>Normalizes "123-456-7890" or "customers/1234567890" to "1234567890".</summary>
    public static string NormalizeCustomerId(string id)
    {
        var t = id.Trim();
        if (t.StartsWith("customers/", StringComparison.OrdinalIgnoreCase)) t = t["customers/".Length..];
        t = t.Replace("-", "");
        if (t.Length is < 5 or > 12 || !t.All(char.IsAsciiDigit))
            throw new ArgumentException($"'{id}' is not a valid Google Ads customer ID (expected 10 digits, e.g. 123-456-7890).");
        return t;
    }
}
