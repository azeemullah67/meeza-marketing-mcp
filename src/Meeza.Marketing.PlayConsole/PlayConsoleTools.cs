using System.ComponentModel;
using Meeza.Marketing.PlayConsole.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.PlayConsole;

/// <summary>Read-only MCP tools for Google Play Console.</summary>
[McpServerToolType]
public sealed class PlayConsoleTools(PlayConsoleService service)
{
    private const string PackageHelp = "Android package name, e.g. com.example.game. Omit to use every allowed app.";
    private const string DateHelp = "Date preset: yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    private static readonly string[] VitalsDimensions =
        ["versionCode", "apiLevel", "countryCode", "deviceModel", "deviceBrand", "deviceType", "deviceRamBucket", "deviceSocMake", "deviceSocModel"];

    // Google Play "bad behavior" thresholds (overall, user-perceived), shown for context.
    public const decimal CrashThresholdPercent = 1.09m;
    public const decimal AnrThresholdPercent = 0.47m;

    [McpServerTool(Name = "play_get_app_details", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List the Google Play apps this server may query (package name and display name) and which Play data sources are configured.")]
    public Task<string> GetAppDetails(CancellationToken ct) =>
        ToolRunner.RunAsync("play_get_app_details", async r =>
        {
            var apps = await service.GetAppsAsync(ct);
            r.Data = new
            {
                serviceAccount = service.Client.ServiceAccountEmail,
                reportsBucketConfigured = service.Client.Options.ReportsBucket is not null,
                apps,
            };
            if (apps.Any(a => !a.VisibleToServiceAccount))
                r.Notes.Add("Some allowed packages are not visible to the service account in the Reporting API; grant it access in Play Console → Users and permissions.");
            r.Notes.Add("Store listing details are not read, because the Play Developer API only exposes them through edit sessions (a write workflow).");
        });

    [McpServerTool(Name = "play_get_reviews", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Recent Google Play user reviews with star rating, text, app version, device and any developer reply. The API only returns reviews with text created or edited in the last 7 days.")]
    public Task<string> GetReviews(
        [Description(PackageHelp)] string? packageName = null,
        [Description("Maximum reviews per app (default 100, max 1000).")] int maxResults = 100,
        [Description("Optional BCP-47 language to translate reviews into, e.g. en.")] string? translationLanguage = null,
        [Description("Optional filter: only reviews with at most this many stars (e.g. 2 for negative reviews).")] int? maxStars = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("play_get_reviews", async r =>
        {
            var data = new List<object>();
            foreach (var pkg in await service.ResolvePackagesAsync(packageName, ct))
            {
                var reviews = await service.Client.GetReviewsAsync(pkg, Math.Clamp(maxResults, 1, 1000), translationLanguage, ct);
                var filtered = reviews.Where(x => maxStars is null || PlayReports.Stars(x) <= maxStars).ToList();
                data.Add(new { packageName = pkg, count = filtered.Count, reviews = filtered.Select(PlayReports.ToReview).ToArray() });
            }
            r.Data = data;
            r.Notes.Add("Only reviews with comments created or modified in the last 7 days are returned by the Play Developer API. Older reviews require the Play Console reviews export.");
        });

    [McpServerTool(Name = "play_get_ratings", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Google Play rating information: average ratings from the Play Console ratings export (if PLAY_REPORTS_BUCKET is set) plus the star distribution of recent reviews.")]
    public Task<string> GetRatings(
        [Description(PackageHelp)] string? packageName = null,
        [Description("Break the ratings export down by country.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("play_get_ratings", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<object>();
            foreach (var pkg in await service.ResolvePackagesAsync(packageName, ct))
            {
                var reviews = await service.Client.GetReviewsAsync(pkg, 1000, null, ct);
                var distribution = reviews.Select(PlayReports.Stars).Where(s => s is not null)
                    .GroupBy(s => s!.Value).OrderBy(g => g.Key).ToDictionary(g => $"{g.Key}_star", g => g.Count());
                var stars = reviews.Select(PlayReports.Stars).Where(s => s is not null).Select(s => (decimal)s!.Value).ToList();

                object? export = null;
                if (service.Client.Options.ReportsBucket is not null)
                {
                    try
                    {
                        var (records, files, missing) = await service.ReadMonthlyReportAsync("ratings", pkg, byCountry ? "country" : "overview", range, ct);
                        export = new { reportFiles = files, missingMonths = missing, summary = PlayReports.SummarizeRatings(records, range, byCountry ? "Country" : null) };
                    }
                    catch (PlatformApiException ex)
                    {
                        r.Errors.Add(new PlatformError(PlayConsoleOptions.Platform, ex.Message));
                    }
                }
                else
                {
                    r.Notes.Add("Set PLAY_REPORTS_BUCKET to include the official average rating from Play Console exports.");
                }

                data.Add(new
                {
                    packageName = pkg,
                    ratingsExport = export,
                    recentReviews = new
                    {
                        window = "last 7 days, reviews with text only",
                        count = stars.Count,
                        averageStars = stars.Count > 0 ? MetricMath.Round(stars.Average()) : (decimal?)null,
                        distribution,
                    },
                });
            }
            r.Data = data;
            r.Notes.Add("The recent-review distribution is a small, biased sample (text reviews from the last 7 days), not the store rating.");
        });

    [McpServerTool(Name = "play_get_android_vitals", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Android vitals overview: user-perceived crash rate and ANR rate for the period, compared with Google Play's bad-behavior thresholds.")]
    public Task<string> GetAndroidVitals(
        [Description(PackageHelp)] string? packageName = null,
        [Description(DateHelp)] string? datePreset = "last_28_days",
        [Description("Start date yyyy-MM-dd (America/Los_Angeles).")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("play_get_android_vitals", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            var data = new List<object>();
            foreach (var pkg in await service.ResolvePackagesAsync(packageName, ct))
            {
                var crash = await QueryVitalsAsync(pkg, "crashRateMetricSet", range, ["crashRate", "userPerceivedCrashRate", "distinctUsers"], [], r, ct);
                var anr = await QueryVitalsAsync(pkg, "anrRateMetricSet", range, ["anrRate", "userPerceivedAnrRate", "distinctUsers"], [], r, ct);
                r.DateRange = crash.Range.ToString();
                data.Add(new
                {
                    packageName = pkg,
                    crash = PlayReports.SummarizeVitals(crash.Rows, ["crashRate", "userPerceivedCrashRate"]),
                    anr = PlayReports.SummarizeVitals(anr.Rows, ["anrRate", "userPerceivedAnrRate"]),
                    thresholds = new { userPerceivedCrashRatePercent = CrashThresholdPercent, userPerceivedAnrRatePercent = AnrThresholdPercent },
                });
            }
            r.Data = data;
            r.Notes.AddRange([
                "Rates are percentages of daily active users, weighted by distinctUsers across the period. Vitals use the America/Los_Angeles time zone.",
                $"Google Play's overall bad-behavior thresholds are {CrashThresholdPercent}% user-perceived crash rate and {AnrThresholdPercent}% user-perceived ANR rate; exceeding them can reduce store visibility.",
            ]);
        });

    [McpServerTool(Name = "play_get_crash_metrics", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Crash rate metrics from Android vitals, optionally broken down by a dimension (versionCode, apiLevel, countryCode, deviceModel...) and/or as a daily series.")]
    public Task<string> GetCrashMetrics(
        [Description(PackageHelp)] string? packageName = null,
        [Description("Optional dimension: versionCode, apiLevel, countryCode, deviceModel, deviceBrand, deviceType, deviceRamBucket.")] string? dimension = null,
        [Description("Include the daily series. Default false (period summary only).")] bool daily = false,
        [Description(DateHelp)] string? datePreset = "last_28_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        VitalsToolAsync("play_get_crash_metrics", "crashRateMetricSet", ["crashRate", "userPerceivedCrashRate"], CrashThresholdPercent,
            packageName, dimension, daily, datePreset, startDate, endDate, ct);

    [McpServerTool(Name = "play_get_anr_metrics", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("ANR (App Not Responding) rate metrics from Android vitals, optionally broken down by a dimension and/or as a daily series.")]
    public Task<string> GetAnrMetrics(
        [Description(PackageHelp)] string? packageName = null,
        [Description("Optional dimension: versionCode, apiLevel, countryCode, deviceModel, deviceBrand, deviceType, deviceRamBucket.")] string? dimension = null,
        [Description("Include the daily series. Default false (period summary only).")] bool daily = false,
        [Description(DateHelp)] string? datePreset = "last_28_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        VitalsToolAsync("play_get_anr_metrics", "anrRateMetricSet", ["anrRate", "userPerceivedAnrRate"], AnrThresholdPercent,
            packageName, dimension, daily, datePreset, startDate, endDate, ct);

    [McpServerTool(Name = "play_get_store_installs", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Store-reported installs/uninstalls from the Play Console installs export (all sources, organic + paid), optionally by country. Requires PLAY_REPORTS_BUCKET.")]
    public Task<string> GetStoreInstalls(
        [Description(PackageHelp)] string? packageName = null,
        [Description("Break down by country.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("play_get_store_installs", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<object>();
            foreach (var pkg in await service.ResolvePackagesAsync(packageName, ct))
                data.Add(await service.GetStoreInstallsAsync(pkg, range, byCountry, ct));
            r.Data = data;
            r.Notes.AddRange([ReportingNotes.StoreVsAttributed,
                "Play Console exports are updated daily with a delay of a few days; recent days may be missing (see latestDateInReports).",
                "primaryInstalls uses the first available column of: " + string.Join(", ", PlayReports.PreferredInstallColumns) + "."]);
        });

    // ---------------------------------------------------------------- helpers

    private Task<string> VitalsToolAsync(string tool, string metricSet, string[] rateMetrics, decimal threshold,
        string? packageName, string? dimension, bool daily, string? datePreset, string? startDate, string? endDate, CancellationToken ct) =>
        ToolRunner.RunAsync(tool, async r =>
        {
            if (dimension is not null && !VitalsDimensions.Contains(dimension))
                throw new ArgumentException($"Unsupported dimension '{dimension}'. Use one of: {string.Join(", ", VitalsDimensions)}.");
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            var dims = dimension is null ? Array.Empty<string>() : [dimension];
            var data = new List<object>();
            foreach (var pkg in await service.ResolvePackagesAsync(packageName, ct))
            {
                var result = await QueryVitalsAsync(pkg, metricSet, range, [.. rateMetrics, "distinctUsers"], dims, r, ct);
                r.DateRange = result.Range.ToString();
                data.Add(new
                {
                    packageName = pkg,
                    summary = PlayReports.SummarizeVitals(result.Rows, rateMetrics).Take(50).ToArray(),
                    dailySeries = daily ? result.Rows.OrderBy(x => x.Date).Take(2000).ToArray() : null,
                });
            }
            r.Data = data;
            r.Notes.AddRange([
                "Summary rates are percentages weighted by distinctUsers; dailySeries metric values are fractions (0.01 = 1%).",
                $"Google Play's overall bad-behavior threshold for the user-perceived rate is {threshold}%.",
                "Vitals use the America/Los_Angeles time zone and typically lag 1-2 days.",
            ]);
        });

    private async Task<(DateRange Range, IReadOnlyList<PlayReports.VitalsRow> Rows)> QueryVitalsAsync(
        string pkg, string metricSet, DateRange range, string[] metrics, string[] dims, ToolResponse r, CancellationToken ct)
    {
        var (effective, rows, note) = await service.QueryVitalsAsync(pkg, metricSet, range, metrics, dims, ct);
        if (note is not null && !r.Notes.Contains(note)) r.Notes.Add(note);
        return (effective, rows);
    }
}
