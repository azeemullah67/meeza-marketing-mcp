using System.Net;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>An error returned by a platform API. The message is always redacted.</summary>
public sealed class PlatformApiException(string platform, string message, HttpStatusCode? statusCode = null, string? errorCode = null)
    : Exception(Redactor.Redact($"[{platform}] {message}"))
{
    public string Platform { get; } = platform;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>Raised when a platform is called without the credentials it needs.</summary>
public sealed class PlatformNotConfiguredException(string platform, string hint)
    : Exception($"{platform} is not configured. {hint}")
{
    public string Platform { get; } = platform;
}

/// <summary>Raised when a request targets an account the user has not allowed or cannot access.</summary>
public sealed class AccountAccessDeniedException(string platform, string accountId, string reason)
    : Exception($"[{platform}] Access to account '{accountId}' is not allowed: {reason}");
