using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Shared.Security;

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken ct);
}

/// <summary>Caches a Google OAuth access token and refreshes it shortly before expiry.</summary>
public abstract class CachedGoogleTokenProvider(HttpClient http, string platform) : IAccessTokenProvider
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    protected HttpClient Http { get; } = http;

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt) return _token;
            using var response = await Http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(BuildRequest()), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var detail = TryReadError(body);
                throw new PlatformApiException(platform, $"OAuth token request failed ({(int)response.StatusCode}): {detail}", response.StatusCode);
            }
            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.Str("access_token") ?? throw new PlatformApiException(platform, "OAuth response did not contain an access token.");
            var expiresIn = doc.RootElement.Long("expires_in") ?? 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120));
            return _token;
        }
        finally
        {
            _lock.Release();
        }
    }

    protected abstract IEnumerable<KeyValuePair<string, string>> BuildRequest();

    private static string TryReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.Str("error");
            var desc = doc.RootElement.Str("error_description");
            var hint = err == "invalid_grant" ? " The refresh token may be revoked or expired; re-run the auth command." : "";
            return Redactor.Redact($"{err} {desc}".Trim() + hint);
        }
        catch (JsonException)
        {
            return "unreadable error response";
        }
    }
}

/// <summary>OAuth 2.0 installed-app flow credentials (client ID/secret + refresh token), used for Google Ads.</summary>
public sealed class GoogleRefreshTokenProvider(HttpClient http, string clientId, string clientSecret, string refreshToken, string platform)
    : CachedGoogleTokenProvider(http, platform)
{
    protected override IEnumerable<KeyValuePair<string, string>> BuildRequest() =>
    [
        new("grant_type", "refresh_token"),
        new("client_id", clientId),
        new("client_secret", clientSecret),
        new("refresh_token", refreshToken),
    ];
}

/// <summary>
/// Service account (JWT bearer) credentials, used for Google Play. Signs the assertion locally
/// with the service account's RSA key; the private key never leaves the machine.
/// </summary>
public sealed class GoogleServiceAccountTokenProvider : CachedGoogleTokenProvider
{
    private readonly string _clientEmail;
    private readonly string _privateKeyPem;
    private readonly string _scopes;

    public GoogleServiceAccountTokenProvider(HttpClient http, string serviceAccountJson, IEnumerable<string> scopes, string platform)
        : base(http, platform)
    {
        using var doc = JsonDocument.Parse(serviceAccountJson);
        var root = doc.RootElement;
        if (root.Str("type") != "service_account")
            throw new ArgumentException("The Play credentials file is not a Google service account key (expected \"type\": \"service_account\").");
        _clientEmail = root.Str("client_email") ?? throw new ArgumentException("Service account key is missing client_email.");
        _privateKeyPem = root.Str("private_key") ?? throw new ArgumentException("Service account key is missing private_key.");
        _scopes = string.Join(' ', scopes);
    }

    public string ClientEmail => _clientEmail;

    protected override IEnumerable<KeyValuePair<string, string>> BuildRequest() =>
    [
        new("grant_type", "urn:ietf:params:oauth:grant-type:jwt-bearer"),
        new("assertion", CreateAssertion(DateTimeOffset.UtcNow)),
    ];

    internal string CreateAssertion(DateTimeOffset now)
    {
        var header = new { alg = "RS256", typ = "JWT" };
        var claims = new
        {
            iss = _clientEmail,
            scope = _scopes,
            aud = TokenEndpoint,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(60).ToUnixTimeSeconds(),
        };
        var unsigned = $"{B64(JsonSerializer.SerializeToUtf8Bytes(header))}.{B64(JsonSerializer.SerializeToUtf8Bytes(claims))}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);
        var sig = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{B64(sig)}";
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
