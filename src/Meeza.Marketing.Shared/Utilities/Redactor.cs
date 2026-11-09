using System.Text.RegularExpressions;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>Removes credentials from text before it is logged or returned to the AI client.</summary>
public static partial class Redactor
{
    [GeneratedRegex(@"(?i)\b(access_token|refresh_token|client_secret|appsecret_proof|developer-token|assertion|code)=([^&\s""']+)")]
    private static partial Regex QueryTokenRegex();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|client_secret|private_key|id_token)""\s*:\s*"")([^""]*)("")")]
    private static partial Regex JsonTokenRegex();

    [GeneratedRegex(@"(?i)\b(Bearer|OAuth)\s+[A-Za-z0-9\-\._~\+/]+=*")]
    private static partial Regex AuthHeaderRegex();

    [GeneratedRegex(@"\bEAA[A-Za-z0-9]{20,}\b")]
    private static partial Regex MetaTokenRegex();

    [GeneratedRegex(@"\bya29\.[A-Za-z0-9\-_\.]+")]
    private static partial Regex GoogleAccessTokenRegex();

    [GeneratedRegex(@"\b1//[A-Za-z0-9\-_]{20,}")]
    private static partial Regex GoogleRefreshTokenRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemRegex();

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var s = QueryTokenRegex().Replace(text, "$1=[REDACTED]");
        s = JsonTokenRegex().Replace(s, "$1[REDACTED]$3");
        s = AuthHeaderRegex().Replace(s, "$1 [REDACTED]");
        s = MetaTokenRegex().Replace(s, "[REDACTED]");
        s = GoogleAccessTokenRegex().Replace(s, "[REDACTED]");
        s = GoogleRefreshTokenRegex().Replace(s, "[REDACTED]");
        s = PemRegex().Replace(s, "[REDACTED PRIVATE KEY]");
        return s;
    }

    /// <summary>Shows only enough of a secret to recognize it, e.g. "EAAB…9f2c (212 chars)".</summary>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "(not set)";
        if (secret.Length <= 12) return $"*** ({secret.Length} chars)";
        return $"{secret[..4]}…{secret[^4..]} ({secret.Length} chars)";
    }
}
