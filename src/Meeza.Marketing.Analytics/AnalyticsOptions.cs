using Meeza.Marketing.Shared.Security;

namespace Meeza.Marketing.Analytics;

/// <summary>Google Analytics 4 (the analytics behind Firebase) settings. Read-only scope only.</summary>
public sealed class AnalyticsOptions
{
    public const string Platform = "ga4";

    public static readonly string[] Scopes = ["https://www.googleapis.com/auth/analytics.readonly"];

    /// <summary>Service account key JSON dedicated to GA4.</summary>
    public string? ServiceAccountJson { get; init; }

    /// <summary>
    /// Fallback: the Google Play service account key (JSON or file path). Lets one service account serve
    /// Play and GA4, so the user only has to add its email in GA4 → Property access management.
    /// </summary>
    public string? FallbackServiceAccountJson { get; init; }
    public string? FallbackServiceAccountKeyPath { get; init; }

    /// <summary>Optional allow-list of GA4 property IDs (digits). Empty means every property the account can see.</summary>
    public IReadOnlyList<string> PropertyIds { get; init; } = [];

    public bool UsesPlayKey => string.IsNullOrWhiteSpace(ServiceAccountJson);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServiceAccountJson) ||
        !string.IsNullOrWhiteSpace(FallbackServiceAccountJson) ||
        !string.IsNullOrWhiteSpace(FallbackServiceAccountKeyPath);

    public const string Hint =
        "Store a service account key as GA4_SERVICE_ACCOUNT_JSON (or reuse the Google Play key), enable the Google Analytics Data API " +
        "and Admin API in its Cloud project, and add the service account email as a Viewer in GA4 → Admin → Property access management. " +
        "Optionally set GA4_PROPERTY_IDS. See docs/analytics-setup.md.";

    public static AnalyticsOptions FromSettings(ISettingsResolver s) => new()
    {
        ServiceAccountJson = s.Get(SettingKeys.Ga4ServiceAccountJson),
        FallbackServiceAccountJson = s.Get(SettingKeys.PlayServiceAccountJson),
        FallbackServiceAccountKeyPath = s.Get(SettingKeys.PlayServiceAccountKeyPath),
        PropertyIds = SettingsResolver.SplitList(s.Get(SettingKeys.Ga4PropertyIds)).Select(NormalizePropertyId).ToArray(),
    };

    public string LoadServiceAccountJson()
    {
        if (!string.IsNullOrWhiteSpace(ServiceAccountJson)) return ServiceAccountJson;
        if (!string.IsNullOrWhiteSpace(FallbackServiceAccountJson)) return FallbackServiceAccountJson;
        if (string.IsNullOrWhiteSpace(FallbackServiceAccountKeyPath)) throw new InvalidOperationException(Hint);
        var path = Environment.ExpandEnvironmentVariables(FallbackServiceAccountKeyPath);
        if (!File.Exists(path)) throw new FileNotFoundException($"Service account key file not found at '{path}'.");
        return File.ReadAllText(path);
    }

    /// <summary>Normalizes "properties/123456789" or "123456789" to "123456789".</summary>
    public static string NormalizePropertyId(string id)
    {
        var t = id.Trim();
        if (t.StartsWith("properties/", StringComparison.OrdinalIgnoreCase)) t = t["properties/".Length..];
        if (t.Length is < 5 or > 15 || !t.All(char.IsAsciiDigit))
            throw new ArgumentException($"'{id}' is not a valid GA4 property ID (digits only, e.g. 123456789; find it in GA4 → Admin → Property details). It is not the G-XXXX measurement ID.");
        return t;
    }
}
