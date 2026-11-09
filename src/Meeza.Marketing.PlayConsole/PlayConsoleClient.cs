using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.PlayConsole;

/// <summary>
/// Read-only client for the Google Play Developer Reporting API (vitals), the Play Developer API
/// (reviews, read only) and Cloud Storage (Play Console bulk report exports).
/// No edits are created and no replies are posted.
/// </summary>
public sealed class PlayConsoleClient
{
    public const string HttpClientName = "google-play";
    private const string ReportingHost = "https://playdeveloperreporting.googleapis.com/v1beta1";
    private const string PublisherHost = "https://androidpublisher.googleapis.com/androidpublisher/v3";
    private const string StorageHost = "https://storage.googleapis.com/storage/v1";
    public const string VitalsTimeZone = "America/Los_Angeles";

    private readonly HttpClient _http;
    private volatile PlayConsoleOptions _options;
    private volatile Lazy<GoogleServiceAccountTokenProvider> _tokens;

    public PlayConsoleClient(HttpClient http, PlayConsoleOptions options)
    {
        _http = http;
        _options = options;
        _tokens = new Lazy<GoogleServiceAccountTokenProvider>(() =>
            new GoogleServiceAccountTokenProvider(http, options.LoadServiceAccountJson(), PlayConsoleOptions.Scopes, PlayConsoleOptions.Platform));
    }

    public PlayConsoleOptions Options => _options;

    /// <summary>Applies new settings (from the dashboard's Settings page) without restarting.</summary>
    public void UpdateOptions(PlayConsoleOptions options)
    {
        _tokens = new Lazy<GoogleServiceAccountTokenProvider>(() =>
            new GoogleServiceAccountTokenProvider(_http, options.LoadServiceAccountJson(), PlayConsoleOptions.Scopes, PlayConsoleOptions.Platform));
        _options = options;
    }

    public string ServiceAccountEmail => _tokens.Value.ClientEmail;

    // ---------------------------------------------------------------- Reporting API

    /// <summary>Apps the service account can access in the Reporting API.</summary>
    public async Task<IReadOnlyList<JsonElement>> SearchAppsAsync(CancellationToken ct)
    {
        var apps = new List<JsonElement>();
        string? page = null;
        do
        {
            var url = $"{ReportingHost}/apps:search?pageSize=1000" + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            apps.AddRange(doc.RootElement.Arr("apps").Select(a => a.Clone()));
            page = doc.RootElement.Str("nextPageToken");
        } while (!string.IsNullOrEmpty(page));
        return apps;
    }

    /// <summary>Latest available end time (exclusive, as a date) for DAILY data in a metric set.</summary>
    public async Task<DateOnly?> GetDailyFreshnessAsync(string packageName, string metricSet, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{ReportingHost}/apps/{packageName}/{metricSet}", null, ct).ConfigureAwait(false);
        foreach (var f in doc.RootElement.Arr("freshnessInfo.freshnesses"))
        {
            if (f.Str("aggregationPeriod") != "DAILY") continue;
            var y = f.Long("latestEndTime.year"); var m = f.Long("latestEndTime.month"); var d = f.Long("latestEndTime.day");
            if (y is not null && m is not null && d is not null) return new DateOnly((int)y, (int)m, (int)d);
        }
        return null;
    }

    /// <summary>
    /// Queries a vitals metric set (e.g. crashRateMetricSet) with DAILY aggregation.
    /// <paramref name="range"/> is inclusive; the API's end time is exclusive, so one day is added.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> QueryMetricSetAsync(
        string packageName, string metricSet, DateRange range, IReadOnlyList<string> metrics, IReadOnlyList<string> dimensions, CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        string? page = null;
        do
        {
            var body = new Dictionary<string, object?>
            {
                ["timelineSpec"] = new
                {
                    aggregationPeriod = "DAILY",
                    startTime = DateTimeJson(range.Start),
                    endTime = DateTimeJson(range.End.AddDays(1)),
                },
                ["metrics"] = metrics,
                ["dimensions"] = dimensions,
                ["pageSize"] = 10000,
            };
            if (page is not null) body["pageToken"] = page;

            using var doc = await SendAsync(HttpMethod.Post, $"{ReportingHost}/apps/{packageName}/{metricSet}:query", JsonContent.Create(body), ct).ConfigureAwait(false);
            rows.AddRange(doc.RootElement.Arr("rows").Select(r => r.Clone()));
            page = doc.RootElement.Str("nextPageToken");
        } while (!string.IsNullOrEmpty(page) && rows.Count < 100_000);
        return rows;
    }

    private static object DateTimeJson(DateOnly d) => new { year = d.Year, month = d.Month, day = d.Day, timeZone = new { id = VitalsTimeZone } };

    // ---------------------------------------------------------------- Developer API (reviews, read only)

    public async Task<IReadOnlyList<JsonElement>> GetReviewsAsync(string packageName, int max, string? translationLanguage, CancellationToken ct)
    {
        var reviews = new List<JsonElement>();
        string? token = null;
        do
        {
            var url = $"{PublisherHost}/applications/{packageName}/reviews?maxResults=100";
            if (!string.IsNullOrWhiteSpace(translationLanguage)) url += $"&translationLanguage={Uri.EscapeDataString(translationLanguage)}";
            if (token is not null) url += $"&token={Uri.EscapeDataString(token)}";
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            reviews.AddRange(doc.RootElement.Arr("reviews").Select(r => r.Clone()));
            token = doc.RootElement.Str("tokenPagination.nextPageToken");
        } while (token is not null && reviews.Count < max);
        return reviews.Take(max).ToArray();
    }

    // ---------------------------------------------------------------- Cloud Storage (bulk report exports)

    public async Task<IReadOnlyList<string>> ListReportObjectsAsync(string prefix, CancellationToken ct)
    {
        var bucket = RequireBucket();
        var names = new List<string>();
        string? page = null;
        do
        {
            var url = $"{StorageHost}/b/{Uri.EscapeDataString(bucket)}/o?prefix={Uri.EscapeDataString(prefix)}&fields=items(name),nextPageToken";
            if (page is not null) url += $"&pageToken={Uri.EscapeDataString(page)}";
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            names.AddRange(doc.RootElement.Arr("items").Select(i => i.Str("name")!).Where(n => n is not null));
            page = doc.RootElement.Str("nextPageToken");
        } while (page is not null);
        return names;
    }

    /// <summary>Downloads a report CSV. Play exports are usually UTF-16 with a BOM; the encoding is detected.</summary>
    public async Task<string?> DownloadReportAsync(string objectName, CancellationToken ct)
    {
        var bucket = RequireBucket();
        var url = $"{StorageHost}/b/{Uri.EscapeDataString(bucket)}/o/{Uri.EscapeDataString(objectName)}?alt=media";
        using var req = await CreateRequestAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode)
            throw new PlatformApiException(PlayConsoleOptions.Platform, DescribeError((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false), storage: true), resp.StatusCode);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return DecodeReport(bytes);
    }

    public static string DecodeReport(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), LooksUtf16(bytes) ? Encoding.Unicode : Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static bool LooksUtf16(byte[] b) => b.Length >= 4 && b[0] != 0 && b[1] == 0 && b[2] != 0 && b[3] == 0;

    private string RequireBucket() =>
        _options.ReportsBucket ?? throw new PlatformNotConfiguredException(PlayConsoleOptions.Platform,
            "PLAY_REPORTS_BUCKET is not set. Copy the Cloud Storage URI from Play Console → Download reports (e.g. pubsite_prod_rev_0123...).");

    // ---------------------------------------------------------------- transport

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new PlatformNotConfiguredException(PlayConsoleOptions.Platform, PlayConsoleOptions.Hint);
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.Value.GetAccessTokenAsync(ct).ConfigureAwait(false));
        return req;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        using var req = await CreateRequestAsync(method, url, content, ct).ConfigureAwait(false);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        throw new PlatformApiException(PlayConsoleOptions.Platform, DescribeError((int)resp.StatusCode, body, url.StartsWith(StorageHost)), resp.StatusCode);
    }

    public static string DescribeError(int status, string body, bool storage = false)
    {
        string message;
        try
        {
            using var doc = JsonDocument.Parse(body);
            message = doc.RootElement.Str("error.message") ?? $"HTTP {status}";
        }
        catch (JsonException)
        {
            message = $"HTTP {status}";
        }
        var hint = status switch
        {
            401 => " Check the service account key.",
            403 when storage => " Grant the service account 'View app information and download bulk reports' in Play Console → Users and permissions.",
            403 => " Invite the service account in Play Console → Users and permissions and grant it access to this app (View app information / View app quality information), and enable the API in the Google Cloud project.",
            404 => " Check the package name and that the app is published or in a testing track.",
            _ => "",
        };
        return $"{message}.{hint}";
    }
}
