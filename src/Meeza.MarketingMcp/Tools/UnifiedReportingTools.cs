using System.ComponentModel;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Services;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

/// <summary>
/// Cross-platform reporting tools. Data is normalized to <see cref="CampaignPerformance"/> and always
/// grouped by currency; installs from different measurement sources are reported side by side, never summed.
/// </summary>
[McpServerToolType]
public sealed class UnifiedReportingTools(PlatformRegistry registry, AnalyticsService analytics, Meeza.Marketing.Adjust.AdjustService adjust)
{
    private const string PlatformsHelp = "Optional comma-separated platforms: meta, google_ads. Omit for all configured platforms.";
    private const string DateHelp = "Date preset: today, yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    [McpServerTool(Name = "get_connected_accounts", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Show which platforms (Meta Ads, Google Ads, Google Play, Google Analytics 4 / Firebase, Adjust) are configured and list the accounts/apps each one can access. Start here.")]
    public Task<string> GetConnectedAccounts(CancellationToken ct) =>
        ToolRunner.RunAsync("get_connected_accounts", async r =>
        {
            var platforms = new List<object>();
            foreach (var p in registry.AdPlatforms)
            {
                if (!p.IsConfigured) { platforms.Add(new { platform = p.Platform, configured = false, hint = p.ConfigurationHint }); continue; }
                try
                {
                    platforms.Add(new { platform = p.Platform, configured = true, accounts = await p.GetAccountsAsync(ct) });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    platforms.Add(new { platform = p.Platform, configured = true, error = ToolRunner.Describe(ex) });
                }
            }

            var play = registry.Play;
            if (!play.IsConfigured)
                platforms.Add(new { platform = PlayConsoleOptions.Platform, configured = false, hint = PlayConsoleOptions.Hint });
            else
                try
                {
                    platforms.Add(new
                    {
                        platform = PlayConsoleOptions.Platform,
                        configured = true,
                        apps = await play.GetAppsAsync(ct),
                        storeInstallReports = play.Client.Options.ReportsBucket is not null,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    platforms.Add(new { platform = PlayConsoleOptions.Platform, configured = true, error = ToolRunner.Describe(ex) });
                }

            if (!analytics.IsConfigured)
                platforms.Add(new { platform = AnalyticsOptions.Platform, configured = false, hint = AnalyticsOptions.Hint });
            else
                try
                {
                    platforms.Add(new { platform = AnalyticsOptions.Platform, configured = true, serviceAccount = analytics.SafeEmail(), properties = await analytics.GetPropertiesAsync(ct) });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    platforms.Add(new { platform = AnalyticsOptions.Platform, configured = true, error = ToolRunner.Describe(ex) });
                }

            if (!adjust.IsConfigured)
                platforms.Add(new { platform = Meeza.Marketing.Adjust.AdjustOptions.Platform, configured = false, hint = Meeza.Marketing.Adjust.AdjustOptions.Hint });
            else
                try
                {
                    platforms.Add(new { platform = Meeza.Marketing.Adjust.AdjustOptions.Platform, configured = true, currency = adjust.Currency, apps = await adjust.GetAppsAsync(ct) });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    platforms.Add(new { platform = Meeza.Marketing.Adjust.AdjustOptions.Platform, configured = true, error = ToolRunner.Describe(ex) });
                }

            r.Data = platforms;
            r.Notes.Add("All access is read-only. This server cannot create, change or pause campaigns.");
        });

    [McpServerTool(Name = "get_campaign_summary", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Summarize campaign performance across ad platforms: per-account totals and the top campaigns by spend with CTR, CPC, CPM, installs and CPI.")]
    public Task<string> GetCampaignSummary(
        [Description(PlatformsHelp)] string? platforms = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Maximum campaigns to list (default 50).")] int topCampaigns = 50,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_campaign_summary", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await registry.CollectAsync(platforms, range, byCountry: false, r, ct);
            r.Data = new
            {
                totalsByAccount = Aggregation.ByAccount(rows),
                campaignCount = rows.Count,
                topCampaignsBySpend = rows.OrderByDescending(x => x.Spend ?? 0).Take(Math.Clamp(topCampaigns, 1, 500)).ToArray(),
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.Ratios, ReportingNotes.TimeZone]);
        });

    [McpServerTool(Name = "get_spend_summary", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Compare advertising spend across platforms and accounts, grouped by currency, with each platform's share of spend.")]
    public Task<string> GetSpendSummary(
        [Description(PlatformsHelp)] string? platforms = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_spend_summary", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await registry.CollectAsync(platforms, range, byCountry: false, r, ct);
            var byPlatform = Aggregation.ByPlatform(rows);
            var shares = byPlatform.GroupBy(t => t.Currency).SelectMany(g =>
            {
                var total = g.Sum(t => t.Spend ?? 0);
                return g.Select(t => new
                {
                    t.Platform, t.Currency, t.Spend,
                    shareOfCurrencySpendPercent = total > 0 ? MetricMath.Round((t.Spend ?? 0) / total * 100m) : (decimal?)null,
                    averageDailySpend = t.Spend is null ? null : (decimal?)MetricMath.Round(t.Spend.Value / range.Days),
                });
            }).ToArray();
            r.Data = new { byPlatform = shares, byAccount = Aggregation.ByAccount(rows), days = range.Days };
            r.Notes.Add(ReportingNotes.Currency);
            if (byPlatform.Select(t => t.Currency).Distinct().Count() > 1)
                r.Notes.Add("Accounts use different currencies; spend shares are computed within each currency only.");
        });

    [McpServerTool(Name = "get_install_summary", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Compare installs from every available source side by side: Meta-attributed, Google Ads-attributed and Play Console store-reported installs (organic + paid). Sources are never summed.")]
    public Task<string> GetInstallSummary(
        [Description(PlatformsHelp)] string? platforms = null,
        [Description("Android package name for Play Console store installs. Omit to use every allowed app.")] string? packageName = null,
        [Description("Break down by country.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_install_summary", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await registry.CollectAsync(platforms, range, byCountry, r, ct);
            var attributed = byCountry ? Aggregation.ByCountry(rows) : Aggregation.ByPlatform(rows);

            var store = new List<object>();
            var play = registry.Play;
            if (!play.IsConfigured)
                r.Notes.Add("Google Play is not configured, so store-reported installs are not included. " + PlayConsoleOptions.Hint);
            else if (play.Client.Options.ReportsBucket is null)
                r.Notes.Add("PLAY_REPORTS_BUCKET is not set, so Play Console store-reported installs are not included.");
            else
                try
                {
                    foreach (var pkg in await play.ResolvePackagesAsync(packageName, ct))
                        store.Add(await play.GetStoreInstallsAsync(pkg, range, byCountry, ct));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.Errors.Add(new PlatformError(PlayConsoleOptions.Platform, ToolRunner.Describe(ex)));
                }

            r.Data = new
            {
                adPlatformAttributedInstalls = attributed.Select(t => new { t.Platform, t.Country, t.CountryName, t.Currency, t.Spend, t.Installs, t.Cpi }).ToArray(),
                storeReportedInstalls = store,
            };
            r.Notes.AddRange([ReportingNotes.PlatformAttribution, ReportingNotes.StoreVsAttributed, ReportingNotes.NullMeansUnavailable,
                "A rough paid-vs-organic signal can be discussed by comparing attributed installs with store installs, but it is an estimate, not a measurement."]);
        });

    [McpServerTool(Name = "get_country_comparison", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Compare countries across ad platforms: spend, impressions, clicks, CTR, CPC, installs and CPI per platform and country.")]
    public Task<string> GetCountryComparison(
        [Description("Optional comma-separated ISO codes or names to compare, e.g. PK,PH,US or Pakistan,Philippines,United States.")] string? countries = null,
        [Description(PlatformsHelp)] string? platforms = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_country_comparison", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var filter = Aggregation.ParseCountries(countries);
            var rows = await registry.CollectAsync(platforms, range, byCountry: true, r, ct);
            var totals = Aggregation.ByCountry(rows, filter);
            r.Data = new
            {
                byPlatformAndCountry = totals,
                requestedCountriesWithoutData = filter?.Where(c => totals.All(t => !string.Equals(t.Country, c, StringComparison.OrdinalIgnoreCase))).ToArray(),
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution, ReportingNotes.NullMeansUnavailable,
                "Meta country = where the ad was delivered; Google Ads country = user's physical location. Small differences in definition apply."]);
        });

    [McpServerTool(Name = "get_cpi_comparison", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Compare cost per install across platforms and campaigns (optionally by country), and list campaigns spending money without installs.")]
    public Task<string> GetCpiComparison(
        [Description(PlatformsHelp)] string? platforms = null,
        [Description("Break down by country as well as campaign.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_cpi_comparison", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await registry.CollectAsync(platforms, range, byCountry, r, ct);
            r.Data = Aggregation.CpiReport(rows);
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution,
                "CPI is only comparable between platforms when both use the same currency and comparable attribution windows."]);
        });

    [McpServerTool(Name = "get_date_range_report", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Normalized campaign-level rows from every selected ad platform for a date range (optionally by country). Use for custom analysis.")]
    public Task<string> GetDateRangeReport(
        [Description(PlatformsHelp)] string? platforms = null,
        [Description("Break rows down by country.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Maximum rows (default 1000, max 5000).")] int maxRows = 1000,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_date_range_report", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await registry.CollectAsync(platforms, range, byCountry, r, ct);
            var limit = Math.Clamp(maxRows, 1, 5000);
            r.Data = new
            {
                totalRows = rows.Count,
                truncated = rows.Count > limit,
                rows = rows.OrderByDescending(x => x.Spend ?? 0).Take(limit).ToArray(),
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.Ratios, ReportingNotes.TimeZone]);
        });
}
