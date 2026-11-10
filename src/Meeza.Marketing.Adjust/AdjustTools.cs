using System.ComponentModel;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.Adjust;

/// <summary>Read-only MCP tools for Adjust (mobile measurement partner).</summary>
[McpServerToolType]
public sealed class AdjustTools(AdjustService service)
{
    private const string AppHelp = "Adjust app token (e.g. abc123def456, from adjust_list_apps). Omit to use every allowed app.";
    private const string DateHelp = "Date preset: yesterday, last_7_days, last_14_days, last_30_days (default), last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    public const string AttributionNote =
        "Adjust installs are deduplicated across networks by Adjust's own attribution (first attribution, UTC days). They can differ from installs that Meta or Google Ads report themselves, and from GA4 first_open. Never add them to other install counts.";

    public const string SpendNote =
        "Adjust cost is spend reported to Adjust by connected networks (or tracked through cost parameters), converted by Adjust into the currency shown. It covers the same money as Meta / Google Ads spend: never add the two together.";

    /// <summary>Yesterday in UTC: Adjust reports UTC days and the current day is incomplete.</summary>
    public static DateOnly LastCompleteDay => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

    [McpServerTool(Name = "adjust_list_apps", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("List the Adjust apps (app token and name) this server can read with the configured Adjust API token.")]
    public Task<string> ListApps(CancellationToken ct) =>
        ToolRunner.RunAsync("adjust_list_apps", async r =>
        {
            var apps = await service.GetAppsAsync(ct);
            r.Data = new { currency = service.Currency, apps };
            if (apps.Count == 0) r.Notes.Add("No apps are visible to this API token. Check the Adjust user's app permissions.");
        });

    [McpServerTool(Name = "adjust_get_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Adjust-attributed installs, clicks, impressions, sessions, cost, CPI, CTR, IPM and revenue (all, ad, in-app) for an app, grouped by network (default), campaign, adgroup, creative, country, platform or day. Adjust deduplicates installs across networks, so this is the cross-network view of which source drives installs and at what cost.")]
    public Task<string> GetPerformance(
        [Description(AppHelp)] string? appToken = null,
        [Description("Group by: network (default), campaign, adgroup, creative, country, platform or day.")] string? groupBy = "network",
        [Description(DateHelp)] string? datePreset = "last_30_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        [Description("Maximum rows per app (default 50).")] int top = 50,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("adjust_get_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<AdjustPerformanceReport>();
            foreach (var app in await service.ResolveAppsAsync(appToken, ct))
                data.Add(await service.GetPerformanceAsync(app, range, groupBy, top, ct));
            r.Data = data;
            r.Notes.Add(AttributionNote);
            r.Notes.Add(SpendNote);
            r.Notes.Add("Revenue is revenue earned in the period (by activity date) from all users, not cohort revenue, so revenue ÷ cost here is not cohort ROAS.");
            r.Notes.Add("Dates are UTC days. Today is incomplete.");
            foreach (var w in data.SelectMany(d => d.Warnings).Distinct()) r.Notes.Add($"Adjust: {w}");
        });

    [McpServerTool(Name = "adjust_get_retention", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Install-cohort retention from Adjust: % of users who installed on each day that were active again on day 1, 3, 7, 14 and 30, optionally per network, campaign, adgroup, creative, country or platform. Unlike GA4, Adjust links users to the network, campaign and creative that acquired them, so this answers 'which source brings players who stay'.")]
    public Task<string> GetRetention(
        [Description(AppHelp)] string? appToken = null,
        [Description("Optional split: all (default), network, campaign, adgroup, creative, country or platform.")] string? groupBy = null,
        [Description("Install dates to include. Preset, default last_30_days.")] string? datePreset = "last_30_days",
        [Description("Cohort start date yyyy-MM-dd.")] string? startDate = null,
        [Description("Cohort end date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("adjust_get_retention", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var data = new List<AdjustRetentionReport>();
            foreach (var app in await service.ResolveAppsAsync(appToken, ct))
                data.Add(await service.GetRetentionAsync(app, range, groupBy, LastCompleteDay, ct));
            r.Data = data;
            r.Notes.Add("Retention % = users of an install cohort active on day N (Adjust retained_users_dN, 24-hour periods after install) ÷ installs of that cohort. Each day uses only cohorts that have fully reached it; null = no cohort has reached it yet.");
            r.Notes.Add("Fewer than ~100 installs per point makes retention noisy.");
            r.Notes.Add("Adjust counts days in 24-hour periods from the install time; GA4 counts calendar days. The two retention figures are close but not identical.");
            foreach (var w in data.SelectMany(d => d.Warnings).Distinct()) r.Notes.Add($"Adjust: {w}");
        });
}
