using System.ComponentModel;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

/// <summary>
/// MCP access to the synchronized project store: the same computations as the dashboard, so Claude can act as the
/// project's analyst with evidence. Analysis tools are read-only; sync_project_data only refreshes the local copy.
/// </summary>
[McpServerToolType]
public sealed class ProjectTools(ProjectStore projects, AnalyticsEngine engine, SyncEngine sync)
{
    private const string ProjectHelp = "Project id from list_projects.";
    private const string DateHelp = "Preset: last_7_days, last_14_days (default), last_30_days, last_N_days, this_month, last_month. Ignored when startDate/endDate are given.";

    private Filters F(string projectId, string? preset, string? startDate, string? endDate, string? country = null, string? platform = null, string? campaign = null, string? channel = null)
    {
        var p = projects.Require(projectId);
        var range = DateRange.Resolve(preset ?? "last_14_days", startDate, endDate, AnalyticsEngine.Today(p));
        return new Filters(range.Start, range.End, country?.ToUpperInvariant(), platform, campaign, channel);
    }

    [McpServerTool(Name = "list_projects", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List game projects with their connected ad accounts / GA4 properties and the freshness of each source's synchronized data.")]
    public Task<string> ListProjects() => ToolRunner.RunAsync("list_projects", r =>
    {
        r.Data = projects.List().Select(p => new { project = p, status = engine.Status(p) });
        return Task.CompletedTask;
    });

    [McpServerTool(Name = "get_project_overview", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Executive KPIs from the project's synchronized data vs the previous period: spend, installs, CPI, CPM, CPC, CTR, IPM, reach, frequency, DAU/WAU/MAU, D1-D30 retention, revenue, ARPU, ARPDAU, LTV, ROAS, organic vs paid. Each KPI has target, achievement, source, last update, freshness and definition.")]
    public Task<string> Overview([Description(ProjectHelp)] string projectId, [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null,
        [Description("ISO country code")] string? country = null, [Description("google_ads or meta")] string? platform = null, [Description("Campaign id")] string? campaign = null, [Description("GA4 channel group")] string? channel = null) =>
        Run("get_project_overview", () => engine.Overview(projectId, F(projectId, datePreset, startDate, endDate, country, platform, campaign, channel)));

    [McpServerTool(Name = "get_project_findings", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Automatic analyst findings: problems with severity, evidence, comparison period, hypothesis, confidence, recommended action, expected impact and validation steps.")]
    public Task<string> Findings([Description(ProjectHelp)] string projectId, [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null) =>
        Run("get_project_findings", () => engine.Findings(projectId, F(projectId, datePreset, startDate, endDate)));

    [McpServerTool(Name = "get_project_retention", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Exact-day D0-D30 cohort retention: heatmap with maturity per cell, blended mature curve, previous-period curve, per-segment curves, churn trend, new vs returning users. Segments (GA4): all, country, platform, channel, campaign, ad_group. Segments (Adjust install cohorts, when an Adjust app is linked): adjust_all, adjust_network, adjust_campaign, adjust_country, adjust_platform. A project with Adjust but no GA4 uses Adjust for all/country/platform/campaign.")]
    public Task<string> Retention([Description(ProjectHelp)] string projectId, string segmentType = "all", string? segmentValue = null,
        [Description(DateHelp)] string? datePreset = "last_30_days", string? startDate = null, string? endDate = null) =>
        Run("get_project_retention", () => engine.Retention(projectId, F(projectId, datePreset, startDate, endDate), segmentType, segmentValue, "exact"));

    [McpServerTool(Name = "get_project_campaigns", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Campaign / ad set / ad performance with period-over-period change, GA4 retention by campaign, and flags (high CPI, wasted spend, low CTR, rising CPM, declining conversion, poor quality, scale candidates), plus country opportunities.")]
    public Task<string> Campaigns([Description(ProjectHelp)] string projectId, [Description("campaign, adset or ad")] string level = "campaign",
        [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null, string? platform = null) =>
        Run("get_project_campaigns", () => engine.Campaigns(projectId, F(projectId, datePreset, startDate, endDate, platform: platform), level));

    [McpServerTool(Name = "get_project_creatives", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Creative performance with composite score (CPI, IPM, CTR - never CTR alone), classification (best, underperforming, fatigued, testing), fatigue evidence, format/hook/theme comparisons and data-based suggestions.")]
    public Task<string> Creatives([Description(ProjectHelp)] string projectId, [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null) =>
        Run("get_project_creatives", () => engine.Creatives(projectId, F(projectId, datePreset, startDate, endDate)));

    [McpServerTool(Name = "get_project_funnel", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Acquisition funnel from impressions to purchases/ad viewers, with step conversion vs previous period and a diagnosis of whether the drop is an ad, store, onboarding, retention or monetization problem.")]
    public Task<string> Funnel([Description(ProjectHelp)] string projectId, [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null) =>
        Run("get_project_funnel", () => engine.Funnel(projectId, F(projectId, datePreset, startDate, endDate)));

    [McpServerTool(Name = "get_project_attribution", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Attribution reconciliation: Google Ads installs, Meta installs, GA4 first opens and GA4 new users by source, per day, with definitions, attribution windows and time zones.")]
    public Task<string> Attribution([Description(ProjectHelp)] string projectId, [Description(DateHelp)] string? datePreset = null, string? startDate = null, string? endDate = null) =>
        Run("get_project_attribution", () => engine.Attribution(projectId, F(projectId, datePreset, startDate, endDate)));

    [McpServerTool(Name = "get_project_forecast", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("CPI forecast, spend→installs model with diminishing returns, budget scenarios, LTV estimates and reallocation proposals (proposals only - nothing is changed in the ad accounts).")]
    public Task<string> Forecast([Description(ProjectHelp)] string projectId, [Description("Optional daily budget for the scenarios")] decimal? dailyBudget = null) =>
        Run("get_project_forecast", () => engine.Forecast(projectId, F(projectId, null, null, null), dailyBudget));

    [McpServerTool(Name = "sync_project_data", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Refresh the project's local copy of its ad and analytics data now (reads from the platforms; never changes any ad account). Returns per-source results.")]
    public Task<string> SyncNow([Description(ProjectHelp)] string projectId, [Description("Optional: google_ads, meta or ga4")] string? integration = null, CancellationToken ct = default) =>
        ToolRunner.RunAsync("sync_project_data", async r =>
        {
            r.Data = await sync.SyncProjectAsync(projectId, "mcp", integration, ct);
        });

    private static Task<string> Run(string tool, Func<object> compute) => ToolRunner.RunAsync(tool, r =>
    {
        r.Data = compute();
        r.Notes.Add("Computed from the project's synchronized local data. Check each source's freshness before drawing conclusions; null means unavailable, not zero.");
        return Task.CompletedTask;
    });
}
