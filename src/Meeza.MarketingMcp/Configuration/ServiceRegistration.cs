using System.Net;
using System.Net.Http.Headers;
using Meeza.Marketing.Adjust;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.Meta;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meeza.MarketingMcp.Configuration;

public static class ServiceRegistration
{
    public static string UserAgent => $"meeza-marketing-mcp/{AppVersion.Current}";

    /// <summary>Loads appsettings.json (non-secret settings only) from the app folder and the working directory.</summary>
    public static IConfigurationBuilder AddMeezaConfiguration(this IConfigurationBuilder builder)
    {
        builder.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
        var custom = Environment.GetEnvironmentVariable("MEEZA_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(custom)) builder.AddJsonFile(Path.GetFullPath(custom), optional: false, reloadOnChange: false);
        return builder;
    }

    public static IServiceCollection AddMeezaMarketing(this IServiceCollection services)
    {
        services.AddSingleton<LocalSecretStore>();
        services.AddSingleton<ISettingsResolver>(sp => new SettingsResolver(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<LocalSecretStore>()));

        services.AddSingleton(sp => MetaAdsOptions.FromSettings(sp.GetRequiredService<ISettingsResolver>()));
        services.AddSingleton(sp => GoogleAdsOptions.FromSettings(sp.GetRequiredService<ISettingsResolver>()));
        services.AddSingleton(sp => PlayConsoleOptions.FromSettings(sp.GetRequiredService<ISettingsResolver>()));
        services.AddSingleton(sp => AnalyticsOptions.FromSettings(sp.GetRequiredService<ISettingsResolver>()));
        services.AddSingleton(sp => AdjustOptions.FromSettings(sp.GetRequiredService<ISettingsResolver>()));

        // One HttpClient per platform keeps credentials and connection pools isolated between platforms.
        services.AddSingleton(sp => new MetaAdsClient(CreateHttpClient(sp), sp.GetRequiredService<MetaAdsOptions>(), sp.GetRequiredService<ILogger<MetaAdsClient>>()));
        services.AddSingleton(sp => new GoogleAdsClient(CreateHttpClient(sp), sp.GetRequiredService<GoogleAdsOptions>()));
        services.AddSingleton(sp => new PlayConsoleClient(CreateHttpClient(sp), sp.GetRequiredService<PlayConsoleOptions>()));
        services.AddSingleton(sp => new AnalyticsClient(CreateHttpClient(sp), sp.GetRequiredService<AnalyticsOptions>()));
        services.AddSingleton(sp => new AdjustClient(CreateHttpClient(sp), sp.GetRequiredService<AdjustOptions>()));

        services.AddSingleton<MetaAdsPlatform>();
        services.AddSingleton<GoogleAdsPlatform>();
        services.AddSingleton<PlayConsoleService>();
        services.AddSingleton<AnalyticsService>();
        services.AddSingleton<AdjustService>();
        services.AddSingleton<PlatformRegistry>();
        services.AddSingleton<Dashboard.DashboardData>();
        services.AddSingleton<Dashboard.DashboardServer>();
        services.AddSingleton<ConnectionSettings>();

        // Persistent store, projects and synchronization.
        services.AddSingleton(_ => new Storage.MarketingDb(Storage.MarketingDb.DefaultPath));
        services.AddSingleton<Storage.ProjectStore>();
        services.AddSingleton<Storage.FactStore>();
        services.AddSingleton<Storage.SyncStore>();
        services.AddSingleton<Storage.TaskStore>();
        services.AddSingleton<Sync.IIntegrationSyncer, Sync.GoogleAdsSyncer>();
        services.AddSingleton<Sync.IIntegrationSyncer, Sync.MetaSyncer>();
        services.AddSingleton<Sync.IIntegrationSyncer, Sync.Ga4Syncer>();
        services.AddSingleton<Sync.IIntegrationSyncer, Sync.AdjustSyncer>();
        // Every project sync ends with the 30-day problem check, whichever host started it (dashboard, scheduler,
        // CLI, MCP tool). The check is resolved when it runs, not here: it needs the analytics engine, which itself
        // depends on the sync engine.
        services.AddSingleton(sp => new Sync.SyncEngine(
            sp.GetRequiredService<Storage.ProjectStore>(), sp.GetRequiredService<Storage.SyncStore>(),
            sp.GetServices<Sync.IIntegrationSyncer>(), sp.GetRequiredService<ILogger<Sync.SyncEngine>>(),
            (projectId, trigger) => sp.GetRequiredService<Intelligence.ProblemCheck>().Run(projectId, trigger, afterSync: true)));
        services.AddSingleton<Sync.SyncScheduler>();
        services.AddSingleton<Intelligence.ProjectData>();
        services.AddSingleton<Intelligence.AnalyticsEngine>();
        services.AddSingleton<Intelligence.ProblemCheck>();
        services.AddSingleton(Ai.LocalClaudeLocator.Default);
        services.AddSingleton<Ai.ClaudeGateway>();
        services.AddSingleton<Ai.AnalystService>();
        services.AddSingleton<Ai.ResearchService>();
        services.AddSingleton(new Dashboard.DemoMode(false));
        return services;
    }

    /// <summary>
    /// Turns the registrations above into demo mode (<c>dashboard --demo</c>): a throwaway database for the invented
    /// project, an empty throwaway secret store, a settings resolver that returns nothing, and no local Claude app. With no setting resolved,
    /// no platform is configured, so a demo can neither show the user's real data nor send a request to a provider.
    /// </summary>
    public static IServiceCollection AddMeezaDemo(this IServiceCollection services)
    {
        var id = Guid.NewGuid().ToString("N");
        var db = Path.Combine(Path.GetTempPath(), $"meeza-demo-{id}.db");
        services.AddSingleton(new Dashboard.DemoMode(true));
        services.AddSingleton<ISettingsResolver>(new Dashboard.NoSettings());
        services.AddSingleton(Ai.LocalClaudeLocator.None); // nor the Claude app signed in on this computer
        services.AddSingleton(new LocalSecretStore(Path.Combine(Path.GetTempPath(), $"meeza-demo-{id}.secrets.json")));
        services.AddSingleton(_ => new Storage.MarketingDb(db));
        return services;
    }

    /// <summary>The HTTP client every provider uses: retries, no automatic redirects. Internal so tests can check that policy.</summary>
    internal static HttpClient CreateHttpClient(IServiceProvider sp)
    {
        var retry = new RetryHandler(sp.GetRequiredService<ILogger<RetryHandler>>())
        {
            InnerHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
                // Credentials are attached per request after the destination is validated; a redirect would send them
                // wherever the server says. The APIs we call never redirect in normal operation.
                AllowAutoRedirect = false,
            },
        };
        var client = new HttpClient(retry) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("meeza-marketing-mcp", AppVersion.Current));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
