using System.ComponentModel;
using System.Text.Json;
using Meeza.Marketing.Meta.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.Meta;

/// <summary>Read-only MCP tools for Meta (Facebook / Instagram) Ads.</summary>
[McpServerToolType]
public sealed class MetaAdsTools(MetaAdsClient client, MetaAdsPlatform platform)
{
    private const string DateHelp = "Date preset: today, yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    [McpServerTool(Name = "meta_get_ad_accounts", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List the Meta ad accounts this server is allowed to query, with currency, time zone and status.")]
    public Task<string> GetAdAccounts(CancellationToken ct) =>
        ToolRunner.RunAsync("meta_get_ad_accounts", async r =>
        {
            r.Data = await platform.GetAccountsAsync(ct);
            if (client.Options.AdAccountIds.Count > 0) r.Notes.Add("Limited to the accounts listed in META_AD_ACCOUNT_IDS.");
        });

    [McpServerTool(Name = "meta_get_campaigns", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List Meta campaigns with status, objective and budget. Budgets are in the account currency.")]
    public Task<string> GetCampaigns(
        [Description("Ad account ID (act_123 or 123). Omit to use every allowed account.")] string? accountId = null,
        [Description("Optional comma-separated effective statuses, e.g. ACTIVE,PAUSED. Omit for all.")] string? statuses = null,
        [Description("Maximum campaigns per account (default 200, max 1000).")] int limit = 200,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_campaigns", async r =>
        {
            var statusList = statuses?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToUpperInvariant()).ToArray();
            var result = new List<object>();
            foreach (var account in await platform.ResolveAccountsAsync(accountId, ct))
            {
                var campaigns = await client.GetCampaignsAsync(account.AccountId, statusList, Math.Clamp(limit, 1, 1000), ct);
                result.AddRange(campaigns.Select(c => new
                {
                    accountId = account.AccountId,
                    id = c.Str("id"),
                    name = c.Str("name"),
                    status = c.Str("status"),
                    effectiveStatus = c.Str("effective_status"),
                    objective = c.Str("objective"),
                    buyingType = c.Str("buying_type"),
                    dailyBudget = MetaMapping.Budget(c, "daily_budget", account.Currency),
                    lifetimeBudget = MetaMapping.Budget(c, "lifetime_budget", account.Currency),
                    currency = account.Currency,
                    startTime = c.Str("start_time"),
                    stopTime = c.Str("stop_time"),
                    createdTime = c.Str("created_time"),
                }));
            }
            r.Data = result;
            r.Notes.Add("Campaigns using ad set budgets (ABO) show null campaign budgets; see meta_get_adsets.");
        });

    [McpServerTool(Name = "meta_get_adsets", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List Meta ad sets (optionally for one campaign) with optimization goal, bid strategy, budget and promoted app.")]
    public Task<string> GetAdSets(
        [Description("Ad account ID. Omit to use every allowed account.")] string? accountId = null,
        [Description("Optional campaign ID to restrict results.")] string? campaignId = null,
        [Description("Maximum ad sets per account (default 200, max 1000).")] int limit = 200,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_adsets", async r =>
        {
            var accounts = await platform.ResolveAccountsAsync(accountId, ct);
            if (campaignId is not null && accounts.Count > 1)
                throw new ArgumentException("Pass accountId together with campaignId so account access can be validated.");
            var result = new List<object>();
            foreach (var account in accounts)
            {
                var items = await client.GetAdSetsAsync(account.AccountId, campaignId, Math.Clamp(limit, 1, 1000), ct);
                result.AddRange(items.Select(a => new
                {
                    accountId = account.AccountId,
                    id = a.Str("id"),
                    name = a.Str("name"),
                    campaignId = a.Str("campaign_id"),
                    status = a.Str("status"),
                    effectiveStatus = a.Str("effective_status"),
                    optimizationGoal = a.Str("optimization_goal"),
                    billingEvent = a.Str("billing_event"),
                    bidStrategy = a.Str("bid_strategy"),
                    dailyBudget = MetaMapping.Budget(a, "daily_budget", account.Currency),
                    lifetimeBudget = MetaMapping.Budget(a, "lifetime_budget", account.Currency),
                    currency = account.Currency,
                    promotedApplicationId = a.Str("promoted_object.application_id"),
                    promotedStoreUrl = a.Str("promoted_object.object_store_url"),
                    startTime = a.Str("start_time"),
                    endTime = a.Str("end_time"),
                }));
            }
            r.Data = result;
        });

    [McpServerTool(Name = "meta_get_ads", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List Meta ads (optionally for one ad set) with status and creative reference.")]
    public Task<string> GetAds(
        [Description("Ad account ID. Omit to use every allowed account.")] string? accountId = null,
        [Description("Optional ad set ID to restrict results.")] string? adSetId = null,
        [Description("Maximum ads per account (default 200, max 1000).")] int limit = 200,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_ads", async r =>
        {
            var accounts = await platform.ResolveAccountsAsync(accountId, ct);
            if (adSetId is not null && accounts.Count > 1)
                throw new ArgumentException("Pass accountId together with adSetId so account access can be validated.");
            var result = new List<object>();
            foreach (var account in accounts)
            {
                var items = await client.GetAdsAsync(account.AccountId, adSetId, Math.Clamp(limit, 1, 1000), ct);
                result.AddRange(items.Select(a => new
                {
                    accountId = account.AccountId,
                    id = a.Str("id"),
                    name = a.Str("name"),
                    campaignId = a.Str("campaign_id"),
                    adSetId = a.Str("adset_id"),
                    status = a.Str("status"),
                    effectiveStatus = a.Str("effective_status"),
                    creativeId = a.Str("creative.id"),
                    creativeName = a.Str("creative.name"),
                    creativeTitle = a.Str("creative.title"),
                    creativeType = a.Str("creative.object_type"),
                    createdTime = a.Str("created_time"),
                }));
            }
            r.Data = result;
        });

    [McpServerTool(Name = "meta_get_ad_insights", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Meta performance insights: spend, impressions, reach, clicks, CTR, CPM, CPC, installs, CPI and all action counts, at account/campaign/adset/ad level with optional breakdowns.")]
    public Task<string> GetAdInsights(
        [Description("Ad account ID. Omit to use every allowed account.")] string? accountId = null,
        [Description("Aggregation level: account, campaign (default), adset or ad.")] string level = "campaign",
        [Description("Optional breakdown: country, age, gender, age,gender, publisher_platform, platform_position, device_platform.")] string? breakdown = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd (account time zone).")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Time series granularity: all_days (default, one row per object), 1 (daily), 7 (weekly) or monthly.")] string? timeIncrement = "all_days",
        [Description("Include the raw action_type counts (purchases, registrations, app events...). Default true.")] bool includeActions = true,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_ad_insights", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var lvl = level.Trim().ToLowerInvariant();
            if (lvl is not ("account" or "campaign" or "adset" or "ad")) throw new ArgumentException("level must be account, campaign, adset or ad.");
            var allowedBreakdowns = new HashSet<string> { "country", "region", "age", "gender", "publisher_platform", "platform_position", "device_platform", "impression_device" };
            var bd = breakdown?.Replace(" ", "");
            if (!string.IsNullOrEmpty(bd) && bd.Split(',').Any(b => !allowedBreakdowns.Contains(b)))
                throw new ArgumentException($"Unsupported breakdown. Use: {string.Join(", ", allowedBreakdowns)}.");

            var rows = new List<object>();
            foreach (var account in await platform.ResolveAccountsAsync(accountId, ct))
            {
                var insights = await client.GetInsightsAsync(account.AccountId, lvl, range, bd, timeIncrement, 10_000, ct);
                foreach (var row in insights)
                {
                    var perf = MetaMapping.ToPerformance(row, range, account.TimeZone, account.AccountId);
                    rows.Add(new
                    {
                        performance = perf,
                        adSetId = row.Str("adset_id"),
                        adSetName = row.Str("adset_name"),
                        adId = row.Str("ad_id"),
                        adName = row.Str("ad_name"),
                        age = row.Str("age"),
                        gender = row.Str("gender"),
                        region = row.Str("region"),
                        publisherPlatform = row.Str("publisher_platform"),
                        platformPosition = row.Str("platform_position"),
                        devicePlatform = row.Str("device_platform"),
                        actions = includeActions ? MetaMapping.Actions(row) : null,
                        costPerAction = includeActions ? MetaMapping.Actions(row, "cost_per_action_type") : null,
                    });
                }
            }
            r.Data = rows;
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.Ratios,
                "Meta installs come from the mobile_app_install action (attributed by Meta's default attribution setting).",
                "Meta returns no row for objects with no delivery in the period."]);
        });

    [McpServerTool(Name = "meta_get_country_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Meta performance broken down by country (spend, impressions, clicks, installs, CPI), aggregated across campaigns.")]
    public Task<string> GetCountryPerformance(
        [Description("Ad account ID. Omit to use every allowed account.")] string? accountId = null,
        [Description("Optional comma-separated ISO country codes to keep, e.g. PK,PH,US.")] string? countries = null,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_country_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await platform.GetCampaignPerformanceAsync(accountId, range, byCountry: true, ct);
            r.Data = Aggregation.ByCountry(rows, Aggregation.ParseCountries(countries));
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.Ratios]);
        });

    [McpServerTool(Name = "meta_get_cost_per_install", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Meta cost per install by campaign (optionally by country), using Meta-attributed mobile_app_install events. Lists campaigns that spent money with no reported installs.")]
    public Task<string> GetCostPerInstall(
        [Description("Ad account ID. Omit to use every allowed account.")] string? accountId = null,
        [Description("Break down by country as well as campaign.")] bool byCountry = false,
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("meta_get_cost_per_install", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = await platform.GetCampaignPerformanceAsync(accountId, range, byCountry, ct);
            r.Data = Aggregation.CpiReport(rows);
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.PlatformAttribution,
                "Campaigns without install actions may not be app-install campaigns, or may lack SDK/MMP install tracking."]);
        });
}
