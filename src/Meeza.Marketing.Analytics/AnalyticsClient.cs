using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Analytics;

/// <summary>A GA4 property. <paramref name="Apps"/> lists the app / web streams under it ("Gapsy (com.meeza.colorslide)"), which is what people recognise; the property display name is often a Firebase project id.</summary>
public sealed record Ga4Property(string PropertyId, string? DisplayName, string? AccountName, IReadOnlyList<string>? Apps = null);

/// <summary>One GA4 report: dimension values as strings, metric values as numbers (null when missing).</summary>
public sealed record Ga4Report(
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<Ga4Row> Rows,
    string? CurrencyCode,
    string? TimeZone,
    bool DataLossFromOtherRow,
    bool SubjectToThresholding)
{
    public decimal? Metric(Ga4Row row, string name)
    {
        var i = Metrics.ToList().IndexOf(name);
        return i < 0 || i >= row.Metrics.Count ? null : row.Metrics[i];
    }

    public string? Dimension(Ga4Row row, string name)
    {
        var i = Dimensions.ToList().IndexOf(name);
        return i < 0 || i >= row.Dimensions.Count ? null : row.Dimensions[i];
    }
}

public sealed record Ga4Row(IReadOnlyList<string> Dimensions, IReadOnlyList<decimal?> Metrics);

/// <summary>
/// Read-only client for the Google Analytics Data API (reports) and Admin API (listing properties).
/// Uses the analytics.readonly scope, so it cannot change any GA4 configuration.
/// </summary>
public sealed class AnalyticsClient
{
    private const string DataHost = "https://analyticsdata.googleapis.com/v1beta";
    private const string AdminHost = "https://analyticsadmin.googleapis.com/v1beta";

    private static readonly JsonSerializerOptions RequestJson = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _http;
    private volatile AnalyticsOptions _options;
    private volatile Lazy<GoogleServiceAccountTokenProvider> _tokens;

    public AnalyticsClient(HttpClient http, AnalyticsOptions options)
    {
        _http = http;
        _options = options;
        _tokens = CreateTokens(options);
    }

    public AnalyticsOptions Options => _options;

    public void UpdateOptions(AnalyticsOptions options)
    {
        _tokens = CreateTokens(options);
        _options = options;
    }

    private Lazy<GoogleServiceAccountTokenProvider> CreateTokens(AnalyticsOptions options) =>
        new(() => new GoogleServiceAccountTokenProvider(_http, options.LoadServiceAccountJson(), AnalyticsOptions.Scopes, AnalyticsOptions.Platform));

    public string ServiceAccountEmail => _tokens.Value.ClientEmail;

    /// <summary>Every GA4 property the service account can read.</summary>
    public async Task<IReadOnlyList<Ga4Property>> ListPropertiesAsync(CancellationToken ct)
    {
        var list = new List<Ga4Property>();
        string? page = null;
        do
        {
            var url = $"{AdminHost}/accountSummaries?pageSize=200" + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var doc = await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            foreach (var acc in doc.RootElement.Arr("accountSummaries"))
                foreach (var p in acc.Arr("propertySummaries"))
                {
                    var id = p.Str("property")?.Replace("properties/", "");
                    if (!string.IsNullOrEmpty(id)) list.Add(new Ga4Property(id, p.Str("displayName"), acc.Str("displayName")));
                }
            page = doc.RootElement.Str("nextPageToken");
        } while (!string.IsNullOrEmpty(page));

        // Resolve the app names under each property so the UI can show "Gapsy (com.meeza.colorslide)" instead of a Firebase project id.
        // Best effort: a property the key cannot read streams for keeps Apps = null.
        for (var i = 0; i < list.Count; i++)
        {
            try { list[i] = list[i] with { Apps = await ListStreamNamesAsync(list[i].PropertyId, ct).ConfigureAwait(false) }; }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return list;
    }

    /// <summary>Human-readable names of the data streams of a property: "Display name (package or bundle id)" or the web stream's default URL.</summary>
    private async Task<IReadOnlyList<string>> ListStreamNamesAsync(string propertyId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{AdminHost}/properties/{propertyId}/dataStreams?pageSize=50", null, ct).ConfigureAwait(false);
        var names = new List<string>();
        foreach (var s in doc.RootElement.Arr("dataStreams"))
        {
            var display = s.Str("displayName");
            var id = s.TryGetProperty("androidAppStreamData", out var a) ? a.Str("packageName")
                   : s.TryGetProperty("iosAppStreamData", out var io) ? io.Str("bundleId")
                   : s.TryGetProperty("webStreamData", out var w) ? w.Str("defaultUri")
                   : null;
            var name = string.IsNullOrEmpty(display) ? id : string.IsNullOrEmpty(id) || id == display ? display : $"{display} ({id})";
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Runs a Data API report. <paramref name="body"/> is a RunReportRequest written with camelCase names.</summary>
    public async Task<Ga4Report> RunReportAsync(string propertyId, object body, CancellationToken ct)
    {
        var id = AnalyticsOptions.NormalizePropertyId(propertyId);
        using var doc = await SendAsync(HttpMethod.Post, $"{DataHost}/properties/{id}:runReport", JsonContent.Create(body, options: RequestJson), ct).ConfigureAwait(false);
        return ParseReport(doc.RootElement);
    }

    public static Ga4Report ParseReport(JsonElement root)
    {
        var dims = root.Arr("dimensionHeaders").Select(h => h.Str("name") ?? "").ToArray();
        var mets = root.Arr("metricHeaders").Select(h => h.Str("name") ?? "").ToArray();
        var rows = root.Arr("rows").Select(r => new Ga4Row(
            r.Arr("dimensionValues").Select(v => v.Str("value") ?? "").ToArray(),
            r.Arr("metricValues").Select(v => v.Dec("value")).ToArray())).ToArray();
        return new Ga4Report(dims, mets, rows,
            root.Str("metadata.currencyCode"), root.Str("metadata.timeZone"),
            root.Bool("metadata.dataLossFromOtherRow") ?? false,
            root.Bool("metadata.subjectToThresholding") ?? false);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken ct)
    {
        if (!_options.IsConfigured) throw new PlatformNotConfiguredException(AnalyticsOptions.Platform, AnalyticsOptions.Hint);
        using var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.Value.GetAccessTokenAsync(ct).ConfigureAwait(false));
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode) return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        throw new PlatformApiException(AnalyticsOptions.Platform, DescribeError((int)resp.StatusCode, text, url.StartsWith(AdminHost)), resp.StatusCode);
    }

    public static string DescribeError(int status, string body, bool admin = false)
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
        var disabled = message.Contains("has not been used", StringComparison.OrdinalIgnoreCase) || message.Contains("is disabled", StringComparison.OrdinalIgnoreCase);
        var hint = status switch
        {
            401 => " Check the service account key.",
            403 when disabled => admin
                ? " Enable the Google Analytics Admin API in the service account's Cloud project."
                : " Enable the Google Analytics Data API in the service account's Cloud project.",
            403 => " Add the service account email as a Viewer in GA4 → Admin → Property access management.",
            400 => " Check the property ID (GA4 → Admin → Property details; digits only).",
            429 => " GA4 API quota reached; try again later.",
            _ => "",
        };
        return Redactor.Redact($"{message.TrimEnd('.', ' ')}.{hint}");
    }
}
