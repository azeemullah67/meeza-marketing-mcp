using System.ComponentModel;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Services;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

[McpServerToolType]
public sealed class DashboardTools(DashboardServer server, PlatformRegistry registry)
{
    [McpServerTool(Name = "open_dashboard", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Open the live marketing dashboard (spend, installs and CPI by campaign, ad set and ad; daily trends; countries; Google Play health) in the user's browser and return its private local link.")]
    public Task<string> OpenDashboard(
        [Description("Open the browser automatically. Default true.")] bool openBrowser = true,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("open_dashboard", async r =>
        {
            var running = server.IsRunning || DashboardServer.PortInUse() || await server.StartAsync(ct);
            if (!running) throw new InvalidOperationException($"The dashboard could not start on port {DashboardServer.Port}. Set MEEZA_DASHBOARD_PORT to a free port.");
            var opened = openBrowser && DashboardServer.TryOpenBrowser(DashboardServer.Url);
            r.Data = new { url = DashboardServer.Url, openedInBrowser = opened };
            r.Notes.Add("The link contains a private access key for this computer. Don't share it. The dashboard only listens on 127.0.0.1.");
        });

    [McpServerTool(Name = "get_ad_performance", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Performance by individual ad (or by ad set / ad group) across Meta and Google Ads: spend, impressions, clicks, CTR, CPC, CPM, installs, CPI and delivery status.")]
    public Task<string> GetAdPerformance(
        [Description("ad (default), adset (Meta ad set / Google ad group) or campaign.")] string level = "ad",
        [Description("Optional comma-separated platforms: meta, google_ads.")] string? platforms = null,
        [Description("Sort by: spend (default), installs, cpi, ctr, clicks.")] string sortBy = "spend",
        [Description("Only rows with spend but no reported installs.")] bool onlyWithoutInstalls = false,
        [Description("Maximum rows (default 50, max 1000).")] int top = 50,
        [Description("Date preset: yesterday, last_7_days, last_30_days, last_N_days, this_month, last_month.")] string? datePreset = "last_7_days",
        [Description("Start date yyyy-MM-dd.")] string? startDate = null,
        [Description("End date yyyy-MM-dd, inclusive.")] string? endDate = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_ad_performance", async r =>
        {
            var range = DateRange.Resolve(datePreset, startDate, endDate);
            r.DateRange = range.ToString();
            var rows = (await registry.CollectLevelAsync(platforms, range, ReportLevels.Parse(level), r, ct)).AsEnumerable();
            if (onlyWithoutInstalls) rows = rows.Where(x => x.Spend > 0 && (x.Installs is null or 0));
            rows = sortBy.Trim().ToLowerInvariant() switch
            {
                "installs" => rows.OrderByDescending(x => x.Installs ?? -1),
                "cpi" => rows.OrderBy(x => x.Cpi ?? decimal.MaxValue),
                "ctr" => rows.OrderByDescending(x => x.Ctr ?? -1),
                "clicks" => rows.OrderByDescending(x => x.Clicks ?? -1),
                _ => rows.OrderByDescending(x => x.Spend ?? 0),
            };
            var list = rows.ToList();
            r.Data = new
            {
                totalRows = list.Count,
                rows = list.Take(Math.Clamp(top, 1, 1000)).Select(x => x with { Notes = null }).ToArray(),
                totalsByPlatform = Aggregation.ByPlatform(list),
            };
            r.Notes.AddRange([ReportingNotes.Currency, ReportingNotes.NullMeansUnavailable, ReportingNotes.PlatformAttribution,
                "Installs are null when a platform reports no install tracking for that ad. Ads with no delivery in the period are not listed."]);
        });
}
