using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Adjust;

/// <summary>An Adjust app: its app token and display name.</summary>
public sealed record AdjustApp(string AppToken, string? Name);

/// <summary>
/// One Report Service response. Rows hold dimension and metric values as text (Adjust sends metric values in rows as
/// strings); <see cref="Number"/> parses them. Missing values are null, never zero.
/// </summary>
public sealed record AdjustReport(IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows, IReadOnlyList<string> Warnings)
{
    public static readonly AdjustReport Empty = new([], []);

    public static string? Text(IReadOnlyDictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static decimal? Number(IReadOnlyDictionary<string, string?> row, string key) =>
        row.TryGetValue(key, out var v) && decimal.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static long? Count(IReadOnlyDictionary<string, string?> row, string key) =>
        Number(row, key) is { } d ? (long)Math.Round(d) : null;
}

/// <summary>
/// Read-only client for the Adjust Report Service API (https://automate.adjust.com/reports-service). Only GET requests
/// are made, to a fixed host, with the API token as a bearer header.
/// </summary>
public sealed class AdjustClient(HttpClient http, AdjustOptions options)
{
    public const string Host = "https://automate.adjust.com/reports-service";

    private volatile AdjustOptions _options = options;

    public AdjustOptions Options => _options;

    public void UpdateOptions(AdjustOptions options) => _options = options;

    /// <summary>Every app the API token can read.</summary>
    public async Task<IReadOnlyList<AdjustApp>> ListAppsAsync(CancellationToken ct)
    {
        using var doc = await SendAsync("/filters_data", [new("required_filters", "apps")], ct).ConfigureAwait(false);
        return ParseApps(doc.RootElement);
    }

    public static IReadOnlyList<AdjustApp> ParseApps(JsonElement root) =>
        root.Arr("apps").Select(a => (Id: a.Str("id"), Name: a.Str("name")))
            .Where(a => !string.IsNullOrWhiteSpace(a.Id))
            .Select(a => new AdjustApp(a.Id!, string.IsNullOrWhiteSpace(a.Name) ? null : a.Name))
            .ToArray();

    /// <summary>Runs a JSON report. <paramref name="query"/> holds the Report Service parameters (dimensions, metrics, date_period, ...).</summary>
    public async Task<AdjustReport> GetReportAsync(IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        using var doc = await SendAsync("/report", query, ct).ConfigureAwait(false);
        return ParseReport(doc.RootElement);
    }

    public static AdjustReport ParseReport(JsonElement root)
    {
        var rows = root.Arr("rows").Where(r => r.ValueKind == JsonValueKind.Object).Select(r =>
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var p in r.EnumerateObject())
            {
                if (p.NameEquals("attr_dependency")) continue;
                row[p.Name] = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    JsonValueKind.True or JsonValueKind.False => p.Value.GetRawText(),
                    _ => null,
                };
            }
            return (IReadOnlyDictionary<string, string?>)row;
        }).ToArray();
        var warnings = root.Arr("warnings").Select(w => w.ValueKind == JsonValueKind.String ? w.GetString() : w.GetRawText())
            .Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).ToArray();
        return new AdjustReport(rows, warnings);
    }

    private async Task<JsonDocument> SendAsync(string path, IReadOnlyList<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        var o = _options;
        if (!o.IsConfigured) throw new PlatformNotConfiguredException(AdjustOptions.Platform, AdjustOptions.Hint);
        var url = new StringBuilder(Host).Append(path);
        for (var i = 0; i < query.Count; i++)
            url.Append(i == 0 ? '?' : '&').Append(Uri.EscapeDataString(query[i].Key)).Append('=').Append(Uri.EscapeDataString(query[i].Value));
        using var req = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ApiToken);
        using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NoContent) return JsonDocument.Parse("{}"); // 204 = no data for the query
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
            catch (JsonException) { throw new PlatformApiException(AdjustOptions.Platform, "Adjust returned a response that is not JSON."); }
        }
        throw new PlatformApiException(AdjustOptions.Platform, DescribeError((int)resp.StatusCode, text), resp.StatusCode);
    }

    public static string DescribeError(int status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            message = root.ValueKind == JsonValueKind.String ? root.GetString()
                : root.Str("detail") ?? root.Str("error") ?? root.Str("message") ?? root.Str("errors");
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(message) || message.Length > 400) message = $"HTTP {status}";
        var hint = status switch
        {
            401 => " Check the Adjust API token (Account settings → My profile). Resetting the token in Adjust invalidates the old one.",
            403 => " This Adjust user has no permission to view that app's data. Ask an Adjust admin for access.",
            400 => " Adjust rejected the report parameters.",
            429 => " Adjust rate limit reached; try again shortly.",
            503 or 504 => " Adjust is busy or the query took too long; try a shorter date range.",
            _ => "",
        };
        return Redactor.Redact($"{message.TrimEnd('.', ' ')}.{hint}");
    }
}
