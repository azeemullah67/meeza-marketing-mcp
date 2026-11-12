using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.Meta;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp;
using Meeza.MarketingMcp.Configuration;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Services;
using Meeza.MarketingMcp.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

// Regression tests written while re-verifying audit findings F1-F6 at the current head. Everything runs against
// in-memory handlers or loopback listeners with synthetic values; no provider is contacted.

/// <summary>
/// Points the per-user data directory (dashboard key, instance files, default secret store and database paths) at a
/// throwaway folder for the whole test run, so tests can rotate and delete the dashboard key without ever touching the
/// developer's real profile.
/// </summary>
internal static class TestDataDirectory
{
    public static readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"meeza-tests-{Guid.NewGuid():N}");

    [ModuleInitializer]
    internal static void Redirect()
    {
        PrivateStorage.DirectoryOverride = Path;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        };
    }
}

internal static class Loopback
{
    public static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>A local listener that answers every request through <paramref name="respond"/> and records what it was asked.</summary>
    public static (HttpListener Listener, List<HttpListenerRequest> Requests) Listen(int port, Action<HttpListenerContext> respond)
    {
        var requests = new List<HttpListenerRequest>();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                lock (requests) requests.Add(ctx.Request);
                try { respond(ctx); ctx.Response.Close(); } catch { /* the test is over */ }
            }
        });
        return (listener, requests);
    }
}

// ------------------------------------------------------------------ F1: a child ID can never smuggle a path

public class MetaChildIdTests
{
    [Theory]
    [InlineData("act_22222")]
    [InlineData("88888/insights")]
    [InlineData("88888?fields=account_id")]
    [InlineData("../act_22222")]
    [InlineData("88888 ")]
    public async Task Child_ids_that_are_not_plain_numbers_are_refused_before_any_request(string childId)
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"id":"88888","account_id":"11111"}"""));
        var client = new MetaAdsClient(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic", AdAccountIds = ["act_11111"] }, NullLogger<MetaAdsClient>.Instance);

        if (childId.Trim().All(char.IsAsciiDigit))
        {
            // Surrounding whitespace is trimmed; the request then names the numeric ID only.
            await client.GetAdSetsAsync("act_11111", childId, 10, default);
            Assert.All(http.Requests, r => Assert.DoesNotContain("%20", r.RequestUri!.AbsoluteUri));
            return;
        }
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAdSetsAsync("act_11111", childId, 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAdsAsync("act_11111", childId, 10, default));
        Assert.Empty(http.Requests);
    }

    // The same boundary seen from the MCP tool, which is where a campaign ID actually arrives.

    private static (MetaAdsTools Tools, FakeHttp Http) Tools(string[] allowList, Func<string, HttpResponseMessage?> graph)
    {
        var http = new FakeHttp(r => graph(r.RequestUri!.AbsolutePath) ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        var client = new MetaAdsClient(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic", AdAccountIds = allowList }, NullLogger<MetaAdsClient>.Instance);
        return (new MetaAdsTools(client, new MetaAdsPlatform(client)), http);
    }

    private static string Account(string digits) => $$"""{"id":"act_{{digits}}","account_id":"{{digits}}","name":"Account {{digits}}","currency":"USD","timezone_name":"UTC","account_status":1}""";

    [Fact]
    public async Task The_tool_returns_nothing_for_a_campaign_owned_by_an_account_outside_the_allow_list()
    {
        var (tools, http) = Tools(["act_11111"], path =>
            path.EndsWith("/act_11111") ? FakeHttp.Json(Account("11111"))
            : path.EndsWith("/88888") ? FakeHttp.Json("""{"id":"88888","account_id":"22222"}""")
            : path.EndsWith("/88888/adsets") ? FakeHttp.Json("""{"data":[{"id":"1","name":"row from the excluded account","campaign_id":"88888"}]}""")
            : null);

        var result = JsonDocument.Parse(await tools.GetAdSets(campaignId: "88888")).RootElement;

        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("different ad account", result.GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.DoesNotContain("row from the excluded account", result.GetRawText());
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/88888/adsets"));
    }

    [Fact]
    public async Task The_tool_refuses_a_campaign_id_when_several_accounts_are_in_scope_and_never_requests_it()
    {
        var (tools, http) = Tools([], path =>
            path.EndsWith("/me/adaccounts") ? FakeHttp.Json($$"""{"data":[{{Account("11111")}},{{Account("22222")}}]}""") : null);

        var adSets = JsonDocument.Parse(await tools.GetAdSets(campaignId: "88888")).RootElement;
        var ads = JsonDocument.Parse(await tools.GetAds(adSetId: "77777")).RootElement;

        Assert.False(adSets.GetProperty("ok").GetBoolean());
        Assert.False(ads.GetProperty("ok").GetBoolean());
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath.Contains("88888") || r.RequestUri.AbsolutePath.Contains("77777"));
    }
}

// ------------------------------------------------------------------ F3: redirects are never followed with a credential

public class GraphUrlRuleTests
{
    [Theory]
    [InlineData("https://user@graph.facebook.com/v25.0/act_1/campaigns")]          // user info on the genuine host
    [InlineData("https://user:pw@graph.facebook.com/v25.0/act_1/campaigns")]
    [InlineData("https://graph.facebook.com:443@attacker.invalid/v25.0/x")]        // the genuine host as user info
    [InlineData("https://graph.facebook.com./v25.0/x")]                            // not exactly the host
    [InlineData("https://xgraph.facebook.com/v25.0/x")]
    [InlineData("https://graph.facebook.com.attacker.invalid:443/v25.0/x")]
    [InlineData("ftp://graph.facebook.com/v25.0/x")]
    [InlineData("//graph.facebook.com/v25.0/x")]
    [InlineData("graph.facebook.com/v25.0/x")]
    [InlineData("")]
    public void Links_that_are_not_exactly_the_graph_host_over_https_are_rejected(string url) => Assert.False(MetaAdsClient.IsGraphUrl(url));

    [Theory]
    [InlineData("https://graph.facebook.com/v25.0/act_1/insights?after=abc")]
    [InlineData("HTTPS://Graph.Facebook.Com/v25.0/act_1/insights?after=abc")]
    public void Genuine_graph_links_are_accepted(string url)
    {
        Assert.True(MetaAdsClient.IsGraphUrl(url));
        Assert.Equal("graph.facebook.com", new Uri(url).Host); // the same parser decides and sends
    }
}

public class RedirectTests
{
    [Fact]
    public async Task A_redirect_from_the_graph_api_is_an_error_and_is_not_followed()
    {
        var http = new FakeHttp(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
            r.Headers.Location = new Uri("https://attacker.invalid/collect");
            return r;
        });
        var client = new MetaAdsClient(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic" }, NullLogger<MetaAdsClient>.Instance);

        await Assert.ThrowsAsync<PlatformApiException>(() => client.GetCampaignsAsync("act_1", null, 100, default));

        Assert.Single(http.Requests);
        Assert.Equal("graph.facebook.com", http.Requests[0].RequestUri!.Host);
    }

    [Fact]
    public async Task The_http_client_every_provider_uses_does_not_follow_redirects()
    {
        var targetPort = Loopback.FreePort();
        var redirectorPort = Loopback.FreePort();
        var (target, targetRequests) = Loopback.Listen(targetPort, ctx => ctx.Response.StatusCode = 200);
        var (redirector, _) = Loopback.Listen(redirectorPort, ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = $"http://127.0.0.1:{targetPort}/collect";
        });
        try
        {
            using var sp = new ServiceCollection().AddLogging().BuildServiceProvider();
            using var client = ServiceRegistration.CreateHttpClient(sp);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{redirectorPort}/v1/report");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "synthetic-token");

            using var resp = await client.SendAsync(req);

            Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
            lock (targetRequests) Assert.Empty(targetRequests); // the bearer token never travelled to the redirect target
        }
        finally { redirector.Stop(); target.Stop(); }
    }
}

// ------------------------------------------------------------------ F2: the GA4 allow-list limits every request, and saves

public class Ga4PropertyListingTests
{
    [Fact]
    public async Task Listing_properties_never_queries_a_property_outside_the_allow_list()
    {
        using var rsa = RSA.Create(2048); // synthetic key generated for this test only
        var key = JsonSerializer.Serialize(new { type = "service_account", client_email = "reader@example.iam.gserviceaccount.com", private_key = rsa.ExportPkcs8PrivateKeyPem() });
        var http = new FakeHttp(r =>
        {
            var url = r.RequestUri!.AbsoluteUri;
            if (url.StartsWith("https://oauth2.googleapis.com/token", StringComparison.Ordinal)) return FakeHttp.Json("""{"access_token":"ya29.synthetic","expires_in":3600}""");
            if (url.Contains("/accountSummaries", StringComparison.Ordinal))
                return FakeHttp.Json("""
                    {"accountSummaries":[{"displayName":"Studio","propertySummaries":[
                        {"property":"properties/111111","displayName":"Allowed game"},
                        {"property":"properties/222222","displayName":"Excluded game"}]}]}
                    """);
            if (url.Contains("/dataStreams", StringComparison.Ordinal))
                return FakeHttp.Json("""{"dataStreams":[{"displayName":"Android","androidAppStreamData":{"packageName":"com.example.game"}}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        });
        var service = new AnalyticsService(new AnalyticsClient(http.Client, new AnalyticsOptions { ServiceAccountJson = key, PropertyIds = ["111111"] }));

        var properties = await service.GetPropertiesAsync(default);

        var only = Assert.Single(properties);
        Assert.Equal("111111", only.PropertyId);
        Assert.NotNull(only.Apps);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsoluteUri.Contains("222222", StringComparison.Ordinal));
    }
}

/// <summary>A dashboard on a free port with an empty, isolated credential store and optional non-secret settings.</summary>
internal sealed class TestDashboard : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"meeza-audit-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _sp;
    private readonly DashboardServer _server;

    public int Port { get; } = Loopback.FreePort();
    public HttpClient Http { get; }
    public IServiceProvider Services => _sp;
    public string Csrf { get; private set; } = "";

    private TestDashboard(Dictionary<string, string?> settings, bool demo)
    {
        Environment.SetEnvironmentVariable("MEEZA_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", Port.ToString(CultureInfo.InvariantCulture));
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(kv => "Settings:" + kv.Key, kv => kv.Value)).Build());
        services.AddLogging();
        services.AddMeezaMarketing();
        services.AddSingleton(new LocalSecretStore(Path.Combine(Path.GetTempPath(), $"meeza-secrets-{Guid.NewGuid():N}.json")));
        if (demo) services.AddMeezaDemo(); // the same registrations `dashboard --demo` uses
        _sp = services.BuildServiceProvider();
        if (demo) CliCommands.SeedDemo(_sp);
        _server = _sp.GetRequiredService<DashboardServer>();
        Http = NewClient();
    }

    public HttpClient NewClient() =>
        new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };

    public static async Task<TestDashboard> StartAsync(Dictionary<string, string?>? settings = null, bool demo = false)
    {
        var d = new TestDashboard(settings ?? [], demo);
        Assert.True(await d._server.StartAsync(default));
        (await d.Http.GetAsync($"/?key={AccessKey.Value}")).EnsureSuccessStatusCode();
        d.Csrf = (await d.Http.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("csrf").GetString()!;
        return d;
    }

    public Task<HttpResponseMessage> Post(string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("Origin", $"http://127.0.0.1:{Port}");
        req.Headers.Add("X-Meeza-Csrf", Csrf);
        return Http.SendAsync(req);
    }

    public async ValueTask DisposeAsync()
    {
        var used = _sp.GetRequiredService<MarketingDb>().Path; // the demo registrations use their own throwaway file
        await _server.DisposeAsync();
        await _sp.DisposeAsync();
        Http.Dispose();
        Environment.SetEnvironmentVariable("MEEZA_DB_PATH", null);
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", null);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var db in new[] { _dbPath, used }.Distinct())
            foreach (var f in new[] { db, db + "-wal", db + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
    }
}

// ------------------------------------------------------------------ demo mode: invented data, nothing real within reach

[Collection("api")]
public class DemoModeTests
{
    [Fact]
    public async Task Demo_shows_one_invented_project_with_data_on_every_page()
    {
        await using var d = await TestDashboard.StartAsync(demo: true);

        var list = await d.Http.GetFromJsonAsync<JsonElement>("/api/v2/projects");
        Assert.True(list.GetProperty("demo").GetBoolean());
        var project = Assert.Single(list.GetProperty("projects").EnumerateArray()).GetProperty("project");
        Assert.Equal(DemoProject.Name, project.GetProperty("name").GetString());
        var id = project.GetProperty("id").GetString();

        foreach (var view in new[] { "overview", "campaigns", "retention", "creatives", "funnel", "attribution", "forecast", "findings", "strategy", "sync", "tasks", "filters" })
            Assert.Equal(HttpStatusCode.OK, (await d.Http.GetAsync($"/api/v2/projects/{id}/{view}?preset=last_14_days")).StatusCode);
        var campaigns = await d.Http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/campaigns?preset=last_14_days");
        Assert.True(campaigns.GetProperty("totals").GetProperty("spend").GetDecimal() > 0);
        Assert.True(campaigns.GetProperty("rows").GetArrayLength() > 1);
        // The 30-day check has run over the invented data, as it does after a real sync, so Overview and Tasks are filled in.
        var check = await d.Http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/check");
        Assert.Equal("demo", check.GetProperty("trigger").GetString());
        Assert.True((await d.Http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/tasks")).GetProperty("tasks").GetArrayLength() > 0);
        // The page labels the data: the header pill and the Data sources banner are driven by this flag.
        Assert.Contains("Demo data", await d.Http.GetStringAsync("/"));
    }

    [Fact]
    public async Task Demo_runs_on_a_throwaway_database_and_resolves_no_credentials_even_when_some_are_set()
    {
        // A synthetic value in the environment stands in for a developer's real token.
        Environment.SetEnvironmentVariable("META_ACCESS_TOKEN", "EAAsynthetic-not-a-real-token");
        try
        {
            await using var d = await TestDashboard.StartAsync(new() { ["META_AD_ACCOUNT_IDS"] = "act_11111" }, demo: true);

            Assert.NotEqual(MarketingDb.DefaultPath, d.Services.GetRequiredService<MarketingDb>().Path);
            Assert.StartsWith(Path.GetTempPath(), d.Services.GetRequiredService<MarketingDb>().Path);
            Assert.False(d.Services.GetRequiredService<MetaAdsClient>().Options.IsConfigured);
            var platforms = (await d.Http.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("settings").GetProperty("platforms").EnumerateArray().ToArray();
            Assert.All(platforms, p => Assert.False(p.GetProperty("configured").GetBoolean()));
            Assert.All(platforms.SelectMany(p => p.GetProperty("fields").EnumerateArray()), f => Assert.False(f.GetProperty("isSet").GetBoolean()));
            var discovered = (await d.Http.GetFromJsonAsync<JsonElement>("/api/v2/discover")).GetProperty("integrations").EnumerateArray();
            Assert.All(discovered, i => Assert.False(i.GetProperty("configured").GetBoolean()));
        }
        finally { Environment.SetEnvironmentVariable("META_ACCESS_TOKEN", null); }
    }

    [Fact]
    public async Task Demo_refuses_to_save_credentials_or_start_a_sync()
    {
        await using var d = await TestDashboard.StartAsync(demo: true);
        var id = (await d.Http.GetFromJsonAsync<JsonElement>("/api/v2/projects")).GetProperty("projects")[0].GetProperty("project").GetProperty("id").GetString();

        var save = await d.Post("/api/settings/meta", new { values = new Dictionary<string, string> { ["META_ACCESS_TOKEN"] = "EAAsynthetic-not-a-real-token" } });
        Assert.Equal(HttpStatusCode.BadRequest, save.StatusCode);
        Assert.Contains("Demo mode", await save.Content.ReadAsStringAsync());

        var sync = await d.Post($"/api/v2/projects/{id}/sync", new { });
        Assert.Equal(HttpStatusCode.BadRequest, sync.StatusCode);
        Assert.Contains("Demo mode", await sync.Content.ReadAsStringAsync());
        var status = (await d.Http.GetFromJsonAsync<JsonElement>($"/api/v2/projects/{id}/sync")).GetProperty("status").EnumerateArray();
        Assert.DoesNotContain(status, s => s.GetProperty("freshness").GetString() is "error" or "never");
    }

    [Fact]
    public void The_demo_database_is_temporary_and_removed_when_the_dashboard_stops()
    {
        using var sp = new ServiceCollection().AddLogging().AddMeezaMarketing().AddMeezaDemo().BuildServiceProvider();
        CliCommands.SeedDemo(sp);
        var path = sp.GetRequiredService<MarketingDb>().Path;
        Assert.True(File.Exists(path));

        CliCommands.RemoveDemoDatabase(sp);

        Assert.DoesNotContain(new[] { path, path + "-wal", path + "-shm" }, File.Exists);
    }

    [Fact]
    public void Sample_data_can_never_be_written_into_a_live_store()
    {
        using var live = new ServiceCollection().AddSingleton(new DemoMode(false)).BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => CliCommands.SeedDemo(live));
        Assert.Throws<InvalidOperationException>(() => CliCommands.RemoveDemoDatabase(live));
        Assert.True(CliCommands.IsDemo(["dashboard", "--demo"]));
        Assert.True(CliCommands.IsDemo(["dashboard", "--no-browser", "--DEMO"]));
        Assert.False(CliCommands.IsDemo(["dashboard"]));
        Assert.False(CliCommands.IsDemo(["sync", "--demo"]));
    }
}

[Collection("api")]
public class AllowListOnSaveTests
{
    private static readonly Dictionary<string, string?> AllowLists = new()
    {
        ["META_AD_ACCOUNT_IDS"] = "act_11111",
        ["GOOGLE_ADS_CUSTOMER_IDS"] = "1111111111",
        ["GA4_PROPERTY_IDS"] = "111111",
        ["ADJUST_APP_TOKENS"] = "abc123def456",
        ["PLAY_PACKAGE_NAMES"] = "com.example.allowed",
    };

    [Theory]
    [InlineData("meta", "act_22222", "META_AD_ACCOUNT_IDS")]
    [InlineData("google_ads", "2222222222", "GOOGLE_ADS_CUSTOMER_IDS")]
    [InlineData("ga4", "222222", "GA4_PROPERTY_IDS")]
    [InlineData("adjust", "zzz999yyy888", "ADJUST_APP_TOKENS")]
    [InlineData("google_play", "com.example.excluded", "PLAY_PACKAGE_NAMES")]
    public async Task A_project_cannot_link_an_account_outside_the_allow_list(string integration, string excludedId, string setting)
    {
        await using var d = await TestDashboard.StartAsync(AllowLists);

        // On create: refused with a clear reason, and nothing is stored.
        var create = await d.Post("/api/v2/projects", new { name = "Excluded", syncNow = false, connections = new[] { new { integration, externalId = excludedId } } });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains(setting, await create.Content.ReadAsStringAsync());
        Assert.Equal(0, (await d.Http.GetFromJsonAsync<JsonElement>("/api/v2/projects")).GetProperty("projects").GetArrayLength());

        // On an existing project: both ways of changing connections refuse, and the project keeps none.
        var created = await d.Post("/api/v2/projects", new { name = "Empty", syncNow = false });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var set = await d.Post($"/api/v2/projects/{id}/connections", new[] { new { integration, externalId = excludedId } });
        Assert.Equal(HttpStatusCode.Forbidden, set.StatusCode);
        var update = await d.Post($"/api/v2/projects/{id}", new { name = "Empty", connections = new[] { new { integration, externalId = excludedId } } });
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Empty(d.Services.GetRequiredService<ProjectStore>().Require(id).Connections);
    }

    [Fact]
    public async Task An_account_inside_the_allow_list_can_be_linked()
    {
        await using var d = await TestDashboard.StartAsync(AllowLists);
        var ok = await d.Post("/api/v2/projects", new { name = "Allowed", syncNow = false, connections = new[] { new { integration = "ga4", externalId = "properties/111111" } } });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var conn = (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connections").EnumerateArray().Single();
        Assert.Equal("111111", conn.GetProperty("externalId").GetString());
    }

    [Fact]
    public async Task A_platform_without_credentials_answers_with_a_clear_error_not_a_server_fault()
    {
        await using var d = await TestDashboard.StartAsync();
        var resp = await d.Http.GetAsync("/api/v2/ga4-events?property=111111");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("not configured", await resp.Content.ReadAsStringAsync());
    }
}

// ------------------------------------------------------------------ F4 + F5: the key only goes to a proven dashboard, and revocation is real

[Collection("api")]
public class DashboardKeyRevocationTests
{
    private static async Task<string> RunCli(params string[] args)
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            using var sp = new ServiceCollection().BuildServiceProvider();
            Assert.Equal(0, await CliCommands.RunAsync(args, sp));
        }
        finally { Console.SetOut(original); }
        return captured.ToString();
    }

    [Fact]
    public async Task Rotating_the_key_invalidates_every_old_cookie_and_link_at_once()
    {
        await using var d = await TestDashboard.StartAsync();
        var oldKey = AccessKey.Value;
        Assert.Equal(HttpStatusCode.OK, (await d.Http.GetAsync("/api/v2/projects")).StatusCode);

        var output = await RunCli("dashboard", "rotate-key");

        var newKey = AccessKey.Value;
        Assert.NotEqual(oldKey, newKey);
        Assert.Contains($"key={newKey}", output); // the running dashboard proved its identity, so the new link is shown
        Assert.Equal(HttpStatusCode.Unauthorized, (await d.Http.GetAsync("/api/v2/projects")).StatusCode);   // old cookie
        using var fresh = d.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync($"/?key={oldKey}")).StatusCode);     // old link
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync($"/?key={newKey}")).StatusCode);               // new link
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/v2/projects")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_key_file_revokes_a_running_dashboard()
    {
        await using var d = await TestDashboard.StartAsync();
        var oldKey = AccessKey.Value;
        Assert.Equal(HttpStatusCode.OK, (await d.Http.GetAsync("/api/v2/projects")).StatusCode);

        File.Delete(AccessKey.FilePath);

        Assert.Equal(HttpStatusCode.Unauthorized, (await d.Http.GetAsync("/api/v2/projects")).StatusCode);
        Assert.False(AccessKey.Matches(oldKey));
        Assert.True(File.Exists(AccessKey.FilePath)); // a new key was issued; only a new link works
    }

    [Fact]
    public void A_key_file_that_cannot_be_read_never_keeps_the_previous_key_alive()
    {
        var oldKey = AccessKey.Value;
        var replacement = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

        // Another process replaced the key (a rotation) and the new file cannot be read yet.
        File.WriteAllText(AccessKey.FilePath, replacement);
        File.SetLastWriteTimeUtc(AccessKey.FilePath, DateTime.UtcNow.AddSeconds(5));
        using (new FileStream(AccessKey.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(AccessKey.Matches(oldKey));
        }

        // Still unreadable history, then the file disappears: the old key must stay dead.
        File.Delete(AccessKey.FilePath);
        Assert.False(AccessKey.Matches(oldKey));
        Assert.False(AccessKey.Matches(replacement));
    }

    [Fact]
    public void A_key_file_left_readable_by_others_is_tightened_when_it_is_used()
    {
        // As an older version, or a restore from a backup, would leave it: present, valid, but not user-only.
        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _ = AccessKey.Value;
        File.Delete(AccessKey.FilePath);
        File.WriteAllText(AccessKey.FilePath, key);
        FilePermissions.MakeWorldReadable(AccessKey.FilePath);
        File.SetLastWriteTimeUtc(AccessKey.FilePath, DateTime.UtcNow.AddSeconds(9));

        Assert.Equal(key, AccessKey.Value);
        FilePermissions.AssertPrivateFile(AccessKey.FilePath);
        Assert.True(AccessKey.Matches(key)); // tightening the file does not invalidate the key it holds
    }

    [Fact]
    public async Task Rotate_key_does_not_print_a_link_for_a_listener_that_is_not_our_dashboard()
    {
        var port = Loopback.FreePort();
        var (squatter, _) = Loopback.Listen(port, ctx =>
        {
            var body = Encoding.UTF8.GetBytes("""{"port":1,"proof":"not-the-right-answer"}""");
            ctx.Response.ContentType = "application/json";
            ctx.Response.OutputStream.Write(body);
        });
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", port.ToString(CultureInfo.InvariantCulture));
        try
        {
            var output = await RunCli("dashboard", "rotate-key");
            Assert.DoesNotContain("key=", output);
            Assert.DoesNotContain(AccessKey.Value, output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", null);
            squatter.Stop();
        }
    }

    [Fact]
    public async Task Rotate_key_does_not_print_a_link_when_no_dashboard_is_running()
    {
        Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", Loopback.FreePort().ToString(CultureInfo.InvariantCulture));
        try
        {
            var output = await RunCli("dashboard", "rotate-key");
            Assert.DoesNotContain("key=", output);
            Assert.Contains("meeza-marketing-mcp dashboard", output);
        }
        finally { Environment.SetEnvironmentVariable("MEEZA_DASHBOARD_PORT", null); }
    }
}

// ------------------------------------------------------------------ F6: the data directory and its files are private

public class PrivateStorageTests
{
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"meeza-private-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void An_existing_permissive_directory_is_restricted_to_the_current_user()
    {
        var dir = TempDir();
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            PrivateStorage.EnsurePrivateDirectory(dir);

            if (OperatingSystem.IsWindows()) FilePermissions.AssertOnlyUserAndSystem(new DirectoryInfo(dir).GetAccessControl());
            else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_existing_world_readable_file_is_restricted_to_the_current_user()
    {
        var dir = TempDir();
        var file = Path.Combine(dir, "dashboard.key");
        try
        {
            File.WriteAllText(file, "synthetic");
            FilePermissions.MakeWorldReadable(file);

            PrivateStorage.MakeFilePrivate(file);

            FilePermissions.AssertPrivateFile(file);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_database_that_already_existed_world_readable_becomes_private_with_its_side_files()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "marketing.db");
        try
        {
            File.WriteAllBytes(path, []); // an older version left a permissive file behind
            FilePermissions.MakeWorldReadable(path);

            var db = new MarketingDb(path);
            using (var c = db.Open()) c.Exec("CREATE TABLE IF NOT EXISTS probe(x INTEGER); INSERT INTO probe VALUES(1);");

            FilePermissions.AssertPrivateFile(path);
            if (!OperatingSystem.IsWindows())
            {
                // SQLite gives the WAL and SHM files the main file's mode; whichever exist must be user-only too.
                foreach (var side in new[] { path + "-wal", path + "-shm" }.Where(File.Exists)) FilePermissions.AssertPrivateFile(side);
                Assert.True(File.Exists(path + "-wal"));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_dashboard_key_and_instance_record_live_in_the_private_data_directory()
    {
        Assert.StartsWith(PrivateStorage.DefaultDirectory, AccessKey.FilePath);
        Assert.StartsWith(PrivateStorage.DefaultDirectory, InstanceIdentity.FilePathFor(5057));
        Assert.StartsWith(PrivateStorage.DefaultDirectory, LocalSecretStore.DefaultPath);
    }
}

/// <summary>Permission checks for both families: Unix modes, and on Windows a protected ACL naming only the user and SYSTEM.</summary>
internal static class FilePermissions
{
    /// <summary>On Unix, mode 0644. On Windows a new file already inherits the folder's wider, unprotected ACL.</summary>
    public static void MakeWorldReadable(string file)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
    }

    public static void AssertPrivateFile(string file)
    {
        if (OperatingSystem.IsWindows()) AssertOnlyUserAndSystem(new FileInfo(file).GetAccessControl());
        else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [SupportedOSPlatform("windows")]
    public static void AssertOnlyUserAndSystem(FileSystemSecurity security)
    {
        Assert.True(security.AreAccessRulesProtected); // nothing is inherited from the parent folder
        var allowed = new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) };
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, r => Assert.Contains((SecurityIdentifier)r.IdentityReference, allowed));
    }
}

// ------------------------------------------------------------------ version: one source

public class VersionTests
{
    [Fact]
    public void The_server_reports_the_version_set_in_Directory_Build_props()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var props = File.ReadAllText(Path.Combine(dir.FullName, "Directory.Build.props"));
        var declared = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;

        Assert.Matches(@"^\d+\.\d+\.\d+", declared);
        Assert.Equal(declared, AppVersion.Current);
    }
}
