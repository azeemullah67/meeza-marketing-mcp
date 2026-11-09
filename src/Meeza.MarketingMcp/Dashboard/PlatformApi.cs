using System.Text.Json;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.Meta;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Ai;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// Project-scoped API for the marketing intelligence app (/api/v2). Every analysis is computed from the local store,
/// so it answers instantly and keeps working while a sync runs. Writes are limited to local configuration
/// (projects, connections, targets) and starting syncs / AI requests; nothing here can change an ad account.
/// </summary>
public static class PlatformApi
{
    private static readonly JsonSerializerOptions JsonIn = new() { PropertyNameCaseInsensitive = true, MaxDepth = 16 };

    public static void Map(WebApplication app, IServiceProvider sp)
    {
        var projects = sp.GetRequiredService<ProjectStore>();
        var engine = sp.GetRequiredService<AnalyticsEngine>();
        var sync = sp.GetRequiredService<SyncEngine>();
        var syncs = sp.GetRequiredService<SyncStore>();
        var analyst = sp.GetRequiredService<AnalystService>();
        var research = sp.GetRequiredService<ResearchService>();

        var g = "/api/v2";

        app.MapGet(g + "/projects", () => Ok(new { projects = projects.List(includeArchived: true).Select(p => Summary(p, engine)), aiConfigured = analyst.IsConfigured }));
        app.MapPost(g + "/projects", (HttpRequest req) => Handle(async () =>
        {
            var body = await Body<ProjectInput>(req);
            var created = projects.Create(body.ToProject());
            if (body.SyncNow != false && created.Connections.Count > 0) _ = sync.StartProjectSync(created.Id, "project_created");
            return created;
        }));
        app.MapGet(g + "/projects/{id}", (string id) => Handle(() => Task.FromResult<object>(new { project = projects.Require(id), status = engine.Status(projects.Require(id)) })));
        app.MapPost(g + "/projects/{id}", (string id, HttpRequest req) => Handle(async () =>
        {
            var body = await Body<ProjectInput>(req);
            var updated = projects.Update(id, body.ToProject());
            if (body.Connections is not null) updated = projects.SetConnections(id, body.Connections.Select(c => c.ToConnection()).ToArray());
            return updated;
        }));
        app.MapPost(g + "/projects/{id}/delete", (string id) => Handle(() => Task.FromResult<object>(new { deleted = projects.Delete(id) })));
        app.MapPost(g + "/projects/{id}/connections", (string id, HttpRequest req) => Handle(async () =>
        {
            var body = await Body<List<ConnectionInput>>(req);
            var p = projects.SetConnections(id, body.Select(c => c.ToConnection()).ToArray());
            _ = sync.StartProjectSync(id, "connections_changed");
            return p;
        }));

        app.MapGet(g + "/discover", (CancellationToken ct) => Handle(() => DiscoverAsync(sp, ct)));

        // ---- analytics views (GET, computed from the store)
        app.MapGet(g + "/projects/{id}/filters", (string id) => Handle(() => Task.FromResult(engine.Data.FilterOptions(projects.Require(id).Id))));
        app.MapGet(g + "/projects/{id}/overview", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Overview(p.Id, f)));
        app.MapGet(g + "/projects/{id}/campaigns", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Campaigns(p.Id, f, r.Query["level"].ToString() is { Length: > 0 } l ? l : "campaign")));
        app.MapGet(g + "/projects/{id}/retention", (string id, HttpRequest r) => View(projects, id, r, (p, f) =>
            engine.Retention(p.Id, f, r.Query["segment"].ToString() is { Length: > 0 } s ? s : "all", r.Query["value"].ToString() is { Length: > 0 } v ? v : null,
                r.Query["mode"] == "rolling" ? "rolling" : "exact")));
        app.MapGet(g + "/projects/{id}/creatives", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Creatives(p.Id, f)));
        app.MapGet(g + "/projects/{id}/funnel", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Funnel(p.Id, f)));
        app.MapGet(g + "/projects/{id}/attribution", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Attribution(p.Id, f)));
        app.MapGet(g + "/projects/{id}/forecast", (string id, HttpRequest r) => View(projects, id, r, (p, f) =>
            engine.Forecast(p.Id, f, decimal.TryParse(r.Query["budget"], System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? b : null)));
        app.MapGet(g + "/projects/{id}/findings", (string id, HttpRequest r) => View(projects, id, r, (p, f) => new { range = f.Range, findings = engine.Findings(p.Id, f) }));
        app.MapGet(g + "/projects/{id}/strategy", (string id, HttpRequest r) => View(projects, id, r, (p, f) => engine.Strategy(p.Id, f)));

        // ---- sync
        app.MapGet(g + "/projects/{id}/sync", (string id) => Handle(() =>
        {
            var p = projects.Require(id);
            return Task.FromResult<object>(new { running = sync.IsRunning(id), status = engine.Status(p), runs = syncs.Runs(id, 60), schedulerEnabled = SyncScheduler.Enabled, intervalMinutes = p.SyncIntervalMinutes });
        }));
        app.MapPost(g + "/projects/{id}/sync", (string id, HttpRequest req) => Handle(async () =>
        {
            projects.Require(id);
            var body = await Body<SyncRequest>(req);
            if (body.Integration is not null && !Integrations.Synced.Contains(body.Integration)) throw new ArgumentException("Unknown integration.");
            var already = sync.IsRunning(id);
            _ = sync.StartProjectSync(id, "manual", body.Integration);
            return new { started = !already, running = true, message = already ? "A sync for this project is already running." : "Sync started. The dashboard stays usable; data updates as each source finishes." };
        }));

        // ---- KPI targets & benchmarks
        app.MapGet(g + "/projects/{id}/targets", (string id) => Handle(() => Task.FromResult<object>(new
        {
            targets = projects.Targets(projects.Require(id).Id),
            metrics = ProjectStore.TargetMetrics.Select(kv => new { key = kv.Key, label = kv.Value.Label, higherIsBetter = kv.Value.HigherIsBetter, unit = kv.Value.Unit }),
        })));
        app.MapPost(g + "/projects/{id}/targets", (string id, HttpRequest req) => Handle(async () =>
        {
            var t = await Body<TargetInput>(req);
            return projects.UpsertTarget(id, t.Metric ?? "", t.ScopeType, t.ScopeValue, t.Target ?? throw new ArgumentException("Target value is required."), t.Notes);
        }));
        app.MapPost(g + "/projects/{id}/targets/{targetId:long}/delete", (string id, long targetId) => Handle(() => Task.FromResult<object>(new { deleted = projects.DeleteTarget(id, targetId) })));

        // ---- AI analyst & research
        app.MapPost(g + "/projects/{id}/analyst", (string id, HttpRequest req, CancellationToken ct) => Handle(async () =>
        {
            if (!analyst.IsConfigured) throw new InvalidOperationException(ClaudeGateway.Hint);
            var body = await Body<AskRequest>(req);
            return await analyst.AskAsync(id, body.ConversationId, body.Question ?? "", ct);
        }));
        app.MapGet(g + "/projects/{id}/analyst", (string id) => Handle(() => Task.FromResult<object>(new { configured = analyst.IsConfigured, conversations = analyst.Conversations(projects.Require(id).Id) })));
        app.MapGet(g + "/projects/{id}/analyst/{cid}", (string id, string cid) => Handle(() => Task.FromResult<object>(new { messages = analyst.History(projects.Require(id).Id, cid) })));
        app.MapGet(g + "/projects/{id}/research", (string id) => Handle(() => Task.FromResult<object>(new
        {
            configured = research.IsConfigured, topics = ResearchService.Topics.Keys, reports = research.List(projects.Require(id).Id),
        })));
        app.MapPost(g + "/projects/{id}/research", (string id, HttpRequest req) => Handle(async () =>
        {
            var body = await Body<ResearchRequest>(req);
            return research.Start(id, body.Topic ?? "custom", body.Question);
        }));
    }

    // ---------------------------------------------------------------- helpers

    private static object Summary(Project p, AnalyticsEngine engine) => new { project = p, status = engine.Status(p) };

    /// <summary>Resolves date filters relative to "today" in the project's time zone.</summary>
    public static Filters ParseFilters(Project p, HttpRequest r)
    {
        var today = AnalyticsEngine.Today(p);
        var range = DateRange.Resolve(r.Query["preset"].ToString() is { Length: > 0 } pr ? pr : "last_14_days", r.Query["start"], r.Query["end"], today);
        if (range.Days > 400) throw new ArgumentException("Choose a range of 400 days or less.");
        string? Q(string k) => r.Query[k].ToString() is { Length: > 0 } v && v != "all" ? v : null;
        var platform = Q("platform");
        if (platform is not null && platform is not (Integrations.GoogleAds or Integrations.Meta)) throw new ArgumentException("Platform filter must be google_ads or meta.");
        return new Filters(range.Start, range.End, Q("country")?.ToUpperInvariant(), platform, Q("campaign"), Q("channel"));
    }

    private static Task<IResult> View(ProjectStore projects, string id, HttpRequest r, Func<Project, Filters, object> compute) =>
        Handle(() =>
        {
            var p = projects.Require(id);
            return Task.FromResult(compute(p, ParseFilters(p, r)));
        });

    private static async Task<IResult> Handle(Func<Task<object>> body)
    {
        try
        {
            return Ok(await body().ConfigureAwait(false));
        }
        catch (KeyNotFoundException ex)
        {
            return Error(ex.Message, 404);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            return Error(Redactor.Redact(ex.Message), 400);
        }
        catch (Anthropic.Exceptions.AnthropicApiException ex)
        {
            return Error("Claude API error: " + Redactor.Redact(ex.Message), 502);
        }
    }

    private static IResult Ok(object o) => Results.Text(Json.Serialize(o), "application/json; charset=utf-8");
    private static IResult Error(string message, int status) => Results.Text(Json.Serialize(new { error = message }), "application/json; charset=utf-8", statusCode: status);

    private static async Task<T> Body<T>(HttpRequest req) where T : new() =>
        req.ContentLength is 0 ? new T() : await JsonSerializer.DeserializeAsync<T>(req.Body, JsonIn).ConfigureAwait(false) ?? new T();

    /// <summary>Lists every account / property / app the configured credentials can see, for the project wizard.</summary>
    private static async Task<object> DiscoverAsync(IServiceProvider sp, CancellationToken ct)
    {
        async Task<object> Try(string integration, bool configured, string hint, Func<Task<IEnumerable<object>>> list)
        {
            if (!configured) return new { integration, configured = false, hint, items = Array.Empty<object>() };
            try { return new { integration, configured = true, items = (await list().ConfigureAwait(false)).ToArray() }; }
            catch (Exception ex) when (ex is not OperationCanceledException) { return new { integration, configured = true, error = ToolRunner.Describe(ex), items = Array.Empty<object>() }; }
        }
        var google = sp.GetRequiredService<GoogleAdsPlatform>();
        var meta = sp.GetRequiredService<MetaAdsPlatform>();
        var ga4 = sp.GetRequiredService<AnalyticsService>();
        var play = sp.GetRequiredService<PlayConsoleService>();
        var tasks = new[]
        {
            Try(Integrations.GoogleAds, google.IsConfigured, google.ConfigurationHint, async () => (await google.GetAccountsAsync(ct)).Where(a => a.IsManager != true)
                .Select(a => (object)new { id = a.AccountId, name = a.Name, currency = a.Currency, timeZone = a.TimeZone })),
            Try(Integrations.Meta, meta.IsConfigured, meta.ConfigurationHint, async () => (await meta.GetAccountsAsync(ct))
                .Select(a => (object)new { id = a.AccountId, name = a.Name, currency = a.Currency, timeZone = a.TimeZone })),
            Try(Integrations.Ga4, ga4.IsConfigured, AnalyticsOptions.Hint, async () => (await ga4.GetPropertiesAsync(ct))
                .Select(x => (object)new { id = x.PropertyId, name = x.DisplayName, account = x.AccountName, apps = x.Apps })),
            Try(Integrations.GooglePlay, play.IsConfigured, PlayConsoleOptions.Hint, async () => (await play.GetAppsAsync(ct))
                .Select(a => (object)new { id = a.PackageName, name = a.DisplayName })),
        };
        return new { integrations = await Task.WhenAll(tasks).ConfigureAwait(false) };
    }

    // ---------------------------------------------------------------- request bodies

    public sealed class ProjectInput
    {
        public string? Name { get; set; }
        public string? Genre { get; set; }
        public string? Platforms { get; set; }
        public string? AndroidStoreUrl { get; set; }
        public string? IosStoreUrl { get; set; }
        public string? TargetCountries { get; set; }
        public string? Monetization { get; set; }
        public string? Currency { get; set; }
        public string? TimeZone { get; set; }
        public string? LaunchStage { get; set; }
        public string? Objectives { get; set; }
        public string? TutorialEvent { get; set; }
        public string? PurchaseEvent { get; set; }
        public int? BackfillDays { get; set; }
        public int? SyncIntervalMinutes { get; set; }
        public bool? Archived { get; set; }
        public bool? SyncNow { get; set; }
        public List<ConnectionInput>? Connections { get; set; }

        public Project ToProject() => new()
        {
            Name = Name ?? "", Genre = Genre, Platforms = Platforms, AndroidStoreUrl = AndroidStoreUrl, IosStoreUrl = IosStoreUrl,
            TargetCountries = TargetCountries, Monetization = Monetization, Currency = Currency, TimeZone = TimeZone, LaunchStage = LaunchStage,
            Objectives = Objectives, TutorialEvent = TutorialEvent, PurchaseEvent = PurchaseEvent, BackfillDays = BackfillDays ?? 90,
            SyncIntervalMinutes = SyncIntervalMinutes ?? 60, Archived = Archived ?? false,
            Connections = Connections?.Select(c => c.ToConnection()).ToList() ?? [],
        };
    }

    public sealed class ConnectionInput
    {
        public string? Integration { get; set; }
        public string? ExternalId { get; set; }
        public string? DisplayName { get; set; }
        public string? Currency { get; set; }
        public string? TimeZone { get; set; }
        public ProjectConnection ToConnection() => new(Integration ?? "", ExternalId ?? "", DisplayName, Currency, TimeZone);
    }

    public sealed class TargetInput
    {
        public string? Metric { get; set; }
        public string? ScopeType { get; set; }
        public string? ScopeValue { get; set; }
        public decimal? Target { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class SyncRequest { public string? Integration { get; set; } }
    public sealed class AskRequest { public string? Question { get; set; } public string? ConversationId { get; set; } }
    public sealed class ResearchRequest { public string? Topic { get; set; } public string? Question { get; set; } }
}
