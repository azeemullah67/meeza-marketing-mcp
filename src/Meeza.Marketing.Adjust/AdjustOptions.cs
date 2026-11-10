using Meeza.Marketing.Shared.Security;

namespace Meeza.Marketing.Adjust;

/// <summary>Adjust (mobile measurement partner) settings. The Report Service API is read-only.</summary>
public sealed class AdjustOptions
{
    public const string Platform = "adjust";
    public const string DefaultCurrency = "USD";

    /// <summary>Personal API token from Adjust → Account settings → My profile.</summary>
    public string? ApiToken { get; init; }

    /// <summary>Optional allow-list of Adjust app tokens. Empty means every app the token can see.</summary>
    public IReadOnlyList<string> AppTokens { get; init; } = [];

    /// <summary>
    /// Currency Adjust reports money in. Adjust converts amounts itself (it is the only place amounts are converted),
    /// so every Adjust amount is labelled with this currency.
    /// </summary>
    public string Currency { get; init; } = DefaultCurrency;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiToken);

    public const string Hint =
        "Store your Adjust API token as ADJUST_API_TOKEN (Adjust → Account settings → My profile → API token). " +
        "Optionally set ADJUST_APP_TOKENS and ADJUST_CURRENCY. See docs/adjust-setup.md.";

    public static AdjustOptions FromSettings(ISettingsResolver s) => new()
    {
        ApiToken = s.Get(SettingKeys.AdjustApiToken),
        AppTokens = SettingsResolver.SplitList(s.Get(SettingKeys.AdjustAppTokens)).Select(NormalizeAppToken).ToArray(),
        Currency = string.IsNullOrWhiteSpace(s.Get(SettingKeys.AdjustCurrency)) ? DefaultCurrency : NormalizeCurrency(s.Get(SettingKeys.AdjustCurrency)!),
    };

    /// <summary>Adjust app tokens are short lowercase alphanumeric strings (12 characters today).</summary>
    public static string NormalizeAppToken(string token)
    {
        var t = token.Trim();
        if (t.Length is < 6 or > 32 || !t.All(char.IsAsciiLetterOrDigit))
            throw new ArgumentException($"'{token}' is not a valid Adjust app token (letters and digits, e.g. abc123def456; find it in Adjust → AppView → app settings).");
        return t;
    }

    public static string NormalizeCurrency(string currency)
    {
        var c = currency.Trim().ToUpperInvariant();
        if (c.Length != 3 || !c.All(char.IsAsciiLetterUpper))
            throw new ArgumentException($"'{currency}' is not a 3-letter currency code such as USD or EUR.");
        return c;
    }
}
