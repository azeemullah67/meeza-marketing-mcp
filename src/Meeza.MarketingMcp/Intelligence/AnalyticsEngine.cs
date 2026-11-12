using Meeza.Marketing.Shared.Models;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;

namespace Meeza.MarketingMcp.Intelligence;

public sealed record TrendPoint(DateOnly Date, decimal? Value, bool Partial = false);

public sealed record Kpi
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    /// <summary>money | percent | number | ratio</summary>
    public required string Unit { get; init; }
    public string? Currency { get; init; }
    public decimal? Value { get; init; }
    public decimal? Previous { get; init; }
    public decimal? ChangePct => Stats.PctChange(Value, Previous);
    public bool HigherIsBetter { get; init; } = true;
    public decimal? Target { get; init; }
    public string? TargetScope { get; init; }
    /// <summary>100 = on target. For lower-is-better KPIs (CPI, CPM, CPC) it is target ÷ value.</summary>
    public decimal? AchievementPct => Target is null or 0 || Value is null or 0 ? null
        : Math.Round(HigherIsBetter ? Value.Value * 100m / Target.Value : Target.Value * 100m / Value.Value, 1);
    public IReadOnlyList<TrendPoint> Trend { get; init; } = [];
    public required string Source { get; init; }
    public string? LastUpdated { get; init; }
    public string? Freshness { get; init; }
    /// <summary>ok | unavailable | estimate | partial</summary>
    public string Status { get; init; } = "ok";
    public string? Definition { get; init; }
    public string? Note { get; init; }
    public object? Breakdown { get; init; }
}

/// <summary>
/// Computes every dashboard view from the local store (never from live APIs), so pages load instantly and keep working
/// while a sync runs or a platform is down. Each figure carries its source, freshness and definition.
/// </summary>
public sealed partial class AnalyticsEngine(ProjectStore projects, SyncStore syncs, ProjectData data, SyncEngine engine)
{
    public ProjectData Data => data;

    /// <param name="syncing">Overrides "is a sync running": the check that runs at the end of a sync passes false so a
    /// source that just failed is reported as failed instead of as still syncing.</param>
    public IReadOnlyList<IntegrationStatus> Status(Project p, DateTimeOffset? now = null, bool? syncing = null)
    {
        var states = syncs.States(p.Id);
        var running = syncing ?? engine.IsRunning(p.Id);
        return p.Connections.Where(c => Integrations.Synced.Contains(c.Integration)).Select(c =>
            Freshness.Evaluate(c, states.FirstOrDefault(s => s.Integration == c.Integration && s.ExternalId == c.ExternalId),
                p.SyncIntervalMinutes, running, now ?? DateTimeOffset.UtcNow)).ToArray();
    }

    /// <summary>Today in the project's reporting zone (project time zone, else the first connected account's).</summary>
    public static DateOnly Today(Project p) =>
        TimeZones.Today(p.TimeZone ?? p.Connections.Where(c => c.Integration != Integrations.Adjust).Select(c => c.TimeZone).FirstOrDefault(t => t is not null)); // Adjust reports UTC days, not the game's zone

    // ---------------------------------------------------------------- overview

    public object Overview(string projectId, Filters f)
    {
        var p = projects.Require(projectId);
        var status = Status(p);
        var targets = projects.Targets(projectId);
        var today = Today(p);
        var notes = new List<string>();

        // ---- ads (country filter switches to the campaign x country table, which has no ad-level detail)
        var useCountry = f.Country is not null;
        var (currency, adRows, prevRows) = AdRowsInPrimaryCurrency(p, f, notes);
        var cur = Perf.Of(adRows.Now);
        var prev = Perf.Of(adRows.Prev);
        var daily = adRows.Now.GroupBy(r => r.Date).ToDictionary(g => g.Key, g => Perf.Of(g));
        var byPlatform = adRows.Now.GroupBy(r => r.Platform).Select(g => (Platform: g.Key, Perf: Perf.Of(g))).ToArray();
        var adSources = status.Where(s => s.Integration is Integrations.GoogleAds or Integrations.Meta && (f.Platform is null || s.Integration == f.Platform)).ToArray();
        // Freshness comes from the sources that actually hold data; a source that never synced is called out instead
        // of making every ad KPI look "never updated".
        var contributing = adSources.Where(s => s.LastSuccessAt is not null).ToArray();
        var missing = adSources.Where(s => s.LastSuccessAt is null).ToArray();
        var adSource = string.Join(" + ", (contributing.Length > 0 ? contributing : adSources).Select(s => Label(s.Integration)).Distinct());
        var (adUpdated, adFresh) = Oldest(contributing.Length > 0 ? contributing : adSources);
        if (missing.Length > 0 && contributing.Length > 0)
            notes.Add($"Ad totals exclude {string.Join(", ", missing.Select(s => $"{Label(s.Integration)} {s.ExternalId}"))}: it has never synced successfully (details in Integrations).");
        else if (contributing.Any(s => s.Freshness is "error" or "stale"))
            notes.Add("Some ad data is not current: " + string.Join("; ", contributing.Where(s => s.Freshness is "error" or "stale").Select(s => $"{Label(s.Integration)} {s.ExternalId}: {s.FreshnessReason}")));
        var platformsWithInstalls = byPlatform.Count(x => x.Perf.Installs > 0);

        IReadOnlyList<TrendPoint> Trend(Func<Perf, decimal?> sel) => Stats.Days(f.Start, f.End)
            .Select(d => new TrendPoint(d, daily.TryGetValue(d, out var x) ? sel(x) : sel(new Perf()), d >= today)).ToArray();

        Kpi AdKpi(string key, string label, string unit, bool higher, Func<Perf, decimal?> sel, string definition, string? note = null, object? breakdown = null)
        {
            var t = TargetFor(targets, key, f);
            return new Kpi
            {
                Key = key, Label = label, Unit = unit, Currency = unit == "money" ? currency : null, HigherIsBetter = higher,
                Value = adRows.Now.Count == 0 ? null : sel(cur), Previous = adRows.Prev.Count == 0 ? null : sel(prev),
                Target = t?.Target, TargetScope = t is null ? null : Scope(t), Trend = Trend(sel), Source = adSources.Length == 0 ? "No ad account connected" : adSource,
                LastUpdated = adUpdated, Freshness = adFresh, Status = adSources.Length == 0 ? "unavailable" : adRows.Now.Count == 0 ? "unavailable" : "ok",
                Definition = definition, Note = note, Breakdown = breakdown,
            };
        }

        var platformBreakdown = byPlatform.Select(x => new { platform = x.Platform, spend = MetricMath.Round(x.Perf.Spend), installs = x.Perf.Installs, cpi = x.Perf.Cpi, ctr = x.Perf.Ctr, cpm = x.Perf.Cpm }).ToArray();
        var kpis = new List<Kpi>
        {
            AdKpi("spend", "Ad spend", "money", false, x => MetricMath.Round(x.Spend), "Sum of spend reported by the ad platforms, in the account currency (never converted).", breakdown: platformBreakdown),
            AdKpi("cpi", "CPI", "money", false, x => x.Cpi,
                platformsWithInstalls > 1
                    ? "Spend ÷ platform-attributed installs, summed across ad platforms. Meta and Google Ads attribute independently and can both claim one install, so this can understate the true CPI. See per-platform values."
                    : "Spend ÷ installs attributed by the ad platform.",
                platformsWithInstalls > 1 ? "Blended across platforms: possible double counting." : null, platformBreakdown),
            AdKpi("cpm", "CPM", "money", false, x => x.Cpm, "Spend per 1,000 impressions."),
            AdKpi("cpc", "CPC", "money", false, x => x.Cpc, "Spend ÷ clicks."),
            AdKpi("ctr", "CTR", "percent", true, x => x.Ctr, "Clicks ÷ impressions."),
            AdKpi("ipm", "IPM", "number", true, x => x.Ipm, "Platform-attributed installs per 1,000 impressions."),
            AdKpi("impressions", "Impressions", "number", true, x => x.Impressions, "Ad impressions reported by the platforms."),
        };
        if (cur.Installs is null && adRows.Now.Count > 0)
            notes.Add("No ad platform reports installs for this selection (no install conversion tracking), so CPI and IPM are unavailable rather than zero.");

        // ---- reach & frequency (Meta only, fixed windows: reach is not additive)
        var reach = data.Reach(projectId).Where(r => r.WindowStart == f.Start && r.WindowEnd == f.End && (f.Platform is null or Integrations.Meta)).ToArray();
        var reachNote = reach.Length > 0 ? null : "Reach is reported per window by Meta only and is not additive across days. Choose the last 7, 14 or 30 complete days (ending yesterday) to see it.";
        kpis.Add(new Kpi
        {
            Key = "reach", Label = "Reach", Unit = "number", Value = reach.Length == 0 ? null : reach.Sum(r => r.Reach ?? 0), Source = "Meta Ads",
            Status = reach.Length == 0 || useCountry ? "unavailable" : "ok", Note = reachNote, Definition = "Unique people reached (Meta). Google App campaigns do not report reach.",
            LastUpdated = adUpdated, Freshness = adFresh,
        });
        kpis.Add(new Kpi
        {
            Key = "frequency", Label = "Frequency", Unit = "ratio",
            Value = reach.Length == 1 ? reach[0].Frequency : reach.Length > 1 && reach.Sum(r => r.Reach ?? 0) > 0 ? Math.Round((decimal)reach.Sum(r => r.Impressions ?? 0) / reach.Sum(r => r.Reach ?? 0), 2) : null,
            Source = "Meta Ads", Status = reach.Length == 0 || useCountry ? "unavailable" : "ok", Note = reachNote, HigherIsBetter = false,
            Definition = "Impressions ÷ reach for the window (Meta).", LastUpdated = adUpdated, Freshness = adFresh,
        });

        // ---- GA4: users, installs, revenue
        var ga4 = status.Where(s => s.Integration == Integrations.Ga4).ToArray();
        var (gaUpdated, gaFresh) = Oldest(ga4);
        var gaNow = LoadGa4(projectId, f);
        var gaPrev = LoadGa4(projectId, f.Previous());
        var gaAvailable = ga4.Length > 0 && gaNow.Days.Count > 0;
        string gaStatus(bool supported) => !supported ? "unavailable" : ga4.Length == 0 || gaNow.Days.Count == 0 ? "unavailable" : "ok";
        Kpi GaKpi(string key, string label, string unit, Func<Ga4Series, decimal?> sel, Func<Ga4Series, IReadOnlyList<TrendPoint>> trend, string definition, bool supported = true, string? note = null, string? cur2 = null)
        {
            var t = TargetFor(targets, key, f);
            return new Kpi
            {
                Key = key, Label = label, Unit = unit, Currency = cur2, Value = supported && gaAvailable ? sel(gaNow) : null,
                Previous = supported && gaPrev.Days.Count > 0 ? sel(gaPrev) : null, Target = t?.Target, TargetScope = t is null ? null : Scope(t),
                Trend = supported && gaAvailable ? trend(gaNow) : [], Source = ga4.Length == 0 ? "GA4 not connected" : "GA4 / Firebase",
                LastUpdated = gaUpdated, Freshness = gaFresh, Status = gaStatus(supported), Definition = definition,
                Note = supported ? note : note ?? "Not available with the current filter.",
            };
        }

        var installsBreakdown = new
        {
            ga4FirstOpens = gaNow.FirstOpens,
            ga4NewUsers = gaNow.Days.Sum(d => d.NewUsers ?? 0),
            platformAttributed = byPlatform.Select(x => new { platform = x.Platform, installs = x.Perf.Installs }).ToArray(),
        };
        kpis.Insert(1, new Kpi
        {
            Key = "installs", Label = "Installs", Unit = "number",
            Value = gaAvailable && !useCountry && f.Channel is null ? gaNow.FirstOpens : cur.Installs,
            Previous = gaAvailable && !useCountry && f.Channel is null ? gaPrev.FirstOpens : prev.Installs,
            Target = TargetFor(targets, "installs", f)?.Target,
            Trend = gaAvailable && !useCountry && f.Channel is null ? gaNow.Days.Select(d => new TrendPoint(d.Date, d.FirstOpens, d.Date >= today)).ToArray() : Trend(x => x.Installs),
            Source = gaAvailable && !useCountry && f.Channel is null ? $"GA4 {p.FirstOpen} (deduplicated, all sources)" : adSource + " (platform-attributed)",
            LastUpdated = gaAvailable ? gaUpdated : adUpdated, Freshness = gaAvailable ? gaFresh : adFresh,
            Status = gaAvailable || cur.Installs is not null ? "ok" : "unavailable",
            Definition = $"With GA4 connected: {p.FirstOpen} events (every source, each device counted once). Platform-attributed installs are shown alongside and are never added together.",
            Breakdown = installsBreakdown,
        });

        var countryOrChannel = useCountry || f.Channel is not null;
        var gaCurrency = gaNow.Days.Select(d => d.Currency).FirstOrDefault(c => c is not null);
        kpis.AddRange(
        [
            GaKpi("dau", "DAU", "number", s => s.AvgDau, s => s.Days.Select(d => new TrendPoint(d.Date, d.ActiveUsers, d.Date >= today)).ToArray(), "Average daily active users over the range."),
            GaKpi("wau", "WAU", "number", s => s.LastWau, s => s.Days.Select(d => new TrendPoint(d.Date, d.Active7d, d.Date >= today)).ToArray(), "Users active in the 7 days ending on the last complete day.", !countryOrChannel),
            GaKpi("mau", "MAU", "number", s => s.LastMau, s => s.Days.Select(d => new TrendPoint(d.Date, d.Active28d, d.Date >= today)).ToArray(), "Users active in the 28 days ending on the last complete day (GA4 28-day active users).", !countryOrChannel),
            GaKpi("revenue", "Revenue", "money", s => s.Revenue, s => s.Days.Select(d => new TrendPoint(d.Date, d.Revenue, d.Date >= today)).ToArray(),
                "In-app purchase + ad revenue recorded in GA4. Zero can mean revenue is not tracked (e.g. AdMob not linked).", cur2: gaCurrency),
            GaKpi("arpdau", "ARPDAU", "money", s => s.Arpdau, s => s.Days.Select(d => new TrendPoint(d.Date, d.ActiveUsers > 0 ? Math.Round((d.Revenue ?? 0) / d.ActiveUsers.Value, 4) : null, d.Date >= today)).ToArray(),
                "Revenue ÷ sum of daily active users.", cur2: gaCurrency),
            GaKpi("arpu", "ARPU", "money", s => s.LastMau is > 0 && s.Revenue is not null ? Math.Round(s.Revenue.Value / s.LastMau.Value, 4) : null, _ => [],
                "Revenue in the range ÷ 28-day active users at the end of the range.", !countryOrChannel, cur2: gaCurrency),
        ]);

        // ---- retention (exact-day, mature cohorts only)
        var (segType, segValue) = CohortSegment(p, f);
        var cells = segType is null ? [] : data.Cohorts(projectId, segType, f.Start, f.End).Where(c => segValue is null || c.SegmentValue == segValue).ToArray();
        var prevCells = segType is null ? [] : data.Cohorts(projectId, segType, f.Previous().Start, f.Previous().End).Where(c => segValue is null || c.SegmentValue == segValue).ToArray();
        var curve = RetentionAnalytics.Curve(cells, today);
        var prevCurve = RetentionAnalytics.Curve(prevCells, today);
        var heat = RetentionAnalytics.Heatmap(cells, today);
        foreach (var d in new[] { 1, 3, 7, 14, 30 })
        {
            var t = TargetFor(targets, $"d{d}", f);
            var point = curve[d];
            kpis.Add(new Kpi
            {
                Key = $"d{d}", Label = $"D{d} retention", Unit = "percent", Value = point.Percent, Previous = RetentionAnalytics.On(prevCurve, d),
                Target = t?.Target, TargetScope = t is null ? null : Scope(t),
                Trend = heat.OrderBy(r => r.Cohort).Select(r => new TrendPoint(r.Cohort, r.Cells[d].Status == CellStatus.Mature ? r.Cells[d].Percent : null)).ToArray(),
                Source = segType is null ? "Not available with this filter" : "GA4 cohorts (first-session date)", LastUpdated = gaUpdated, Freshness = gaFresh,
                Status = segType is null || ga4.Length == 0 ? "unavailable" : point.Percent is null ? "unavailable" : "ok",
                Definition = $"Exact-day: % of users whose first session was in the range who were active exactly on day {d}. Only cohorts that have fully reached day {d} count (no zero-filling).",
                Note = point.Percent is null ? $"No cohort in the range has matured to day {d} yet." : $"{point.CohortUsers:N0} users in {point.Cohorts} cohort(s).",
            });
        }

        // ---- LTV and ROAS (estimates, labelled)
        var ltv = LtvEstimate(curve, gaNow.Arpdau);
        var sameCurrency = gaCurrency is not null && currency is not null && string.Equals(gaCurrency, currency, StringComparison.OrdinalIgnoreCase);
        kpis.Add(new Kpi
        {
            Key = "ltv", Label = "LTV (D30, est.)", Unit = "money", Currency = gaCurrency, Value = ltv.D30, Status = ltv.D30 is null ? "unavailable" : "estimate",
            Source = "GA4 (retention × ARPDAU)", LastUpdated = gaUpdated, Freshness = gaFresh, Target = TargetFor(targets, "ltv", f)?.Target,
            Definition = "Estimate: ARPDAU × expected active days in the first 30 days (observed retention where mature, fitted curve beyond).", Note = ltv.Method,
        });
        decimal? roas = sameCurrency && cur.Spend > 0 && gaNow.Revenue is not null ? Math.Round(gaNow.Revenue.Value * 100m / cur.Spend, 1) : null;
        kpis.Add(new Kpi
        {
            Key = "roas", Label = "ROAS (period)", Unit = "percent", Value = roas, Previous = sameCurrency && prev.Spend > 0 && gaPrev.Revenue is not null ? Math.Round(gaPrev.Revenue.Value * 100m / prev.Spend, 1) : null,
            Status = roas is null ? "unavailable" : "ok", Source = "GA4 revenue ÷ ad spend", LastUpdated = gaUpdated, Freshness = gaFresh,
            Definition = "All GA4 revenue in the period (organic + paid users) ÷ ad spend in the period. A blended efficiency ratio, not cohort ROAS.",
            Note = !sameCurrency ? $"Unavailable: GA4 currency ({gaCurrency ?? "unknown"}) differs from ad spend currency ({currency ?? "none"}); amounts are never converted." : null,
        });
        decimal? roasD7 = sameCurrency && cur.Cpi is > 0 && ltv.D7 is not null ? Math.Round(ltv.D7.Value * 100m / cur.Cpi.Value, 1) : null;
        kpis.Add(new Kpi
        {
            Key = "roas_d7", Label = "D7 ROAS (est.)", Unit = "percent", Value = roasD7, Target = TargetFor(targets, "roas_d7", f)?.Target,
            Status = roasD7 is null ? "unavailable" : "estimate", Source = "GA4 + ad platforms", LastUpdated = gaUpdated, Freshness = gaFresh,
            Definition = "Estimate: (ARPDAU × expected active days through day 7) ÷ CPI. Assumes paid users monetize like the average user.",
            Note = !sameCurrency ? "Unavailable: revenue and spend currencies differ." : null,
        });

        return new
        {
            project = new { p.Id, p.Name, p.Currency, p.TimeZone },
            range = f.Range, previousRange = f.Previous().Range, today = Db.Iso(today),
            filters = f, currency, kpis, acquisition = OrganicVsPaid(projectId, f),
            status, notes,
            otherCurrencies = prevRows,
        };
    }

    /// <summary>Organic versus paid new users from GA4's first-user default channel group.</summary>
    public object OrganicVsPaid(string projectId, Filters f)
    {
        var rows = data.Ga4Dims(projectId, f.Start, f.End, "channel");
        if (rows.Count == 0) return new { available = false, note = "Needs GA4 (first-user channel group)." };
        var groups = rows.GroupBy(r => r.Value).Select(g => new { channel = g.Key, newUsers = g.Sum(x => x.NewUsers ?? 0), kind = ChannelKind(g.Key) })
            .Where(x => x.newUsers > 0).OrderByDescending(x => x.newUsers).ToArray();
        var total = groups.Sum(x => x.newUsers);
        long K(string k) => groups.Where(x => x.kind == k).Sum(x => x.newUsers);
        decimal? Share(long v) => total == 0 ? null : Math.Round(v * 100m / total, 1);
        var daily = rows.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new
        {
            date = g.Key,
            paid = g.Where(x => ChannelKind(x.Value) == "paid").Sum(x => x.NewUsers ?? 0),
            organic = g.Where(x => ChannelKind(x.Value) == "organic").Sum(x => x.NewUsers ?? 0),
            unattributed = g.Where(x => ChannelKind(x.Value) == "unattributed").Sum(x => x.NewUsers ?? 0),
        }).ToArray();
        return new
        {
            available = true, totalNewUsers = total,
            paid = K("paid"), organic = K("organic"), unattributed = K("unattributed"),
            paidShare = Share(K("paid")), organicShare = Share(K("organic")), unattributedShare = Share(K("unattributed")),
            channels = groups, daily,
            definition = "GA4 first-user default channel group. Paid = Paid Search/Social/Video/Shopping, Display, Cross-network, Paid Other. Organic = Organic Search/Social/Video/Shopping, Referral, Email. Direct and Unassigned are shown as unattributed: for apps they usually mean GA4 could not see the install source, not that the user was organic.",
        };
    }

    public static string ChannelKind(string channel)
    {
        var c = channel.ToLowerInvariant();
        if (c.StartsWith("paid") || c is "display" or "cross-network" or "audio" || c.Contains("paid")) return "paid";
        if (c is "direct" or "unassigned" or "(not set)" or "(other)") return "unattributed";
        return "organic";
    }

    // ---------------------------------------------------------------- helpers

    internal sealed record Ga4Point(DateOnly Date, long? ActiveUsers, long? NewUsers, long? Active7d, long? Active28d, decimal? Revenue, long? FirstOpens, string? Currency);

    internal sealed record Ga4Series(IReadOnlyList<Ga4Point> Days)
    {
        public long? FirstOpens => Days.Count == 0 || Days.All(d => d.FirstOpens is null) ? null : Days.Sum(d => d.FirstOpens ?? 0);
        public decimal? AvgDau => Days.Count == 0 ? null : Math.Round((decimal)Days.Average(d => d.ActiveUsers ?? 0), 1);
        public decimal? Revenue => Days.Count == 0 || Days.All(d => d.Revenue is null) ? null : Days.Sum(d => d.Revenue ?? 0);
        public decimal? Arpdau => Revenue is null || Days.Sum(d => d.ActiveUsers ?? 0) == 0 ? null : Math.Round(Revenue.Value / Days.Sum(d => d.ActiveUsers ?? 0), 4);
        public long? LastWau => Days.LastOrDefault(d => d.Active7d is not null)?.Active7d;
        public long? LastMau => Days.LastOrDefault(d => d.Active28d is not null)?.Active28d;
    }

    /// <summary>GA4 daily series honouring the country / channel filter (via dimension splits).</summary>
    internal Ga4Series LoadGa4(string projectId, Filters f)
    {
        var firstOpen = projects.Require(projectId).FirstOpen;
        var events = data.Ga4Events(projectId, f.Start, f.End).Where(e => e.EventName == firstOpen).ToDictionary(e => e.Date, e => e.Users);
        if (f.Country is not null || f.Channel is not null)
        {
            var dim = f.Country is not null ? "country" : "channel";
            var value = f.Country ?? f.Channel!;
            var rows = data.Ga4Dims(projectId, f.Start, f.End, dim).Where(r => string.Equals(r.Value, value, StringComparison.OrdinalIgnoreCase));
            return new Ga4Series(rows.GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new Ga4Point(g.Key, g.Sum(x => x.ActiveUsers ?? 0), g.Sum(x => x.NewUsers ?? 0), null, null,
                g.Sum(x => x.TotalRevenue ?? 0), null, null)).ToArray());
        }
        return new Ga4Series(data.Ga4Daily(projectId, f.Start, f.End).GroupBy(r => r.Date).OrderBy(g => g.Key).Select(g => new Ga4Point(g.Key,
            g.Sum(x => x.ActiveUsers ?? 0), g.Sum(x => x.NewUsers ?? 0), g.All(x => x.Active7d is null) ? null : g.Sum(x => x.Active7d ?? 0),
            g.All(x => x.Active28d is null) ? null : g.Sum(x => x.Active28d ?? 0), g.All(x => x.TotalRevenue is null) ? null : g.Sum(x => x.TotalRevenue ?? 0),
            events.TryGetValue(g.Key, out var fo) ? fo ?? 0 : 0, g.Select(x => x.Currency).FirstOrDefault(c => c is not null))).ToArray());
    }

    /// <summary>Cohort segment for the filter. Campaign filters map to GA4's first-user campaign name (Google Ads auto-tagging).</summary>
    internal (string? Type, string? Value) CohortSegment(Project p, Filters f)
    {
        if (f.Country is not null) return ("country", f.Country.ToUpperInvariant());
        if (f.Channel is not null) return ("channel", f.Channel);
        if (f.Campaign is not null)
        {
            var name = data.Ads(p.Id, f with { Start = f.Start.AddDays(-365) }).FirstOrDefault(a => a.CampaignId == f.Campaign)?.CampaignName;
            return name is null ? (null, null) : ("campaign", name);
        }
        if (f.Platform is not null) return (null, null); // GA4 cannot split users by the ad platform that won the install
        return ("all", "");
    }

    internal sealed record AdSets(IReadOnlyList<AdFact> Now, IReadOnlyList<AdFact> Prev);

    /// <summary>Ad rows in the primary currency (project currency, else the one with the most spend); other currencies are reported separately.</summary>
    private (string? Currency, AdSets Rows, object? Other) AdRowsInPrimaryCurrency(Project p, Filters f, List<string> notes)
    {
        IReadOnlyList<AdFact> Load(Filters x) => f.Country is null
            ? data.Ads(p.Id, x)
            : data.Countries(p.Id, x).Select(c => new AdFact(c.Date, c.Platform, c.AccountId, c.CampaignId, c.CampaignName, null, null, "", null, null, "", null, null,
                c.Currency, c.Spend, c.Impressions, null, c.Clicks, c.Conversions, c.Installs, null, null, null, null, null)).ToArray();
        var now = Load(f);
        var prev = Load(f.Previous());
        var currency = PrimaryCurrency(p, now.Concat(prev));
        var others = now.Where(r => r.Currency != currency).GroupBy(r => r.Currency)
            .Select(g => new { currency = g.Key, spend = MetricMath.Round(g.Sum(x => x.Spend ?? 0)) }).ToArray();
        if (others.Length > 0)
            notes.Add($"Some ad accounts report in other currencies ({string.Join(", ", others.Select(o => o.currency))}). KPIs show {currency} only; amounts are never converted.");
        return (currency, new AdSets(now.Where(r => r.Currency == currency).ToArray(), prev.Where(r => r.Currency == currency).ToArray()), others.Length == 0 ? null : others);
    }

    internal static string? PrimaryCurrency(Project p, IEnumerable<AdFact> rows)
    {
        var list = rows.ToArray();
        if (p.Currency is not null && list.Any(r => r.Currency == p.Currency)) return p.Currency;
        return list.GroupBy(r => r.Currency).OrderByDescending(g => g.Sum(x => x.Spend ?? 0)).Select(g => g.Key).FirstOrDefault() ?? p.Currency;
    }

    internal sealed record Ltv(decimal? D7, decimal? D30, decimal? D90, string Method);

    /// <summary>LTV estimate = ARPDAU × expected active days. Mature observed retention is used where available, the fitted power curve elsewhere.</summary>
    internal static Ltv LtvEstimate(IReadOnlyList<CurvePoint> curve, decimal? arpdau)
    {
        if (arpdau is null) return new Ltv(null, null, null, "No revenue data, so LTV cannot be estimated.");
        var fit = RetentionAnalytics.Fit(curve);
        decimal? Days(int n)
        {
            var observed = RetentionAnalytics.ObservedActiveDays(curve, n);
            if (observed is not null) return observed;
            if (fit is null) return null;
            decimal sum = 0;
            for (var d = 0; d <= n; d++) sum += curve.ElementAtOrDefault(d)?.Percent is { } pct ? pct / 100m : (decimal)fit.At(d);
            return sum;
        }
        var d7 = Days(7); var d30 = Days(30); var d90 = fit is null ? null : (decimal?)Meeza.Marketing.Analytics.RetentionMath.ExpectedActiveDays(fit, 90);
        var method = fit is null && d30 is null ? "Not enough mature retention data for a curve (needs ~30+ users per day point)."
            : $"ARPDAU {arpdau:0.####} × expected active days (D7 {d7:0.##}, D30 {d30:0.##}). Fitted curve R(d) = {fit?.A:0.###}·d^{fit?.B:0.###} used for days not yet observed.";
        return new Ltv(d7 is null ? null : Math.Round(d7.Value * arpdau.Value, 4), d30 is null ? null : Math.Round(d30.Value * arpdau.Value, 4),
            d90 is null ? null : Math.Round(d90.Value * arpdau.Value, 4), method);
    }

    internal static KpiTarget? TargetFor(IReadOnlyList<KpiTarget> targets, string metric, Filters f)
    {
        var m = targets.Where(t => t.Metric == metric).ToArray();
        return (f.Campaign is not null ? m.FirstOrDefault(t => t.ScopeType == "campaign" && t.ScopeValue == f.Campaign) : null)
               ?? (f.Country is not null ? m.FirstOrDefault(t => t.ScopeType == "country" && t.ScopeValue == f.Country.ToUpperInvariant()) : null)
               ?? (f.Platform is not null ? m.FirstOrDefault(t => t.ScopeType == "platform" && t.ScopeValue == f.Platform) : null)
               ?? m.FirstOrDefault(t => t.ScopeType == "all");
    }

    private static string Scope(KpiTarget t) => t.ScopeType == "all" ? "all" : $"{t.ScopeType}: {t.ScopeValue}";

    /// <summary>Least-fresh timestamp across sources, so a KPI never claims to be newer than its oldest input.</summary>
    internal static (string? LastUpdated, string? Freshness) Oldest(IReadOnlyList<IntegrationStatus> s)
    {
        if (s.Count == 0) return (null, null);
        var order = new[] { "never", "stale", "error", "delayed", "syncing", "fresh" };
        var worst = s.OrderBy(x => Array.IndexOf(order, x.Freshness)).First();
        var oldest = s.Select(x => x.LastSuccessAt).Where(x => x is not null).OrderBy(x => x).FirstOrDefault();
        return (oldest, worst.Freshness);
    }

    public static string Label(string integration) => integration switch
    {
        Integrations.GoogleAds => "Google Ads", Integrations.Meta => "Meta Ads", Integrations.Ga4 => "GA4", Integrations.Adjust => "Adjust", Integrations.GooglePlay => "Google Play", _ => integration,
    };
}
