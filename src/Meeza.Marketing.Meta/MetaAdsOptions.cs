using Meeza.Marketing.Shared.Security;

namespace Meeza.Marketing.Meta;

public sealed class MetaAdsOptions
{
    public const string DefaultApiVersion = "v25.0";
    public const string Platform = "meta";

    /// <summary>Access token with the ads_read permission (a System User token is recommended).</summary>
    public string? AccessToken { get; init; }

    /// <summary>Optional app secret. When present every request carries appsecret_proof.</summary>
    public string? AppSecret { get; init; }

    /// <summary>
    /// Optional allow-list of ad account IDs (with or without the "act_" prefix). When set, the server
    /// refuses to query any other account even if the token can see it.
    /// </summary>
    public IReadOnlyList<string> AdAccountIds { get; init; } = [];

    public string ApiVersion { get; init; } = DefaultApiVersion;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessToken);

    public const string Hint =
        "Set META_ACCESS_TOKEN (and optionally META_AD_ACCOUNT_IDS, META_APP_SECRET). See docs/meta-setup.md.";

    public static MetaAdsOptions FromSettings(ISettingsResolver s)
    {
        var ids = SettingsResolver.SplitList(s.Get(SettingKeys.MetaAdAccountIds) ?? s.Get(SettingKeys.MetaAdAccountIdLegacy));
        return new MetaAdsOptions
        {
            AccessToken = s.Get(SettingKeys.MetaAccessToken),
            AppSecret = s.Get(SettingKeys.MetaAppSecret),
            AdAccountIds = ids.Select(NormalizeAccountId).ToArray(),
            ApiVersion = s.Get(SettingKeys.MetaApiVersion) ?? DefaultApiVersion,
        };
    }

    /// <summary>Normalizes "123", "act_123" and " act_123 " to "act_123".</summary>
    public static string NormalizeAccountId(string id)
    {
        var t = id.Trim();
        if (t.StartsWith("act_", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        if (t.Length == 0 || !t.All(char.IsAsciiDigit))
            throw new ArgumentException($"'{id}' is not a valid Meta ad account ID (expected digits, optionally prefixed with act_).");
        return "act_" + t;
    }
}
