namespace Meeza.Marketing.Shared.Security;

/// <summary>
/// Every setting the server understands. Names double as environment variable names.
/// Secrets are only read from environment variables or the encrypted local secret store,
/// never from appsettings.json, so they cannot be committed by accident.
/// </summary>
public static class SettingKeys
{
    // Meta Ads
    public const string MetaAccessToken = "META_ACCESS_TOKEN";
    public const string MetaAppSecret = "META_APP_SECRET";
    public const string MetaAdAccountIds = "META_AD_ACCOUNT_IDS";
    public const string MetaAdAccountIdLegacy = "META_AD_ACCOUNT_ID";
    public const string MetaApiVersion = "META_API_VERSION";

    // Google Ads
    public const string GoogleAdsServiceAccountJson = "GOOGLE_ADS_SERVICE_ACCOUNT_JSON";
    public const string GoogleAdsDeveloperToken = "GOOGLE_ADS_DEVELOPER_TOKEN";
    public const string GoogleAdsClientId = "GOOGLE_ADS_CLIENT_ID";
    public const string GoogleAdsClientSecret = "GOOGLE_ADS_CLIENT_SECRET";
    public const string GoogleAdsRefreshToken = "GOOGLE_ADS_REFRESH_TOKEN";
    public const string GoogleAdsLoginCustomerId = "GOOGLE_ADS_LOGIN_CUSTOMER_ID";
    public const string GoogleAdsCustomerIds = "GOOGLE_ADS_CUSTOMER_IDS";
    public const string GoogleAdsApiVersion = "GOOGLE_ADS_API_VERSION";

    // Google Play Console
    public const string PlayServiceAccountJson = "PLAY_SERVICE_ACCOUNT_JSON";
    public const string PlayServiceAccountKeyPath = "PLAY_SERVICE_ACCOUNT_KEY_PATH";
    public const string PlayPackageNames = "PLAY_PACKAGE_NAMES";
    public const string PlayReportsBucket = "PLAY_REPORTS_BUCKET";

    // Google Analytics 4 / Firebase
    public const string Ga4ServiceAccountJson = "GA4_SERVICE_ACCOUNT_JSON";
    public const string Ga4PropertyIds = "GA4_PROPERTY_IDS";

    // Adjust (mobile measurement partner)
    public const string AdjustApiToken = "ADJUST_API_TOKEN";
    public const string AdjustAppTokens = "ADJUST_APP_TOKENS";
    public const string AdjustCurrency = "ADJUST_CURRENCY";

    // Claude (AI analyst and market research)
    public const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    public const string AiModel = "MEEZA_AI_MODEL";
    /// <summary>"api", "claude_code" (the Claude app on this computer), or empty for automatic.</summary>
    public const string AiEngine = "MEEZA_AI_ENGINE";

    public static readonly IReadOnlySet<string> Secrets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        MetaAccessToken, MetaAppSecret,
        GoogleAdsServiceAccountJson, GoogleAdsDeveloperToken, GoogleAdsClientSecret, GoogleAdsRefreshToken,
        PlayServiceAccountJson, Ga4ServiceAccountJson, AdjustApiToken, AnthropicApiKey,
    };

    public static readonly IReadOnlyList<string> All =
    [
        MetaAccessToken, MetaAppSecret, MetaAdAccountIds, MetaApiVersion,
        GoogleAdsServiceAccountJson, GoogleAdsDeveloperToken, GoogleAdsClientId, GoogleAdsClientSecret, GoogleAdsRefreshToken,
        GoogleAdsLoginCustomerId, GoogleAdsCustomerIds, GoogleAdsApiVersion,
        PlayServiceAccountJson, PlayServiceAccountKeyPath, PlayPackageNames, PlayReportsBucket,
        Ga4ServiceAccountJson, Ga4PropertyIds,
        AdjustApiToken, AdjustAppTokens, AdjustCurrency,
        AnthropicApiKey, AiModel, AiEngine,
    ];

    public static bool IsSecret(string key) => Secrets.Contains(key);
}
