using Meeza.Marketing.Adjust;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.Meta;
using Meeza.Marketing.PlayConsole;
using Meeza.MarketingMcp.Configuration;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Services;
using Meeza.MarketingMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

const string ServerInstructions = """
    Meeza Marketing MCP gives read-only access to the user's own Meta Ads, Google Ads, Google Play Console, Google Analytics 4 / Firebase and Adjust data.
    Start with list_projects: each game project has synchronized, stored data and the get_project_* tools analyse it
    (overview, findings, retention, campaigns, creatives, funnel, attribution, forecast). Prefer them for analysis;
    use the platform tools (meta_*, google_ads_*, ga4_*, play_*, adjust_*) for live lookups. Adjust (an MMP) gives
    deduplicated installs and retention per network / campaign / creative, which GA4 cannot attribute for Meta. get_connected_accounts shows credentials.
    When the user wants to see or browse their data visually, call open_dashboard: it opens a live local dashboard.
    For "can this game work?" use get_game_scorecard (retention + CPI + estimated LTV). For "analytics shows fewer users
    than expected" use check_analytics_tracking.
    When analysing results:
    - Distinguish observed data (tool output) from estimates and recommendations, and label each.
    - Never add amounts in different currencies; amounts are in each account's original currency.
    - null means unavailable, not zero.
    - Ad-platform installs are self-attributed and can overlap between Meta and Google Ads; Play Console installs are
      store-reported (organic + paid); Adjust installs are deduplicated by Adjust. Never sum installs from different sources.
    - Adjust cost is the same money as Meta / Google Ads spend, seen through Adjust. Never add it to ad-platform spend.
    - Read the notes and errors arrays in every response and surface important caveats to the user.
    """;

// Setup commands (check, secrets, auth) run in a terminal and print to stdout.
if (CliCommands.IsCommand(args))
{
    var cliBuilder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
    cliBuilder.Configuration.AddMeezaConfiguration();
    cliBuilder.Logging.ClearProviders();
    cliBuilder.Services.AddMeezaMarketing();
    if (CliCommands.IsDemo(args)) cliBuilder.Services.AddMeezaDemo(); // invented data only: no credentials, no real projects
    using var cliHost = cliBuilder.Build();
    return await CliCommands.RunAsync(args, cliHost.Services);
}

// MCP server over stdio. stdout carries the protocol, so every log line goes to stderr.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Configuration.AddMeezaConfiguration();
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("System.Net.Http", LogLevel.Warning); // never log request URLs

builder.Services.AddMeezaMarketing();
builder.Services.AddHostedService<DashboardHostedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Meeza.MarketingMcp.Sync.SyncScheduler>());
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "meeza-marketing", Version = Meeza.MarketingMcp.AppVersion.Current };
        o.ServerInstructions = ServerInstructions;
    })
    .WithStdioServerTransport()
    .WithTools<UnifiedReportingTools>()
    .WithTools<DashboardTools>()
    .WithTools<BenchmarkTools>()
    .WithTools<InsightTools>()
    .WithTools<GrowthTools>()
    .WithTools<AnalyticsTools>()
    .WithTools<AdjustTools>()
    .WithTools<MetaAdsTools>()
    .WithTools<GoogleAdsTools>()
    .WithTools<PlayConsoleTools>()
    .WithTools<ProjectTools>();

await builder.Build().RunAsync();
return 0;
