using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Meeza.MarketingMcp.Storage;

public static class Integrations
{
    public const string GoogleAds = "google_ads";
    public const string Meta = "meta";
    public const string Ga4 = "ga4";
    public const string GooglePlay = "google_play";
    public const string Adjust = "adjust";
    public static readonly string[] All = [GoogleAds, Meta, Ga4, Adjust, GooglePlay];
    public static readonly string[] Synced = [GoogleAds, Meta, Ga4, Adjust];
}

public sealed record Project
{
    public string Id { get; init; } = "";
    public required string Name { get; init; }
    public string? Genre { get; init; }
    /// <summary>Comma-separated: android, ios.</summary>
    public string? Platforms { get; init; }
    public string? AndroidStoreUrl { get; init; }
    public string? IosStoreUrl { get; init; }
    /// <summary>Comma-separated ISO country codes.</summary>
    public string? TargetCountries { get; init; }
    public string? Monetization { get; init; }
    public string? Currency { get; init; }
    public string? TimeZone { get; init; }
    public string? LaunchStage { get; init; }
    public string? Objectives { get; init; }
    /// <summary>GA4 event that marks tutorial completion (funnel step). Null = not configured.</summary>
    public string? TutorialEvent { get; init; }
    /// <summary>GA4 event that marks the start of the tutorial (funnel step). Null = tutorial_begin.</summary>
    public string? TutorialStartEvent { get; init; }
    /// <summary>The tutorial start event to read: the configured one, else the standard GA4 name.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string TutorialStart => TutorialStartEvent ?? "tutorial_begin";
    /// <summary>GA4 event that marks a purchase. Default in_app_purchase.</summary>
    public string? PurchaseEvent { get; init; }
    /// <summary>
    /// First-time player (NUX) journey: GA4 events in the order a new player should hit them, e.g. tutorial_begin →
    /// tutorial_step_3 → tutorial_complete → level_1_complete. Empty = not configured (the funnel uses the standard
    /// tutorial events). Null on input means "keep what is saved".
    /// </summary>
    public List<NuxStep>? NuxEvents { get; init; } = [];
    public int BackfillDays { get; init; } = 90;
    public int SyncIntervalMinutes { get; init; } = 60;
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public bool Archived { get; init; }
    public List<ProjectConnection> Connections { get; init; } = [];

    /// <summary>
    /// This game's store IDs (Android package from the Play link, numeric App Store ID from the iOS link). An ad
    /// account can advertise several games; ad syncs keep only campaigns promoting one of these apps. Empty = no store
    /// link set, so every campaign is kept.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlySet<string> AppIds => StoreAppIds(AndroidStoreUrl, IosStoreUrl);

    public static IReadOnlySet<string> StoreAppIds(string? androidStoreUrl, string? iosStoreUrl)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (androidStoreUrl is { Length: > 0 } a &&
            System.Text.RegularExpressions.Regex.Match(a, @"[?&]id=([A-Za-z0-9_.]+)") is { Success: true } am)
            ids.Add(am.Groups[1].Value);
        if (iosStoreUrl is { Length: > 0 } i &&
            System.Text.RegularExpressions.Regex.Match(i, @"/id(\d+)") is { Success: true } im)
            ids.Add(im.Groups[1].Value);
        return ids;
    }
}

/// <summary>One step of the first-time player journey: a GA4 event name and an optional friendly label.</summary>
public sealed record NuxStep(string Event, string? Label = null);

public sealed record ProjectConnection(string Integration, string ExternalId, string? DisplayName, string? Currency, string? TimeZone);

public sealed record KpiTarget(long Id, string Metric, string ScopeType, string ScopeValue, decimal Target, string? Notes, string UpdatedAt);

/// <summary>Projects, their account connections and KPI targets. Every query is scoped by project id.</summary>
public sealed class ProjectStore(MarketingDb db)
{
    public const int MinBackfillDays = 7, MaxBackfillDays = 730;
    public const int MaxNuxSteps = 20;

    public IReadOnlyList<Project> List(bool includeArchived = false)
    {
        using var c = db.Open();
        var projects = c.Query("SELECT * FROM projects" + (includeArchived ? "" : " WHERE archived = 0") + " ORDER BY name COLLATE NOCASE", null, Map);
        var conns = c.Query("SELECT * FROM project_connections ORDER BY integration, external_id", null, r => (Pid: r.Str("project_id")!, Conn: MapConn(r)));
        return projects.Select(p => p with { Connections = conns.Where(x => x.Pid == p.Id).Select(x => x.Conn).ToList() }).ToArray();
    }

    public Project? Get(string id)
    {
        using var c = db.Open();
        var p = c.Query("SELECT * FROM projects WHERE id = @id", new { id }, Map).FirstOrDefault();
        if (p is null) return null;
        return p with { Connections = c.Query("SELECT * FROM project_connections WHERE project_id = @id ORDER BY integration, external_id", new { id }, MapConn) };
    }

    public Project Require(string id) => Get(id) ?? throw new KeyNotFoundException($"Project '{id}' does not exist.");

    public Project Create(Project input)
    {
        var p = Validate(input);
        var id = NewId(p.Name);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var now = Db.Now();
        c.Exec("""
            INSERT INTO projects(id, name, genre, platforms, android_store_url, ios_store_url, target_countries, monetization, currency, timezone,
                                 launch_stage, objectives, tutorial_event, tutorial_start_event, purchase_event, nux_events, backfill_days, sync_interval_minutes, created_at, updated_at, archived)
            VALUES(@id, @Name, @Genre, @Platforms, @AndroidStoreUrl, @IosStoreUrl, @TargetCountries, @Monetization, @Currency, @TimeZone,
                   @LaunchStage, @Objectives, @TutorialEvent, @TutorialStartEvent, @PurchaseEvent, @NuxEvents, @BackfillDays, @SyncIntervalMinutes, @now, @now, 0)
            """, Params(p, new() { ["id"] = id, ["now"] = now }), tx);
        foreach (var conn in p.Connections) AddConnection(c, tx, id, conn);
        tx.Commit();
        return Require(id);
    }

    public Project Update(string id, Project input)
    {
        var current = Require(id);
        var p = Validate(input.NuxEvents is null ? input with { NuxEvents = current.NuxEvents } : input);
        using var c = db.Open();
        c.Exec("""
            UPDATE projects SET name=@Name, genre=@Genre, platforms=@Platforms, android_store_url=@AndroidStoreUrl, ios_store_url=@IosStoreUrl,
                target_countries=@TargetCountries, monetization=@Monetization, currency=@Currency, timezone=@TimeZone, launch_stage=@LaunchStage,
                objectives=@Objectives, tutorial_event=@TutorialEvent, tutorial_start_event=@TutorialStartEvent, purchase_event=@PurchaseEvent, nux_events=@NuxEvents, backfill_days=@BackfillDays,
                sync_interval_minutes=@SyncIntervalMinutes, archived=@Archived, updated_at=@now
            WHERE id=@id
            """, Params(p, new() { ["id"] = id, ["now"] = Db.Now(), ["Archived"] = p.Archived ? 1 : 0 }));
        return Require(id);
    }

    /// <summary>Deletes a project and (by cascade) every row of data synchronized for it.</summary>
    public bool Delete(string id)
    {
        using var c = db.Open();
        return c.Exec("DELETE FROM projects WHERE id=@id", new { id }) > 0;
    }

    public Project SetConnections(string id, IReadOnlyList<ProjectConnection> connections)
    {
        Require(id);
        foreach (var conn in connections) ValidateConnection(conn);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var existing = c.Query("SELECT integration, external_id FROM project_connections WHERE project_id=@id", new { id }, r => (r.GetString(0), r.GetString(1)), tx).ToHashSet();
        var wanted = connections.Select(x => (x.Integration, Normalize(x.Integration, x.ExternalId))).ToHashSet();
        foreach (var (integration, ext) in existing.Except(wanted))
        {
            // Removing a connection removes that account's data from the project, so the project never shows data
            // from an account it is no longer linked to.
            c.Exec("DELETE FROM project_connections WHERE project_id=@id AND integration=@integration AND external_id=@ext", new { id, integration, ext }, tx);
            PurgeAccountData(c, tx, id, integration, ext);
        }
        foreach (var conn in connections) AddConnection(c, tx, id, conn);
        c.Exec("UPDATE projects SET updated_at=@now WHERE id=@id", new { id, now = Db.Now() }, tx);
        tx.Commit();
        return Require(id);
    }

    private static void PurgeAccountData(SqliteConnection c, SqliteTransaction tx, string pid, string integration, string ext)
    {
        var args = new { pid, platform = integration, ext };
        if (integration is Integrations.GoogleAds or Integrations.Meta)
        {
            foreach (var t in new[] { "ad_daily", "campaign_country_daily", "asset_daily", "creatives", "reach_windows" })
                c.Exec($"DELETE FROM {t} WHERE project_id=@pid AND platform=@platform AND account_id=@ext", args, tx);
        }
        else if (integration == Integrations.Ga4)
        {
            foreach (var t in new[] { "ga4_daily", "ga4_dim_daily", "ga4_events_daily", "ga4_cohorts" })
                c.Exec($"DELETE FROM {t} WHERE project_id=@pid AND property_id=@ext", args, tx);
        }
        else if (integration == Integrations.Adjust)
        {
            foreach (var t in new[] { "adjust_daily", "adjust_campaign_daily", "adjust_cohorts" })
                c.Exec($"DELETE FROM {t} WHERE project_id=@pid AND app_token=@ext", args, tx);
        }
        c.Exec("DELETE FROM sync_state WHERE project_id=@pid AND integration=@platform AND external_id=@ext", args, tx);
    }

    private static void AddConnection(SqliteConnection c, SqliteTransaction tx, string pid, ProjectConnection conn)
    {
        ValidateConnection(conn);
        c.Exec("""
            INSERT INTO project_connections(project_id, integration, external_id, display_name, currency, timezone, added_at)
            VALUES(@pid, @integration, @ext, @name, @currency, @tz, @now)
            ON CONFLICT(project_id, integration, external_id) DO UPDATE SET
                display_name=COALESCE(excluded.display_name, display_name), currency=COALESCE(excluded.currency, currency), timezone=COALESCE(excluded.timezone, timezone)
            """, new { pid, integration = conn.Integration, ext = Normalize(conn.Integration, conn.ExternalId), name = conn.DisplayName, currency = conn.Currency, tz = conn.TimeZone, now = Db.Now() }, tx);
    }

    /// <summary>Canonical account id per integration so the same account is never linked twice under two spellings.</summary>
    public static string Normalize(string integration, string externalId)
    {
        var v = externalId.Trim();
        return integration switch
        {
            Integrations.GoogleAds => v.Replace("-", ""),
            Integrations.Meta => v.StartsWith("act_", StringComparison.OrdinalIgnoreCase) ? "act_" + v[4..] : "act_" + v,
            Integrations.Ga4 => v.Replace("properties/", ""),
            _ => v,
        };
    }

    private static void ValidateConnection(ProjectConnection conn)
    {
        if (!Integrations.All.Contains(conn.Integration)) throw new ArgumentException($"Unknown integration '{conn.Integration}'. Use: {string.Join(", ", Integrations.All)}.");
        var v = Normalize(conn.Integration, conn.ExternalId ?? "");
        var ok = conn.Integration switch
        {
            Integrations.GoogleAds => Regex.IsMatch(v, @"^\d{10}$"),
            Integrations.Meta => Regex.IsMatch(v, @"^act_\d{5,20}$"),
            Integrations.Ga4 => Regex.IsMatch(v, @"^\d{5,15}$"),
            Integrations.GooglePlay => Regex.IsMatch(v, @"^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$"),
            Integrations.Adjust => Regex.IsMatch(v, @"^[A-Za-z0-9]{6,32}$"),
            _ => false,
        };
        if (!ok) throw new ArgumentException($"'{conn.ExternalId}' is not a valid {conn.Integration} id.");
    }

    private static Project Validate(Project p)
    {
        var name = (p.Name ?? "").Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Project name is required (max 100 characters).");
        string? Clean(string? s, int max = 500) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? throw new ArgumentException($"Value too long (max {max}).") : s.Trim();
        string? Url(string? s)
        {
            var v = Clean(s, 400);
            if (v is null) return null;
            return Uri.TryCreate(v, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? v : throw new ArgumentException($"Store URL must be an https URL: {v}");
        }
        var countries = Clean(p.TargetCountries)?.ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (countries.Any(x => !Regex.IsMatch(x, "^[A-Z]{2}$"))) throw new ArgumentException("Target countries must be 2-letter ISO codes, e.g. US, PH, PK.");
        var platforms = Clean(p.Platforms)?.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        if (platforms.Any(x => x is not ("android" or "ios"))) throw new ArgumentException("Platforms must be android and/or ios.");
        var currency = Clean(p.Currency)?.ToUpperInvariant();
        if (currency is not null && !Regex.IsMatch(currency, "^[A-Z]{3}$")) throw new ArgumentException("Currency must be a 3-letter ISO code.");
        var tz = Clean(p.TimeZone);
        if (tz is not null && !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) throw new ArgumentException($"Unknown time zone '{tz}'. Use an IANA name such as Asia/Karachi.");
        string? Event(string? e)
        {
            var v = Clean(e, 40);
            return v is null || Regex.IsMatch(v, "^[A-Za-z][A-Za-z0-9_]{0,39}$") ? v : throw new ArgumentException($"'{v}' is not a valid GA4 event name.");
        }
        List<NuxStep> Nux(List<NuxStep>? steps)
        {
            var list = (steps ?? []).Where(s => !string.IsNullOrWhiteSpace(s?.Event))
                .Select(s => new NuxStep(Event(s.Event)!, Clean(s.Label, 60))).ToList();
            if (list.Count > MaxNuxSteps) throw new ArgumentException($"The first-time player journey can have at most {MaxNuxSteps} steps.");
            var dup = list.GroupBy(s => s.Event, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null) throw new ArgumentException($"'{dup.Key}' appears twice in the first-time player journey. Each step needs its own event.");
            return list;
        }
        return p with
        {
            Name = name, Genre = Clean(p.Genre, 60), Platforms = platforms.Length == 0 ? null : string.Join(",", platforms.Distinct()),
            AndroidStoreUrl = Url(p.AndroidStoreUrl), IosStoreUrl = Url(p.IosStoreUrl),
            TargetCountries = countries.Length == 0 ? null : string.Join(",", countries.Distinct()),
            Monetization = Clean(p.Monetization, 60), Currency = currency, TimeZone = tz, LaunchStage = Clean(p.LaunchStage, 40),
            Objectives = Clean(p.Objectives, 2000), TutorialEvent = Event(p.TutorialEvent), TutorialStartEvent = Event(p.TutorialStartEvent), PurchaseEvent = Event(p.PurchaseEvent),
            NuxEvents = Nux(p.NuxEvents),
            BackfillDays = Math.Clamp(p.BackfillDays == 0 ? 90 : p.BackfillDays, MinBackfillDays, MaxBackfillDays),
            SyncIntervalMinutes = Math.Clamp(p.SyncIntervalMinutes == 0 ? 60 : p.SyncIntervalMinutes, 15, 1440),
        };
    }

    private static Dictionary<string, object?> Params(Project p, Dictionary<string, object?> extra)
    {
        var d = new Dictionary<string, object?>
        {
            ["Name"] = p.Name, ["Genre"] = p.Genre, ["Platforms"] = p.Platforms, ["AndroidStoreUrl"] = p.AndroidStoreUrl, ["IosStoreUrl"] = p.IosStoreUrl,
            ["TargetCountries"] = p.TargetCountries, ["Monetization"] = p.Monetization, ["Currency"] = p.Currency, ["TimeZone"] = p.TimeZone,
            ["LaunchStage"] = p.LaunchStage, ["Objectives"] = p.Objectives, ["TutorialEvent"] = p.TutorialEvent, ["TutorialStartEvent"] = p.TutorialStartEvent, ["PurchaseEvent"] = p.PurchaseEvent,
            ["NuxEvents"] = p.NuxEvents is { Count: > 0 } n ? System.Text.Json.JsonSerializer.Serialize(n.Select(s => new { e = s.Event, l = s.Label })) : null,
            ["BackfillDays"] = p.BackfillDays, ["SyncIntervalMinutes"] = p.SyncIntervalMinutes,
        };
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static string NewId(string name)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        return $"{(slug.Length == 0 ? "project" : slug)}-{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";
    }

    private static Project Map(SqliteDataReader r) => new()
    {
        Id = r.Str("id")!, Name = r.Str("name")!, Genre = r.Str("genre"), Platforms = r.Str("platforms"),
        AndroidStoreUrl = r.Str("android_store_url"), IosStoreUrl = r.Str("ios_store_url"), TargetCountries = r.Str("target_countries"),
        Monetization = r.Str("monetization"), Currency = r.Str("currency"), TimeZone = r.Str("timezone"), LaunchStage = r.Str("launch_stage"),
        Objectives = r.Str("objectives"), TutorialEvent = r.Str("tutorial_event"), TutorialStartEvent = r.Str("tutorial_start_event"), PurchaseEvent = r.Str("purchase_event"),
        NuxEvents = ParseNux(r.Str("nux_events")),
        BackfillDays = (int)(r.Long("backfill_days") ?? 90), SyncIntervalMinutes = (int)(r.Long("sync_interval_minutes") ?? 60),
        CreatedAt = r.Str("created_at"), UpdatedAt = r.Str("updated_at"), Archived = r.Long("archived") == 1,
    };

    private static List<NuxStep> ParseNux(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray()
                .Select(x => new NuxStep(x.GetProperty("e").GetString() ?? "", x.TryGetProperty("l", out var l) ? l.GetString() : null))
                .Where(s => s.Event.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException) { return []; }
    }

    private static ProjectConnection MapConn(SqliteDataReader r) =>
        new(r.Str("integration")!, r.Str("external_id")!, r.Str("display_name"), r.Str("currency"), r.Str("timezone"));

    // ---------------------------------------------------------------- KPI targets

    public static readonly IReadOnlyDictionary<string, (string Label, bool HigherIsBetter, string Unit)> TargetMetrics =
        new Dictionary<string, (string, bool, string)>
        {
            ["cpi"] = ("CPI", false, "money"), ["ctr"] = ("CTR", true, "percent"), ["cpm"] = ("CPM", false, "money"),
            ["cpc"] = ("CPC", false, "money"), ["ipm"] = ("IPM", true, "number"),
            ["d1"] = ("D1 retention", true, "percent"), ["d3"] = ("D3 retention", true, "percent"), ["d7"] = ("D7 retention", true, "percent"),
            ["d14"] = ("D14 retention", true, "percent"), ["d30"] = ("D30 retention", true, "percent"),
            ["dau"] = ("DAU", true, "number"), ["wau"] = ("WAU", true, "number"), ["mau"] = ("MAU", true, "number"),
            ["revenue"] = ("Revenue", true, "money"), ["arpdau"] = ("ARPDAU", true, "money"), ["ltv"] = ("LTV", true, "money"),
            ["roas_d7"] = ("D7 ROAS", true, "percent"), ["roas_d14"] = ("D14 ROAS", true, "percent"), ["roas_d30"] = ("D30 ROAS", true, "percent"),
            ["installs"] = ("Installs", true, "number"), ["spend"] = ("Spend", false, "money"),
        };

    public IReadOnlyList<KpiTarget> Targets(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM kpi_targets WHERE project_id=@projectId ORDER BY metric, scope_type, scope_value", new { projectId },
            r => new KpiTarget(r.Long("id")!.Value, r.Str("metric")!, r.Str("scope_type")!, r.Str("scope_value")!, r.Dec("target")!.Value, r.Str("notes"), r.Str("updated_at")!));
    }

    public KpiTarget UpsertTarget(string projectId, string metric, string? scopeType, string? scopeValue, decimal target, string? notes)
    {
        Require(projectId);
        metric = metric.Trim().ToLowerInvariant();
        if (!TargetMetrics.ContainsKey(metric)) throw new ArgumentException($"Unknown KPI '{metric}'. Use: {string.Join(", ", TargetMetrics.Keys)}.");
        scopeType = string.IsNullOrWhiteSpace(scopeType) ? "all" : scopeType.Trim().ToLowerInvariant();
        if (scopeType is not ("all" or "country" or "platform" or "campaign")) throw new ArgumentException("Scope must be all, country, platform or campaign.");
        scopeValue = scopeType == "all" ? "" : (scopeValue ?? "").Trim();
        if (scopeType != "all" && scopeValue.Length == 0) throw new ArgumentException("A scope value is required (country code, platform or campaign id).");
        if (scopeType == "country") scopeValue = scopeValue.ToUpperInvariant();
        if (target < 0) throw new ArgumentException("Targets cannot be negative.");
        using var c = db.Open();
        c.Exec("""
            INSERT INTO kpi_targets(project_id, metric, scope_type, scope_value, target, notes, updated_at)
            VALUES(@projectId, @metric, @scopeType, @scopeValue, @target, @notes, @now)
            ON CONFLICT(project_id, metric, scope_type, scope_value) DO UPDATE SET target=excluded.target, notes=excluded.notes, updated_at=excluded.updated_at
            """, new { projectId, metric, scopeType, scopeValue, target, notes, now = Db.Now() });
        return Targets(projectId).Single(t => t.Metric == metric && t.ScopeType == scopeType && t.ScopeValue == scopeValue);
    }

    public bool DeleteTarget(string projectId, long id)
    {
        using var c = db.Open();
        return c.Exec("DELETE FROM kpi_targets WHERE project_id=@projectId AND id=@id", new { projectId, id }) > 0;
    }
}
