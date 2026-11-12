using System.Text.Json;
using Meeza.Marketing.Adjust;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.Meta;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Dashboard;

namespace Meeza.MarketingMcp.Services;

/// <summary>
/// Backs the dashboard's Settings page: describes each connection's fields, validates and stores
/// values in the encrypted local secret store, applies them live (no restart), and tests the connection.
/// Secret values are never returned, logged or echoed; only whether they are set and where from.
/// </summary>
public sealed class ConnectionSettings(
    ISettingsResolver settings,
    LocalSecretStore store,
    MetaAdsClient metaClient, MetaAdsPlatform meta,
    GoogleAdsClient googleClient, GoogleAdsPlatform google,
    PlayConsoleClient playClient, PlayConsoleService play,
    AnalyticsClient analyticsClient, AnalyticsService analytics,
    AdjustClient adjustClient, AdjustService adjust,
    DashboardData dashboard, Ai.ClaudeGateway claude, DemoMode demo)
{
    public const int MaxValueLength = 20_000;

    public sealed record Field(string Key, string Label, bool Secret, bool Required, string Help, bool Multiline = false);

    public static readonly IReadOnlyDictionary<string, Field[]> Fields = new Dictionary<string, Field[]>
    {
        [MetaAdsOptions.Platform] =
        [
            new(SettingKeys.MetaAccessToken, "Access token", true, true, "System User token with only the ads_read permission."),
            new(SettingKeys.MetaAdAccountIds, "Ad account IDs", false, false, "Optional. Comma-separated, e.g. act_1234567890. Leave empty to use every account the token can see."),
            new(SettingKeys.MetaAppSecret, "App secret", true, false, "Optional. Adds appsecret_proof to every call for extra protection."),
        ],
        [GoogleAdsOptions.Platform] =
        [
            new(SettingKeys.GoogleAdsServiceAccountJson, "Service account key (JSON)", true, true, "Recommended. Choose the downloaded .json key of a service account you added as a Read only user in Google Ads.", Multiline: true),
            new(SettingKeys.GoogleAdsClientId, "OAuth client ID", false, false, "Alternative to the service account: Desktop app OAuth client. Save it, then run `meeza-marketing-mcp auth google-ads` in a terminal to sign in."),
            new(SettingKeys.GoogleAdsClientSecret, "OAuth client secret", true, false, "From the same Desktop OAuth client."),
            new(SettingKeys.GoogleAdsRefreshToken, "Refresh token", true, false, "Filled in by `meeza-marketing-mcp auth google-ads`."),
            new(SettingKeys.GoogleAdsDeveloperToken, "Developer token", true, false, "Only for older setups. Google no longer requires one for new Cloud projects."),
            new(SettingKeys.GoogleAdsLoginCustomerId, "Manager (MCC) ID", false, false, "Optional. Only if you reach the ad account through a manager account, e.g. 123-456-7890."),
            new(SettingKeys.GoogleAdsCustomerIds, "Customer IDs", false, false, "Optional allow-list, comma-separated."),
        ],
        [PlayConsoleOptions.Platform] =
        [
            new(SettingKeys.PlayServiceAccountJson, "Service account key (JSON)", true, true, "Paste the JSON key, or choose the downloaded .json file.", Multiline: true),
            new(SettingKeys.PlayPackageNames, "Package names", false, false, "Optional allow-list, e.g. com.yourcompany.game."),
            new(SettingKeys.PlayReportsBucket, "Reports bucket", false, false, "Optional, for store installs: pubsite_prod_rev_… from Download reports → Copy Cloud Storage URI."),
        ],
        [AnalyticsOptions.Platform] =
        [
            new(SettingKeys.Ga4ServiceAccountJson, "Service account key (JSON)", true, false, "Optional. Leave empty to reuse the Google Play service account. Otherwise choose a .json key.", Multiline: true),
            new(SettingKeys.Ga4PropertyIds, "Property IDs", false, false, "Optional allow-list, e.g. 123456789 (GA4 → Admin → Property details). Leave empty to use every property the account can see."),
        ],
        [AdjustOptions.Platform] =
        [
            new(SettingKeys.AdjustApiToken, "API token", true, true, "In Adjust: settings (bottom left) → Account settings → My profile → API token. Read-only reporting access; resetting it in Adjust revokes it here."),
            new(SettingKeys.AdjustAppTokens, "App tokens", false, false, "Optional allow-list, comma-separated, e.g. abc123def456. Leave empty to use every app the token can see."),
            new(SettingKeys.AdjustCurrency, "Report currency", false, false, $"Optional. Adjust converts cost and revenue into this currency. Default {AdjustOptions.DefaultCurrency}."),
        ],
        [ClaudePlatform] =
        [
            new(SettingKeys.AiEngine, "Engine", false, false, "Optional. claude_code = the Claude Code app on this computer, signed in with your Claude account (no API key needed). api = the Anthropic API key below. Leave empty to use the API key when one is saved, otherwise the Claude app."),
            new(SettingKeys.AnthropicApiKey, "Anthropic API key", true, false, "From platform.claude.com → API keys. Optional when the Claude app is installed; market research always needs it. Your data is sent to Claude only when you ask a question."),
            new(SettingKeys.AiModel, "Model", false, false, $"Optional. Default {Ai.ClaudeGateway.DefaultModel} on the API; the Claude app uses its own default unless you set one."),
        ],
    };

    public const string ClaudePlatform = "claude";

    public bool CanSave => LocalSecretStore.IsSupported;

    public object Describe() => new
    {
        canSave = CanSave,
        storePath = CanSave ? store.FilePath : null,
        demo = dashboard.Demo,
        platforms = Fields.Select(p => new
        {
            platform = p.Key,
            configured = IsConfigured(p.Key),
            serviceAccountEmail = p.Key == PlayConsoleOptions.Platform ? SafeServiceAccountEmail()
                : p.Key == AnalyticsOptions.Platform && analyticsClient.Options.IsConfigured ? analytics.SafeEmail() : null,
            fields = p.Value.Select(f =>
            {
                var source = settings.SourceOf(f.Key);
                return new
                {
                    key = f.Key, label = f.Label, secret = f.Secret, required = f.Required, help = f.Help, multiline = f.Multiline,
                    isSet = source is not null,
                    source,
                    // Non-secret values are shown so they can be edited; secrets never leave the server.
                    value = f.Secret ? null : settings.Get(f.Key),
                    lockedByEnvironment = source == "environment",
                };
            }),
        }),
    };

    public bool IsConfigured(string platform) => platform switch
    {
        MetaAdsOptions.Platform => metaClient.Options.IsConfigured,
        GoogleAdsOptions.Platform => googleClient.Options.IsConfigured,
        PlayConsoleOptions.Platform => playClient.Options.IsConfigured,
        AnalyticsOptions.Platform => analyticsClient.Options.IsConfigured,
        AdjustOptions.Platform => adjustClient.Options.IsConfigured,
        ClaudePlatform => claude.IsConfigured,
        _ => false,
    };

    /// <summary>Validates and stores the given values. Empty values are ignored (keep current); keys in <paramref name="remove"/> are deleted.</summary>
    public void Save(string platform, IReadOnlyDictionary<string, string?> values, IReadOnlyCollection<string> remove)
    {
        demo.RequireLive();
        if (!CanSave) throw new InvalidOperationException("Saving settings needs the encrypted store, which is only available on Windows. Use environment variables on this OS.");
        if (!Fields.TryGetValue(platform, out var fields)) throw new ArgumentException($"Unknown connection '{platform}'.");
        var allowed = fields.ToDictionary(f => f.Key, StringComparer.OrdinalIgnoreCase);

        var toSet = new Dictionary<string, string>();
        foreach (var (key, raw) in values)
        {
            if (!allowed.TryGetValue(key, out var field)) throw new ArgumentException($"'{key}' is not a {platform} setting.");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (raw.Length > MaxValueLength) throw new ArgumentException($"{field.Label} is too long.");
            if (settings.SourceOf(field.Key) == "environment") throw new ArgumentException($"{field.Label} is set by an environment variable, which takes priority. Change it there.");
            toSet[field.Key] = Normalize(field.Key, raw.Trim());
        }
        foreach (var key in remove)
        {
            if (!allowed.ContainsKey(key)) throw new ArgumentException($"'{key}' is not a {platform} setting.");
        }

        foreach (var (k, v) in toSet) store.Set(k, v);
        foreach (var k in remove) store.Remove(allowed[k].Key);
        Apply();
    }

    /// <summary>Re-reads all settings and swaps them into the running clients.</summary>
    public void Apply()
    {
        metaClient.UpdateOptions(MetaAdsOptions.FromSettings(settings));
        googleClient.UpdateOptions(GoogleAdsOptions.FromSettings(settings));
        playClient.UpdateOptions(PlayConsoleOptions.FromSettings(settings));
        analyticsClient.UpdateOptions(AnalyticsOptions.FromSettings(settings));
        adjustClient.UpdateOptions(AdjustOptions.FromSettings(settings));
        analytics.ResetCache();
        adjust.ResetCache();
        meta.ResetCache();
        google.ResetCache();
        play.ResetCache();
        dashboard.ClearCache();
    }

    public async Task<object> TestAsync(string platform, CancellationToken ct)
    {
        try
        {
            switch (platform)
            {
                case MetaAdsOptions.Platform:
                    var ma = await meta.GetAccountsAsync(ct).ConfigureAwait(false);
                    return new { ok = true, message = $"Connected. {ma.Count} ad account{(ma.Count == 1 ? "" : "s")} found.", items = ma.Select(a => $"{a.Name ?? a.AccountId} ({a.Currency})").Take(10) };
                case GoogleAdsOptions.Platform:
                    var ga = await google.GetAccountsAsync(ct).ConfigureAwait(false);
                    return new { ok = true, message = $"Connected. {ga.Count} Google Ads account{(ga.Count == 1 ? "" : "s")} found.", items = ga.Select(a => $"{a.Name ?? a.AccountId} ({a.Currency})").Take(10) };
                case PlayConsoleOptions.Platform:
                    var apps = await play.GetAppsAsync(ct).ConfigureAwait(false);
                    return new { ok = true, message = $"Connected as {SafeServiceAccountEmail()}. {apps.Count} app{(apps.Count == 1 ? "" : "s")} found.", items = apps.Select(a => a.DisplayName is null ? a.PackageName : $"{a.DisplayName} ({a.PackageName})").Take(10) };
                case AnalyticsOptions.Platform:
                    analytics.ResetCache();
                    var props = await analytics.GetPropertiesAsync(ct).ConfigureAwait(false);
                    if (props.Count == 0)
                        return new { ok = false, message = $"Signed in as {analytics.SafeEmail()}, but no GA4 property is shared with it. Add that email as a Viewer in GA4 → Admin → Property access management.", items = Array.Empty<string>() };
                    return new { ok = true, message = $"Connected as {analytics.SafeEmail()}. {props.Count} propert{(props.Count == 1 ? "y" : "ies")} found.", items = props.Select(x => $"{x.DisplayName ?? "(unknown)"} ({x.PropertyId})").Take(10) };
                case AdjustOptions.Platform:
                    adjust.ResetCache();
                    var adjustApps = await adjust.GetAppsAsync(ct).ConfigureAwait(false);
                    if (adjustApps.Count == 0)
                        return new { ok = false, message = "The token works, but no Adjust app is visible to it. Check this Adjust user's app permissions.", items = Array.Empty<string>() };
                    return new { ok = true, message = $"Connected. {adjustApps.Count} app{(adjustApps.Count == 1 ? "" : "s")} found. Amounts are reported in {adjust.Currency}.", items = adjustApps.Select(a => $"{a.Name ?? "(unnamed)"} ({a.AppToken})").Take(10) };
                case ClaudePlatform when claude.UsesLocalApp:
                    var reply = await claude.RunLocalAsync("Reply with the single word: ready", "Are you ready?", ct).ConfigureAwait(false);
                    return new { ok = true, message = $"Connected through the Claude app on this computer ({claude.LocalAppPath}){(reply.Model is null ? "" : $", model {reply.Model}")}. No API key is used; market research still needs one.", items = Array.Empty<string>() };
                case ClaudePlatform:
                    if (!claude.HasApiKey)
                        return new { ok = false, message = claude.EngineSetting == Ai.ClaudeGateway.EngineLocal ? "The Claude Code app was not found on this computer. Install it from claude.com/code and sign in, then test again." : Ai.ClaudeGateway.Hint, items = Array.Empty<string>() };
                    using (var client = claude.Client())
                    {
                        var model = await client.Models.Retrieve(claude.Model, cancellationToken: ct).ConfigureAwait(false);
                        return new { ok = true, message = $"Connected. Model {model.ID} is available.", items = Array.Empty<string>() };
                    }
                default:
                    throw new ArgumentException($"Unknown connection '{platform}'.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { ok = false, message = ToolRunner.Describe(ex), items = Array.Empty<string>() };
        }
    }

    /// <summary>Runs the Google OAuth loopback sign-in and stores the refresh token.</summary>
    public async Task<object> SignInGoogleAdsAsync(CancellationToken ct)
    {
        demo.RequireLive();
        var clientId = settings.Get(SettingKeys.GoogleAdsClientId);
        var clientSecret = settings.Get(SettingKeys.GoogleAdsClientSecret);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Save the OAuth client ID and client secret first.");
        if (!CanSave) throw new InvalidOperationException("Saving the refresh token needs the encrypted store (Windows).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var refresh = await GoogleOAuthFlow.GetRefreshTokenAsync(clientId, clientSecret, GoogleAdsOptions.Scope, TextWriter.Null, timeout.Token).ConfigureAwait(false);
        store.Set(SettingKeys.GoogleAdsRefreshToken, refresh);
        Apply();
        return new { ok = true, message = "Signed in. Refresh token saved (encrypted)." };
    }

    private string? SafeServiceAccountEmail()
    {
        try
        {
            if (!playClient.Options.IsConfigured) return null;
            using var doc = JsonDocument.Parse(playClient.Options.LoadServiceAccountJson());
            return doc.RootElement.TryGetProperty("client_email", out var e) ? e.GetString() : null;
        }
        catch { return null; }
    }

    public static string Normalize(string key, string value)
    {
        switch (key)
        {
            case SettingKeys.MetaAdAccountIds:
                return string.Join(",", SettingsResolver.SplitList(value).Select(MetaAdsOptions.NormalizeAccountId));
            case SettingKeys.GoogleAdsCustomerIds:
                return string.Join(",", SettingsResolver.SplitList(value).Select(GoogleAdsOptions.NormalizeCustomerId));
            case SettingKeys.GoogleAdsLoginCustomerId:
                return GoogleAdsOptions.NormalizeCustomerId(value);
            case SettingKeys.GoogleAdsClientId:
                if (!value.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The OAuth client ID should end with .apps.googleusercontent.com.");
                return value;
            case SettingKeys.PlayPackageNames:
                return string.Join(",", SettingsResolver.SplitList(value).Select(PlayConsoleOptions.ValidatePackageName));
            case SettingKeys.PlayReportsBucket:
                var bucket = value.StartsWith("gs://", StringComparison.OrdinalIgnoreCase) ? value[5..] : value;
                bucket = bucket.Split('/', 2)[0];
                if (!bucket.StartsWith("pubsite_prod", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The reports bucket normally starts with pubsite_prod_ (copy it from Play Console → Download reports).");
                return bucket;
            case SettingKeys.Ga4PropertyIds:
                return string.Join(",", SettingsResolver.SplitList(value).Select(AnalyticsOptions.NormalizePropertyId));
            case SettingKeys.Ga4ServiceAccountJson:
            case SettingKeys.PlayServiceAccountJson:
            case SettingKeys.GoogleAdsServiceAccountJson:
                try
                {
                    using var doc = JsonDocument.Parse(value);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("client_email", out _) || !root.TryGetProperty("private_key", out _))
                        throw new ArgumentException("That doesn't look like a service account key: it needs client_email and private_key.");
                    if (root.TryGetProperty("type", out var t) && t.GetString() != "service_account")
                        throw new ArgumentException("The key's type must be service_account.");
                }
                catch (JsonException)
                {
                    throw new ArgumentException("The service account key isn't valid JSON. Paste the whole downloaded .json file.");
                }
                return value;
            case SettingKeys.AdjustAppTokens:
                return string.Join(",", SettingsResolver.SplitList(value).Select(AdjustOptions.NormalizeAppToken));
            case SettingKeys.AdjustCurrency:
                return AdjustOptions.NormalizeCurrency(value);
            case SettingKeys.AiEngine:
                var engine = value.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
                if (engine is "local" or "claude" or "claude_app") engine = Ai.ClaudeGateway.EngineLocal;
                if (engine is not (Ai.ClaudeGateway.EngineApi or Ai.ClaudeGateway.EngineLocal))
                    throw new ArgumentException("Engine must be claude_code (the Claude app on this computer) or api (Anthropic API key). Leave it empty for automatic.");
                return engine;
            case SettingKeys.AnthropicApiKey:
                if (!value.StartsWith("sk-ant-", StringComparison.Ordinal)) throw new ArgumentException("Anthropic API keys start with sk-ant-.");
                if (value.Any(char.IsWhiteSpace)) throw new ArgumentException("Keys can't contain spaces.");
                return value;
            case SettingKeys.MetaAccessToken:
            case SettingKeys.GoogleAdsDeveloperToken:
            case SettingKeys.AdjustApiToken:
                if (value.Any(char.IsWhiteSpace)) throw new ArgumentException("Tokens can't contain spaces. Check that you copied the whole token.");
                return value;
            default:
                return value;
        }
    }
}
