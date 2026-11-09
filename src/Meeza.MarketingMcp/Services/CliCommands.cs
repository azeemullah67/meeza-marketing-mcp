using System.Text;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Meeza.MarketingMcp.Services;

/// <summary>
/// Setup commands. These run in a terminal (never under the MCP client) and write to stdout.
/// </summary>
public static class CliCommands
{
    public const string Usage = """
        Meeza Marketing MCP - read-only marketing data for Claude

        Usage:
          meeza-marketing-mcp                       Run the MCP server over stdio (what Claude launches)
          meeza-marketing-mcp dashboard             Open the live dashboard in your browser (http://127.0.0.1:5057)
          meeza-marketing-mcp dashboard --demo      Preview the dashboard with generated sample data
                                     --no-browser  Print the link without opening a browser
          meeza-marketing-mcp check                 Show configuration and test each platform connection
          meeza-marketing-mcp secrets list          List known settings and where each value comes from (masked)
          meeza-marketing-mcp secrets set NAME      Store a secret encrypted for the current user (prompts; or pipe it in)
          meeza-marketing-mcp secrets set NAME --from-file PATH
                                                   Store a file's contents (e.g. PLAY_SERVICE_ACCOUNT_JSON)
          meeza-marketing-mcp secrets remove NAME   Delete a stored secret
          meeza-marketing-mcp auth google-ads       Sign in with Google and store a Google Ads refresh token
          meeza-marketing-mcp projects              List game projects and their connected accounts
          meeza-marketing-mcp sync [PROJECT_ID] [--integration google_ads|meta|ga4]
                                                   Synchronize now (all projects when no id is given) and print the result
          meeza-marketing-mcp help                  Show this help

        Settings are read from environment variables first, then the encrypted secret store,
        then appsettings.json (non-secret settings only).
        """;

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && args[0].ToLowerInvariant() is "check" or "dashboard" or "secrets" or "auth" or "sync" or "projects" or "help" or "--help" or "-h" or "/?";

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "check" => await CheckAsync(services),
                "dashboard" => await DashboardAsync(services, args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)), !args.Any(a => a.Equals("--no-browser", StringComparison.OrdinalIgnoreCase))),
                "secrets" => Secrets(args.Skip(1).ToArray(), services),
                "projects" => Projects(services),
                "sync" => await SyncAsync(args.Skip(1).ToArray(), services),
                "auth" when args.Length > 1 && args[1].Equals("google-ads", StringComparison.OrdinalIgnoreCase) => await AuthGoogleAdsAsync(services),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + Redactor.Redact(ex.Message));
            return 1;
        }
    }

    private static int PrintUsage()
    {
        Console.WriteLine(Usage);
        return 0;
    }

    // ---------------------------------------------------------------- dashboard

    private static async Task<int> DashboardAsync(IServiceProvider sp, bool demo, bool openBrowser)
    {
        var url = Meeza.MarketingMcp.Dashboard.DashboardServer.Url;
        if (Meeza.MarketingMcp.Dashboard.DashboardServer.PortInUse())
        {
            if (demo) throw new InvalidOperationException($"Port {Meeza.MarketingMcp.Dashboard.DashboardServer.Port} is in use by the live dashboard. Set MEEZA_DASHBOARD_PORT to another port for the demo.");
            Console.WriteLine("The dashboard is already running (started by Claude or another terminal).");
            Console.WriteLine("Open: " + url);
            if (openBrowser) Meeza.MarketingMcp.Dashboard.DashboardServer.TryOpenBrowser(url);
            return 0;
        }

        sp.GetRequiredService<Meeza.MarketingMcp.Dashboard.DashboardData>().Demo = demo;
        await using var server = sp.GetRequiredService<Meeza.MarketingMcp.Dashboard.DashboardServer>();
        if (!await server.StartAsync(CancellationToken.None))
            throw new InvalidOperationException($"Could not start the dashboard on port {Meeza.MarketingMcp.Dashboard.DashboardServer.Port}. Set MEEZA_DASHBOARD_PORT to a free port.");

        Console.WriteLine(demo ? "Meeza Marketing dashboard is running with DEMO DATA (no accounts are contacted)." : "Meeza Marketing dashboard is running (read-only, this computer only).");
        Console.WriteLine("Open: " + url);
        Console.WriteLine("Press Ctrl+C to stop.");
        if (openBrowser) Meeza.MarketingMcp.Dashboard.DashboardServer.TryOpenBrowser(url);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        // Background synchronization keeps the stored data current while the dashboard is open.
        var scheduler = demo ? null : sp.GetRequiredService<Meeza.MarketingMcp.Sync.SyncScheduler>();
        if (scheduler is not null) await scheduler.StartAsync(stop.Token);
        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        if (scheduler is not null) await scheduler.StopAsync(CancellationToken.None);
        return 0;
    }

    // ---------------------------------------------------------------- projects & sync

    private static int Projects(IServiceProvider sp)
    {
        var store = sp.GetRequiredService<Meeza.MarketingMcp.Storage.ProjectStore>();
        var syncs = sp.GetRequiredService<Meeza.MarketingMcp.Storage.SyncStore>();
        var list = store.List();
        if (list.Count == 0) Console.WriteLine("No projects yet. Create one in the dashboard (meeza-marketing-mcp dashboard).");
        foreach (var p in list)
        {
            Console.WriteLine($"{p.Id}  {p.Name}");
            foreach (var c in p.Connections)
            {
                var s = syncs.State(p.Id, c.Integration, c.ExternalId);
                Console.WriteLine($"    {c.Integration,-12} {c.ExternalId,-22} {c.DisplayName}  last success: {s?.LastSuccessAt ?? "never"}  latest data: {s?.LatestDataDate ?? "-"}  {(s?.LastStatus is "failed" ? "FAILED: " + s.LastError : "")}");
            }
        }
        return 0;
    }

    private static async Task<int> SyncAsync(string[] args, IServiceProvider sp)
    {
        var engine = sp.GetRequiredService<Meeza.MarketingMcp.Sync.SyncEngine>();
        var store = sp.GetRequiredService<Meeza.MarketingMcp.Storage.ProjectStore>();
        string? integration = null;
        var ids = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--integration" && i + 1 < args.Length) integration = args[++i];
            else ids.Add(args[i]);
        }
        if (ids.Count == 0) ids.AddRange(store.List().Select(p => p.Id));
        var failed = false;
        foreach (var id in ids)
        {
            Console.WriteLine($"Syncing {id} ...");
            foreach (var o in await engine.SyncProjectAsync(id, "manual_cli", integration, CancellationToken.None))
            {
                failed |= o.Status == "failed";
                Console.WriteLine($"  {o.Integration,-12} {o.ExternalId,-22} {o.Status,-8} records={o.Records} windows={o.Windows} latest={o.LatestDataDate?.ToString("yyyy-MM-dd") ?? "-"} {o.DurationMs} ms");
                if (o.Error is not null) Console.WriteLine("      error: " + Redactor.Redact(o.Error));
                foreach (var w in o.Warnings) Console.WriteLine("      warning: " + Redactor.Redact(w));
            }
        }
        return failed ? 2 : 0;
    }

    // ---------------------------------------------------------------- secrets

    private static int Secrets(string[] args, IServiceProvider sp)
    {
        var store = sp.GetRequiredService<LocalSecretStore>();
        var resolver = sp.GetRequiredService<ISettingsResolver>();
        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        switch (sub)
        {
            case "list":
                Console.WriteLine($"Secret store: {store.FilePath} {(LocalSecretStore.IsSupported ? "(DPAPI, current user)" : "(not available on this OS)")}");
                Console.WriteLine();
                foreach (var key in SettingKeys.All)
                {
                    var source = resolver.SourceOf(key);
                    var value = resolver.Get(key);
                    var shown = value is null ? "-" : SettingKeys.IsSecret(key) ? Redactor.Mask(value) : value;
                    Console.WriteLine($"  {key,-32} {source ?? "(not set)",-14} {shown}");
                }
                return 0;

            case "set" when args.Length >= 2:
            {
                var key = ValidateKey(args[1]);
                string value;
                var fromFile = Array.FindIndex(args, a => a.Equals("--from-file", StringComparison.OrdinalIgnoreCase));
                if (fromFile >= 0)
                {
                    if (fromFile + 1 >= args.Length) throw new ArgumentException("--from-file requires a path.");
                    value = File.ReadAllText(args[fromFile + 1]);
                }
                else if (Console.IsInputRedirected)
                {
                    value = Console.In.ReadToEnd();
                }
                else
                {
                    Console.Write($"Enter value for {key} (input hidden): ");
                    value = ReadHidden();
                }
                value = value.Trim();
                if (value.Length == 0) throw new ArgumentException("Empty value; nothing stored.");
                if (key is SettingKeys.PlayServiceAccountJson or SettingKeys.Ga4ServiceAccountJson) _ = new GoogleServiceAccountTokenProvider(new HttpClient(), value, PlayConsoleOptions.Scopes, "google_play"); // validates format
                store.Set(key, value);
                Console.WriteLine($"Stored {key} ({(SettingKeys.IsSecret(key) ? Redactor.Mask(value) : value)}) in {store.FilePath}.");
                if (Environment.GetEnvironmentVariable(key) is not null)
                    Console.WriteLine($"Note: environment variable {key} is also set and takes precedence.");
                return 0;
            }

            case "remove" when args.Length >= 2:
            {
                var key = ValidateKey(args[1]);
                Console.WriteLine(store.Remove(key) ? $"Removed {key}." : $"{key} was not stored.");
                return 0;
            }

            default:
                return PrintUsage();
        }
    }

    private static string ValidateKey(string key)
    {
        var k = key.Trim().ToUpperInvariant();
        if (!SettingKeys.All.Contains(k)) throw new ArgumentException($"Unknown setting '{key}'. Known settings: {string.Join(", ", SettingKeys.All)}");
        return k;
    }

    private static string ReadHidden()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // ---------------------------------------------------------------- auth

    private static async Task<int> AuthGoogleAdsAsync(IServiceProvider sp)
    {
        var resolver = sp.GetRequiredService<ISettingsResolver>();
        var clientId = resolver.Get(SettingKeys.GoogleAdsClientId);
        var clientSecret = resolver.Get(SettingKeys.GoogleAdsClientSecret);
        if (clientId is null || clientSecret is null)
            throw new InvalidOperationException("Set GOOGLE_ADS_CLIENT_ID and GOOGLE_ADS_CLIENT_SECRET first (Desktop app OAuth client). See docs/google-ads-setup.md.");

        var refresh = await GoogleOAuthFlow.GetRefreshTokenAsync(clientId, clientSecret, GoogleAdsOptions.Scope, Console.Out, CancellationToken.None);
        var store = sp.GetRequiredService<LocalSecretStore>();
        if (LocalSecretStore.IsSupported)
        {
            store.Set(SettingKeys.GoogleAdsRefreshToken, refresh);
            Console.WriteLine($"Stored GOOGLE_ADS_REFRESH_TOKEN ({Redactor.Mask(refresh)}) in the encrypted secret store.");
        }
        else
        {
            Console.WriteLine("The encrypted secret store is not available on this OS. Set this value as GOOGLE_ADS_REFRESH_TOKEN in your secure environment:");
            Console.WriteLine(refresh);
        }
        return 0;
    }

    // ---------------------------------------------------------------- check

    private static async Task<int> CheckAsync(IServiceProvider sp)
    {
        var registry = sp.GetRequiredService<PlatformRegistry>();
        var failures = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        foreach (var p in registry.AdPlatforms)
        {
            Console.Write($"{p.Platform,-12} ");
            if (!p.IsConfigured) { Console.WriteLine("not configured - " + p.ConfigurationHint); continue; }
            try
            {
                var accounts = await p.GetAccountsAsync(cts.Token);
                Console.WriteLine($"OK - {accounts.Count} account(s)");
                foreach (var a in accounts) Console.WriteLine($"             {a.AccountId}  {a.Name}  [{a.Currency}, {a.TimeZone}{(a.IsManager == true ? ", manager" : "")}]");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAILED - " + ToolRunner.Describe(ex));
            }
        }

        var play = registry.Play;
        Console.Write($"{PlayConsoleOptions.Platform,-12} ");
        if (!play.IsConfigured) Console.WriteLine("not configured - " + PlayConsoleOptions.Hint);
        else
            try
            {
                var apps = await play.GetAppsAsync(cts.Token);
                Console.WriteLine($"OK - service account {play.Client.ServiceAccountEmail}, {apps.Count} app(s)");
                foreach (var a in apps) Console.WriteLine($"             {a.PackageName}  {a.DisplayName}{(a.VisibleToServiceAccount ? "" : "  (NOT visible to service account)")}");
                if (play.Client.Options.ReportsBucket is { } bucket)
                {
                    var objects = await play.Client.ListReportObjectsAsync("stats/installs/", cts.Token);
                    Console.WriteLine($"             reports bucket {bucket}: {objects.Count} install report file(s)");
                }
                else Console.WriteLine("             PLAY_REPORTS_BUCKET not set (store installs and rating exports unavailable)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAILED - " + ToolRunner.Describe(ex));
            }

        var ga4 = sp.GetRequiredService<AnalyticsService>();
        Console.Write($"{AnalyticsOptions.Platform,-12} ");
        if (!ga4.IsConfigured) Console.WriteLine("not configured - " + AnalyticsOptions.Hint);
        else
            try
            {
                var props = await ga4.GetPropertiesAsync(cts.Token);
                Console.WriteLine($"OK - service account {ga4.SafeEmail()}{(ga4.Client.Options.UsesPlayKey ? " (Play key)" : "")}, {props.Count} propert{(props.Count == 1 ? "y" : "ies")}");
                foreach (var p in props) Console.WriteLine($"             {p.PropertyId}  {p.DisplayName}  [{p.AccountName}]");
                if (props.Count == 0) Console.WriteLine("             Add the service account as a Viewer in GA4 → Admin → Property access management.");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAILED - " + ToolRunner.Describe(ex));
            }

        return failures == 0 ? 0 : 2;
    }
}
