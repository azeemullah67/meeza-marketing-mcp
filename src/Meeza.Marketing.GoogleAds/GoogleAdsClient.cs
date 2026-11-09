using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.GoogleAds;

/// <summary>
/// Read-only Google Ads API client (REST transport). Only GoogleAdsService.SearchStream and
/// CustomerService.ListAccessibleCustomers are called; no mutate endpoints exist in this client.
/// </summary>
public sealed class GoogleAdsClient
{
    public const string HttpClientName = "google-ads";
    private const string Host = "https://googleads.googleapis.com";

    private readonly HttpClient _http;
    private volatile GoogleAdsOptions _options;
    private volatile IAccessTokenProvider? _tokens;

    public GoogleAdsClient(HttpClient http, GoogleAdsOptions options)
    {
        _http = http;
        _options = options;
        _tokens = CreateTokens(http, options);
    }

    /// <summary>Service account first (Google's current model), user OAuth refresh token as the alternative.</summary>
    private static IAccessTokenProvider? CreateTokens(HttpClient http, GoogleAdsOptions options) =>
        options.UsesServiceAccount
            ? new GoogleServiceAccountTokenProvider(http, options.ServiceAccountJson!, [GoogleAdsOptions.Scope], GoogleAdsOptions.Platform)
            : options.UsesUserOAuth
                ? new GoogleRefreshTokenProvider(http, options.ClientId!, options.ClientSecret!, options.RefreshToken!, GoogleAdsOptions.Platform)
                : null;

    public GoogleAdsOptions Options => _options;

    /// <summary>Applies new settings (from the dashboard's Settings page) without restarting.</summary>
    public void UpdateOptions(GoogleAdsOptions options)
    {
        _tokens = CreateTokens(_http, options);
        _options = options;
    }

    /// <summary>Customer IDs the OAuth user can access directly.</summary>
    public async Task<IReadOnlyList<string>> ListAccessibleCustomersAsync(CancellationToken ct)
    {
        using var req = await CreateRequestAsync(HttpMethod.Get, $"{Host}/{_options.ApiVersion}/customers:listAccessibleCustomers", null, ct).ConfigureAwait(false);
        using var doc = await SendAsync(req, ct).ConfigureAwait(false);
        return doc.RootElement.Arr("resourceNames").Select(e => GoogleAdsOptions.NormalizeCustomerId(e.GetString()!)).ToArray();
    }

    /// <summary>Runs a GAQL query and returns every result row.</summary>
    public async Task<IReadOnlyList<JsonElement>> SearchAsync(string customerId, string gaql, CancellationToken ct, string? loginCustomerIdOverride = null)
    {
        var url = $"{Host}/{_options.ApiVersion}/customers/{GoogleAdsOptions.NormalizeCustomerId(customerId)}/googleAds:searchStream";
        using var req = await CreateRequestAsync(HttpMethod.Post, url, JsonContent.Create(new { query = gaql }), ct, loginCustomerIdOverride).ConfigureAwait(false);
        using var doc = await SendAsync(req, ct).ConfigureAwait(false);

        var rows = new List<JsonElement>();
        // searchStream returns a JSON array of batches, each with a "results" array.
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var batch in doc.RootElement.EnumerateArray())
                rows.AddRange(batch.Arr("results").Select(r => r.Clone()));
        else
            rows.AddRange(doc.RootElement.Arr("results").Select(r => r.Clone()));
        return rows;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct, string? loginOverride = null)
    {
        if (_tokens is null) throw new PlatformNotConfiguredException(GoogleAdsOptions.Platform, GoogleAdsOptions.Hint);

        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct).ConfigureAwait(false));
        if (!string.IsNullOrWhiteSpace(_options.DeveloperToken)) req.Headers.TryAddWithoutValidation("developer-token", _options.DeveloperToken);
        var login = loginOverride ?? _options.LoginCustomerId;
        if (!string.IsNullOrWhiteSpace(login)) req.Headers.TryAddWithoutValidation("login-customer-id", login);
        return req;
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(body);
        throw new PlatformApiException(GoogleAdsOptions.Platform, DescribeError((int)resp.StatusCode, body), resp.StatusCode);
    }

    /// <summary>Extracts the most useful message from a Google Ads error payload (object or streamed array).</summary>
    public static string DescribeError(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : doc.RootElement;
            var error = root.Prop("error");
            if (error is null) return $"HTTP {status}.";
            var message = error.Value.Str("message") ?? "Unknown error";
            var statusText = error.Value.Str("status");
            var details = new List<string>();
            string? requestId = null;
            foreach (var d in error.Value.Arr("details"))
            {
                requestId ??= d.Str("requestId");
                foreach (var e in d.Arr("errors"))
                {
                    var code = e.Prop("errorCode")?.EnumerateObject().Select(p => $"{p.Name}={p.Value}").FirstOrDefault();
                    details.Add($"{code}: {e.Str("message")}");
                }
            }
            var hint = statusText switch
            {
                "UNAUTHENTICATED" => " Check the service account key (or re-run Sign in with Google).",
                "PERMISSION_DENIED" when body.Contains("ACCESS_LEVEL", StringComparison.OrdinalIgnoreCase) || body.Contains("TEST_ACCOUNT", StringComparison.OrdinalIgnoreCase) =>
                    " Your Cloud project's Google Ads API access level only allows test accounts. Apply for Explorer access in Google Cloud console → Google Ads API → Access levels.",
                "PERMISSION_DENIED" when body.Contains("DEVELOPER_TOKEN", StringComparison.OrdinalIgnoreCase) =>
                    " The developer token is not approved for this account or access level. Check API Center in your manager account.",
                "PERMISSION_DENIED" => " The service account (or signed-in user) can't access this customer. Add the service account as a Read only user in Google Ads → Admin → Access and security; if access goes through a manager account, set the manager ID.",
                "NOT_FOUND" => " Check GOOGLE_ADS_API_VERSION; the configured version may be sunset.",
                _ => "",
            };
            var detailText = details.Count > 0 ? " Details: " + string.Join("; ", details.Take(5)) : "";
            var rid = requestId is null ? "" : $" (request-id {requestId})";
            return $"{statusText ?? status.ToString()}: {message}.{detailText}{rid}{hint}";
        }
        catch (JsonException)
        {
            return $"HTTP {status} with an unreadable error body.";
        }
    }
}
