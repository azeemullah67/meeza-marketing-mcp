using System.ComponentModel;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.GoogleAds;

/// <summary>Read-only MCP tools for Google Ads.</summary>
[McpServerToolType]
public sealed class GoogleAdsTools(GoogleAdsPlatform platform)
{
    private const string DateHelp = "Date preset: today, yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";
    private const string CustomerHelp = "Google Ads customer ID (123-456-7890). Omit to use every allowed non-manager account.";

    [McpServerTool(Name = "google_ads_get_accounts", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List the Google Ads accounts this server may query, with currency, time zone, status and whether each is a manager account.")]
    public Task<string> GetAccounts(CancellationToken ct) =>
        ToolRunner.RunAsync("google_ads_get_accounts", async r =>
        {
            r.Data = await platform.GetAccountsAsync(ct);
            r.Notes.Add("Manager (MCC) accounts have no metrics of their own; reports use their client accounts.");
        });

    [McpServerTool(Name = "google_ads_get_campaigns", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List Google Ads campaigns with status, channel type, bidding strategy, daily budget and promoted app.")]
    public Task<string> GetCampaigns(
        [Description(CustomerHelp)] string? customerId = null,
        [Description("Include removed campaigns. Default false.")] bool includeRemoved = false,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_campaigns", async r =>
        {
            var data = new List<object>();
            foreach (var (account, rows) in await platform.QueryAsync(customerId, GoogleAdsQueries.Campaigns(includeRemoved), ct))
                data.AddRange(rows.Select(c => new
                {
                    customerId = account.AccountId,
                    id = c.Str("campaign.id"),
                    name = c.Str("campaign.name"),
                    status = c.Str("campaign.status"),
                    servingStatus = c.Str("campaign.servingStatus"),
                    channelType = c.Str("campaign.advertisingChannelType"),
                    channelSubType = c.Str("campaign.advertisingChannelSubType"),
                    biddingStrategy = c.Str("campaign.biddingStrategyType"),
                    dailyBudget = GoogleAdsMapping.Micros(c, "campaignBudget.amountMicros"),
                    currency = account.Currency,
                    appId = c.Str("campaign.appCampaignSetting.appId"),
                    appStore = c.Str("campaign.appCampaignSetting.appStore"),
                }));
            r.Data = data;
        });

    [McpServerTool(Name = "google_ads_get_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Google Ads performance (spend, impressions, clicks, CTR, CPC, CPM, conversions, installs, CPI) by campaign or ad group, optionally as a daily series.")]
    public Task<string> GetPerformance(
        [Description(CustomerHelp)] string? customerId = null,
        [Description("campaign (default) or ad_group.")] string level = "campaign",
        [Description("Return one row per day (campaign level only). Default false.")] bool daily = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd (account time zone).")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.Ratios]);

            switch (level.Trim().ToLowerInvariant())
            {
                case "campaign" when !daily:
                    r.Data = await platform.GetCampaignPerformanceAsync(customerId, range, byCountry: false, ct);
                    r.Notes.Add("Installs = conversions from enabled app-install (DOWNLOAD category) conversion actions.");
                    break;
                case "campaign":
                {
                    var data = new List<object>();
                    foreach (var (account, rows) in await platform.QueryAsync(customerId, GoogleAdsQueries.CampaignPerformance(range, daily: true), ct))
                        data.AddRange(rows.Select(x => new
                        {
                            date = x.Str("segments.date"),
                            performance = GoogleAdsMapping.ToPerformance(x, account, range, null, null) with { Installs = null, Notes = null },
                        }));
                    r.Data = data;
                    r.Notes.Add("Daily series omits installs; use google_ads_get_cost_per_install for install metrics.");
                    break;
                }
                case "ad_group" or "adgroup":
                {
                    var data = new List<object>();
                    foreach (var (account, rows) in await platform.QueryAsync(customerId, GoogleAdsQueries.AdGroupPerformance(range), ct))
                        data.AddRange(rows.Select(x => new
                        {
                            adGroupId = x.Str("adGroup.id"),
                            adGroupName = x.Str("adGroup.name"),
                            adGroupStatus = x.Str("adGroup.status"),
                            performance = GoogleAdsMapping.ToPerformance(x, account, range, null, null) with { Installs = null, Notes = null },
                        }));
                    r.Data = data;
                    break;
                }
                default:
                    throw new ArgumentException("level must be campaign or ad_group.");
            }
        });

    [McpServerTool(Name = "google_ads_get_conversion_metrics", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Google Ads conversions broken down by conversion action (name and category such as DOWNLOAD, PURCHASE) per campaign.")]
    public Task<string> GetConversionMetrics(
        [Description(CustomerHelp)] string? customerId = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_conversion_metrics", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<object>();
            foreach (var (account, rows) in await platform.QueryAsync(customerId, GoogleAdsQueries.ConversionsByAction(range), ct))
                data.AddRange(rows.Select(x => new
                {
                    customerId = account.AccountId,
                    campaignId = x.Str("campaign.id"),
                    campaignName = x.Str("campaign.name"),
                    conversionAction = x.Str("segments.conversionActionName"),
                    category = x.Str("segments.conversionActionCategory"),
                    conversions = x.Dec("metrics.conversions"),
                    allConversions = x.Dec("metrics.allConversions"),
                    conversionsValue = x.Dec("metrics.conversionsValue"),
                    allConversionsValue = x.Dec("metrics.allConversionsValue"),
                    currency = account.Currency,
                }));
            r.Data = data;
            r.Notes.Add("conversions counts only actions marked primary for bidding; allConversions also includes secondary actions and view-through/cross-device conversions.");
        });

    [McpServerTool(Name = "google_ads_get_country_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Google Ads performance by country (user's physical location), aggregated across campaigns: spend, clicks, installs, CPI.")]
    public Task<string> GetCountryPerformance(
        [Description(CustomerHelp)] string? customerId = null,
        [Description("Optional comma-separated ISO country codes or names to keep, e.g. PK,PH,US.")] string? countries = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_country_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await platform.GetCampaignPerformanceAsync(customerId, range, byCountry: true, ct);
            r.Data = Aggregation.ByCountry(rows, Aggregation.ParseCountries(countries));
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable,
                "Country = user's physical location (geographic_view LOCATION_OF_PRESENCE). Totals can differ slightly from campaign totals because some traffic has no resolvable location."]);
        });

    [McpServerTool(Name = "google_ads_get_cost_per_install", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Google Ads cost per install by campaign (optionally by country), using app-install (DOWNLOAD) conversion actions. Lists campaigns spending without installs.")]
    public Task<string> GetCostPerInstall(
        [Description(CustomerHelp)] string? customerId = null,
        [Description("Break down by country as well as campaign.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_cost_per_install", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await platform.GetCampaignPerformanceAsync(customerId, range, byCountry, ct);
            r.Data = Aggregation.CpiReport(rows);
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution,
                "Installs = conversions from enabled DOWNLOAD-category conversion actions (Google Play / Firebase / MMP linked)."]);
        });

    [McpServerTool(Name = "google_ads_get_asset_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Per-asset results inside Google App (and other asset-based) ads: each headline, description, image and YouTube video with spend, impressions, clicks, installs, CTR, CPI and Google's own performance rating (LEARNING, LOW, GOOD, BEST). Use it to see which creative pieces to keep or replace.")]
    public Task<string> GetAssetPerformance(
        [Description(CustomerHelp)] string? customerId = null,
        [Description("Optional field filter, e.g. HEADLINE, DESCRIPTION, MARKETING_IMAGE, YOUTUBE_VIDEO.")] string? fieldType = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("google_ads_get_asset_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            IEnumerable<AssetPerformance> rows = await platform.GetAssetPerformanceAsync(customerId, range, ct);
            if (!string.IsNullOrWhiteSpace(fieldType))
                rows = rows.Where(a => string.Equals(a.FieldType, fieldType.Trim(), StringComparison.OrdinalIgnoreCase));
            r.Data = CreativeLab.ClassifyAssets(rows);
            r.Notes.AddRange([ReportingNotes.AssetOverlap, ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable,
                "Installs = conversions credited to the asset from enabled DOWNLOAD-category conversion actions. Most installs are usually not broken down by asset."]);
        });
}
