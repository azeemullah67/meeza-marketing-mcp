using Meeza.Marketing.Shared.Security;

namespace Meeza.Marketing.PlayConsole;

public sealed class PlayConsoleOptions
{
    public const string Platform = "google_play";

    public static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/androidpublisher",
        "https://www.googleapis.com/auth/playdeveloperreporting",
        "https://www.googleapis.com/auth/devstorage.read_only",
    ];

    /// <summary>Service account key JSON (from the secret store or environment).</summary>
    public string? ServiceAccountJson { get; init; }

    /// <summary>Path to the service account key file. Used when <see cref="ServiceAccountJson"/> is not set.</summary>
    public string? ServiceAccountKeyPath { get; init; }

    /// <summary>Optional allow-list of package names. When set, no other app can be queried.</summary>
    public IReadOnlyList<string> PackageNames { get; init; } = [];

    /// <summary>Cloud Storage bucket holding Play Console bulk reports, e.g. pubsite_prod_rev_01234567890987654321.</summary>
    public string? ReportsBucket { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceAccountJson) || !string.IsNullOrWhiteSpace(ServiceAccountKeyPath);

    public const string Hint =
        "Set PLAY_SERVICE_ACCOUNT_KEY_PATH (or store PLAY_SERVICE_ACCOUNT_JSON in the secret store) and PLAY_PACKAGE_NAMES. See docs/play-console-setup.md.";

    public static PlayConsoleOptions FromSettings(ISettingsResolver s)
    {
        var bucket = s.Get(SettingKeys.PlayReportsBucket)?.Trim();
        if (bucket is not null && bucket.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)) bucket = bucket[5..];
        return new PlayConsoleOptions
        {
            ServiceAccountJson = s.Get(SettingKeys.PlayServiceAccountJson),
            ServiceAccountKeyPath = s.Get(SettingKeys.PlayServiceAccountKeyPath),
            PackageNames = SettingsResolver.SplitList(s.Get(SettingKeys.PlayPackageNames)).Select(ValidatePackageName).ToArray(),
            ReportsBucket = bucket?.TrimEnd('/'),
        };
    }

    public string LoadServiceAccountJson()
    {
        if (!string.IsNullOrWhiteSpace(ServiceAccountJson)) return ServiceAccountJson;
        if (string.IsNullOrWhiteSpace(ServiceAccountKeyPath)) throw new InvalidOperationException(Hint);
        var path = Environment.ExpandEnvironmentVariables(ServiceAccountKeyPath);
        if (!File.Exists(path)) throw new FileNotFoundException($"Service account key file not found at '{path}'.");
        return File.ReadAllText(path);
    }

    /// <summary>Validates an Android application ID such as com.example.game.</summary>
    public static string ValidatePackageName(string name)
    {
        var t = name.Trim();
        var parts = t.Split('.');
        var valid = parts.Length >= 2 && parts.All(p => p.Length > 0 && char.IsAsciiLetter(p[0]) && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'));
        return valid ? t : throw new ArgumentException($"'{name}' is not a valid Android package name.");
    }
}
