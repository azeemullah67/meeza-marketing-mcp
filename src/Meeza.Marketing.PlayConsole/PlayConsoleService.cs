using Meeza.Marketing.PlayConsole.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.PlayConsole;

/// <summary>App access validation and report assembly shared by the Play tools and the unified tools.</summary>
public sealed class PlayConsoleService(PlayConsoleClient client)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<PlayApp>? _apps;

    /// <summary>Forgets cached apps (after settings change).</summary>
    public void ResetCache() => _apps = null;
    private DateTimeOffset _fetchedAt;

    public PlayConsoleClient Client => client;
    public bool IsConfigured => client.Options.IsConfigured;

    public sealed record PlayApp(string PackageName, string? DisplayName, bool VisibleToServiceAccount);

    /// <summary>Apps this server may query: the PLAY_PACKAGE_NAMES allow-list, or every app the service account can see.</summary>
    public async Task<IReadOnlyList<PlayApp>> GetAppsAsync(CancellationToken ct)
    {
        if (!IsConfigured) throw new PlatformNotConfiguredException(PlayConsoleOptions.Platform, PlayConsoleOptions.Hint);
        if (_apps is not null && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromMinutes(10)) return _apps;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_apps is not null && DateTimeOffset.UtcNow - _fetchedAt < TimeSpan.FromMinutes(10)) return _apps;
            var visible = (await client.SearchAppsAsync(ct).ConfigureAwait(false))
                .Select(a => (Package: a.Str("packageName")!, Name: a.Str("displayName")))
                .Where(a => a.Package is not null)
                .ToDictionary(a => a.Package, a => a.Name, StringComparer.OrdinalIgnoreCase);

            var allow = client.Options.PackageNames;
            _apps = allow.Count > 0
                ? allow.Select(p => new PlayApp(p, visible.GetValueOrDefault(p), visible.ContainsKey(p))).ToArray()
                : visible.Select(v => new PlayApp(v.Key, v.Value, true)).OrderBy(a => a.PackageName).ToArray();
            _fetchedAt = DateTimeOffset.UtcNow;
            return _apps;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Resolves target packages; validates an explicitly requested one against the allowed set.</summary>
    public async Task<IReadOnlyList<string>> ResolvePackagesAsync(string? packageName, CancellationToken ct)
    {
        var apps = await GetAppsAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(packageName))
        {
            if (apps.Count == 0) throw new ArgumentException("No apps are accessible. Set PLAY_PACKAGE_NAMES and grant the service account access in Play Console.");
            if (apps.Count > 10) throw new ArgumentException($"{apps.Count} apps are accessible; pass packageName or set PLAY_PACKAGE_NAMES.");
            return apps.Select(a => a.PackageName).ToArray();
        }
        var pkg = PlayConsoleOptions.ValidatePackageName(packageName);
        if (!apps.Any(a => string.Equals(a.PackageName, pkg, StringComparison.OrdinalIgnoreCase)))
            throw new AccountAccessDeniedException(PlayConsoleOptions.Platform, pkg,
                client.Options.PackageNames.Count > 0 ? "it is not in PLAY_PACKAGE_NAMES." : "the service account cannot access it.");
        return [pkg];
    }

    /// <summary>Reads a monthly bulk report family (e.g. "installs", "ratings") for one dimension (overview, country, …).</summary>
    public async Task<(List<Dictionary<string, string>> Records, List<string> Files, List<string> MissingMonths)> ReadMonthlyReportAsync(
        string family, string packageName, string dimension, DateRange range, CancellationToken ct)
    {
        var records = new List<Dictionary<string, string>>();
        var files = new List<string>();
        var missing = new List<string>();
        foreach (var month in PlayReports.Months(range))
        {
            var name = $"stats/{family}/{family}_{packageName}_{month}_{dimension}.csv";
            var csv = await client.DownloadReportAsync(name, ct).ConfigureAwait(false);
            if (csv is null) { missing.Add(month); continue; }
            files.Add(name);
            records.AddRange(PlayReports.ToRecords(csv));
        }
        return (records, files, missing);
    }

    /// <summary>
    /// Queries a vitals metric set with the end date clamped to the latest complete day
    /// (the API's freshness end is exclusive). Returns the effective range and an optional note.
    /// </summary>
    public async Task<(DateRange Range, IReadOnlyList<PlayReports.VitalsRow> Rows, string? Note)> QueryVitalsAsync(
        string pkg, string metricSet, DateRange range, IReadOnlyList<string> metrics, IReadOnlyList<string> dims, CancellationToken ct)
    {
        var fresh = await client.GetDailyFreshnessAsync(pkg, metricSet, ct).ConfigureAwait(false);
        var (effective, note) = FitToFreshness(range, fresh);
        var rows = await client.QueryMetricSetAsync(pkg, metricSet, effective, metrics, dims, ct).ConfigureAwait(false);
        return (effective, rows.Select(PlayReports.ToVitalsRow).ToArray(), note);
    }

    /// <summary>
    /// Fits a requested range to the vitals Google has published. <paramref name="freshness"/> is the API's exclusive
    /// end (the day after the latest complete day). Vitals arrive about two days late, so a range of only recent days
    /// (e.g. today) is moved back to the latest available days of the same length instead of failing.
    /// </summary>
    public static (DateRange Range, string? Note) FitToFreshness(DateRange range, DateOnly? freshness)
    {
        if (freshness is not { } f || range.End < f) return (range, null);
        var latest = f.AddDays(-1);
        if (latest >= range.Start)
            return (range with { End = latest }, $"Vitals end date clamped to {latest:yyyy-MM-dd}, the latest day with complete data.");
        var shifted = new DateRange(latest.AddDays(-(range.Days - 1)), latest);
        return (shifted, $"Google Play has no vitals for {range} yet (they arrive about 2 days late), so the latest {range.Days} day(s) available are shown instead: {shifted}.");
    }

    /// <summary>Store-reported installs for the range, from the Play Console installs export.</summary>
    public async Task<object> GetStoreInstallsAsync(string packageName, DateRange range, bool byCountry, CancellationToken ct)
    {
        var (records, files, missing) = await ReadMonthlyReportAsync("installs", packageName, byCountry ? "country" : "overview", range, ct).ConfigureAwait(false);
        var lastDate = records.Select(PlayReports.Date).Where(d => d is not null).DefaultIfEmpty().Max();
        return new
        {
            platform = PlayConsoleOptions.Platform,
            packageName,
            source = "play_console_store_reported",
            reportFiles = files,
            missingMonths = missing,
            latestDateInReports = lastDate,
            breakdown = PlayReports.SummarizeInstalls(records, range, byCountry ? "Country" : null),
        };
    }
}
