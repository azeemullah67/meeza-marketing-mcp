using System.Text.RegularExpressions;
using Meeza.Marketing.Shared.Models;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;

namespace Meeza.MarketingMcp.Intelligence;

public sealed record EntityRow(
    string Key, string Platform, string Level, string CampaignId, string? CampaignName, string? AdsetId, string? AdsetName, string? AdId, string? AdName,
    string? Status, string? Channel, Perf Now, Perf Prev, decimal? SpendShare, decimal? D1, decimal? D7, long? CohortUsers, IReadOnlyList<string> Flags,
    IReadOnlyList<TrendPoint> SpendTrend, IReadOnlyList<TrendPoint> CpiTrend);

public sealed partial class AnalyticsEngine
{
    // ---------------------------------------------------------------- retention

    public object Retention(string projectId, Filters f, string segmentType, string? segmentValue, string mode)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        if (mode == "rolling")
            return new
            {
                available = false, mode,
                reason = "Rolling retention (active on day N or any later day) needs user-level event data, e.g. the GA4 BigQuery export. The GA4 Data API only provides exact-day cohort activity, so rolling retention is not shown rather than approximated.",
            };
        if (segmentType == "creative")
            return new
            {
                available = false, mode, segmentType,
                reason = "Retention by creative needs install attribution that links each user to the ad creative (an MMP such as AppsFlyer/Adjust, or Firebase-linked creative parameters). Google Ads and Meta do not pass the creative to GA4, so it is unavailable rather than guessed.",
            };
        if (!Sync.Ga4Syncer.CohortSegments.ContainsKey(segmentType))
            throw new ArgumentException($"Unknown segment '{segmentType}'. Use: {string.Join(", ", Sync.Ga4Syncer.CohortSegments.Keys)}, creative.");

        var cells = data.Cohorts(projectId, segmentType, f.Start, f.End);
        var selected = segmentValue is null || segmentType == "all" ? cells : cells.Where(c => string.Equals(c.SegmentValue, segmentValue, StringComparison.OrdinalIgnoreCase)).ToArray();
        var heat = RetentionAnalytics.Heatmap(selected, today);
        var curve = RetentionAnalytics.Curve(selected, today);
        var curveProvisional = RetentionAnalytics.Curve(selected, today, includeProvisional: true);
        var segments = segmentType == "all" ? [] : RetentionAnalytics.BySegment(cells, today);
        var prev = RetentionAnalytics.Curve(data.Cohorts(projectId, segmentType, f.Previous().Start, f.Previous().End)
            .Where(c => segmentValue is null || segmentType == "all" || string.Equals(c.SegmentValue, segmentValue, StringComparison.OrdinalIgnoreCase)), today);
        var ga = data.Ga4Daily(projectId, f.Start, f.End).GroupBy(d => d.Date).OrderBy(g => g.Key)
            .Select(g => new { date = g.Key, newUsers = g.Sum(x => x.NewUsers ?? 0), returningUsers = g.Sum(x => Math.Max(0, (x.ActiveUsers ?? 0) - (x.NewUsers ?? 0))), activeUsers = g.Sum(x => x.ActiveUsers ?? 0) })
            .ToArray();
        var status = Status(p).Where(s => s.Integration == Integrations.Ga4).ToArray();
        var (updated, freshness) = Oldest(status);
        return new
        {
            available = status.Length > 0, mode = "exact", segmentType, segmentValue, range = f.Range, today = Db.Iso(today), lastUpdated = updated, freshness,
            cohortCount = heat.Count, users = heat.Sum(h => h.Users),
            maturity = new
            {
                d1Mature = heat.Count(h => h.Cells[1].Status == CellStatus.Mature),
                d7Mature = heat.Count(h => h.Cells[7].Status == CellStatus.Mature),
                d30Mature = heat.Count(h => h.Cells[30].Status == CellStatus.Mature),
            },
            heatmap = heat.Select(h => new
            {
                cohort = h.Cohort, users = h.Users,
                cells = h.Cells.Select(c => new { day = c.Day, pct = c.Percent, active = c.ActiveUsers, status = c.Status.ToString().ToLowerInvariant() }),
            }),
            curve, curveWithProvisional = curveProvisional, previousCurve = prev,
            segments = segments.Select(s => new { segment = s.Segment, users = s.Users, curve = s.Curve }),
            segmentValues = cells.GroupBy(c => c.SegmentValue).Select(g => new { value = g.Key, users = g.GroupBy(c => c.CohortDate).Sum(x => x.Max(c => c.CohortUsers)) })
                .OrderByDescending(x => x.users).ToArray(),
            churnTrend = RetentionAnalytics.ChurnTrend(heat),
            newVsReturning = ga,
            definitions = new
            {
                exact = "Exact-day (Day N) retention = users of a first-session cohort who were active on exactly day N ÷ cohort size (GA4 cohortActiveUsers / cohortTotalUsers).",
                rolling = "Rolling retention (active on day N or later) is not available from the GA4 Data API.",
                maturity = $"Immature cells (cohort has not reached day N) are blank, never 0%. Cells in the last {RetentionAnalytics.ProvisionalLagDays} complete days are provisional: GA4 may still be processing them. Curves use mature cells only.",
                segments = "Segments use the user's first-touch values in GA4: country, platform, first-user channel group, first-user campaign (Google Ads auto-tagged campaign name) and Google Ads ad group.",
            },
        };
    }

    // ---------------------------------------------------------------- campaigns

    public object Campaigns(string projectId, Filters f, string level)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var notes = new List<string>();
        var (currency, rows, _) = AdRowsInPrimaryCurrency(p, f with { Country = null }, notes);
        var entities = BuildEntities(p, f, level, rows, today);
        var countries = CountryView(p, f, currency);
        var dailyTotals = Stats.Days(f.Start, f.End).Select(d =>
        {
            var x = Perf.Of(rows.Now.Where(r => r.Date == d));
            return new { date = d, spend = MetricMath.Round(x.Spend), installs = x.Installs, cpi = x.Cpi, ctr = x.Ctr, cpm = x.Cpm, partial = d >= today };
        }).ToArray();
        return new
        {
            range = f.Range, previousRange = f.Previous().Range, level, currency,
            totals = Perf.Of(rows.Now).ToJson(), previousTotals = Perf.Of(rows.Prev).ToJson(),
            rows = entities.Select(EntityJson), daily = dailyTotals, countries, notes,
            flagRules = FlagRules,
        };
    }

    public static readonly object FlagRules = new
    {
        high_cpi = "CPI ≥ 1.5× the median CPI of the same platform's rows (≥ 5 installs).",
        wasted_spend = "Spent ≥ 2× the platform's median CPI with zero tracked installs.",
        low_ctr = "CTR ≤ 0.5× the platform median with ≥ 2,000 impressions.",
        rising_cpm = "CPM up ≥ 25% vs the previous period (≥ 2,000 impressions in both).",
        declining_cvr = "Click-to-install rate down ≥ 25% vs the previous period (≥ 100 clicks in both).",
        poor_quality = "D1 retention of users from this campaign (GA4) ≤ 0.7× the project's D1 (≥ 30 cohort users).",
        good_retention = "D1 retention ≥ 1.2× the project's D1 (≥ 30 cohort users).",
        investigate_before_scaling = "Looks efficient on CPI but retention is unknown or below average, or there are fewer than 20 installs.",
        scale_candidate = "CPI ≤ 0.8× median with ≥ 20 installs and retention at or above the project average.",
    };

    internal IReadOnlyList<EntityRow> BuildEntities(Project p, Filters f, string level, AdSets rows, DateOnly today)
    {
        level = level is "adset" or "ad" ? level : "campaign";
        string Key(AdFact r) => level switch { "ad" => $"{r.Platform}|{r.AdsetId}|{r.AdId}", "adset" => $"{r.Platform}|{r.AdsetId}", _ => $"{r.Platform}|{r.CampaignId}" };
        var prevByKey = rows.Prev.GroupBy(Key).ToDictionary(g => g.Key, g => Perf.Of(g));
        var total = rows.Now.Sum(r => r.Spend ?? 0);

        // Retention per campaign from GA4 first-user campaign cohorts (matched by campaign name).
        var cohorts = level == "campaign" ? data.Cohorts(p.Id, "campaign", f.Start, f.End) : [];
        var adGroupCohorts = level == "adset" ? data.Cohorts(p.Id, "ad_group", f.Start, f.End) : [];
        var overall = RetentionAnalytics.Curve(data.Cohorts(p.Id, "all", f.Start, f.End), today);
        var overallD1 = overall[1].Percent;

        var list = rows.Now.GroupBy(Key).Select(g =>
        {
            var first = g.OrderByDescending(x => x.Date).First();
            var now = Perf.Of(g);
            var prev = prevByKey.GetValueOrDefault(g.Key) ?? new Perf();
            var segCells = level == "campaign" ? cohorts.Where(c => c.SegmentValue == first.CampaignName).ToArray()
                : level == "adset" ? adGroupCohorts.Where(c => c.SegmentValue == first.AdsetName).ToArray() : [];
            var curve = segCells.Length == 0 ? null : RetentionAnalytics.Curve(segCells, today);
            var daily = g.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => Perf.Of(x));
            return new EntityRow(g.Key, first.Platform, level, first.CampaignId, first.CampaignName, level == "campaign" ? null : first.AdsetId,
                level == "campaign" ? null : first.AdsetName, level == "ad" ? first.AdId : null, level == "ad" ? first.AdName : null,
                level switch { "ad" => first.AdStatus, "adset" => first.AdsetStatus, _ => first.CampaignStatus }, first.Channel, now, prev,
                total > 0 ? Math.Round(now.Spend * 100m / total, 1) : null,
                curve?[1].Percent, curve?[7].Percent, curve?[1].CohortUsers, [],
                Stats.Days(f.Start, f.End).Select(d => new TrendPoint(d, daily.TryGetValue(d, out var x) ? MetricMath.Round(x.Spend) : 0, d >= today)).ToArray(),
                Stats.Days(f.Start, f.End).Select(d => new TrendPoint(d, daily.TryGetValue(d, out var x) ? x.Cpi : null, d >= today)).ToArray());
        }).ToList();

        return list.Select(r => r with { Flags = Flag(r, list.Where(x => x.Platform == r.Platform).ToArray(), overallD1) })
            .OrderByDescending(r => r.Now.Spend).ToArray();
    }

    /// <summary>Rule-based flags. Thresholds are relative to the account's own data (no universal benchmarks).</summary>
    internal static IReadOnlyList<string> Flag(EntityRow r, IReadOnlyList<EntityRow> peers, decimal? overallD1)
    {
        var flags = new List<string>();
        var medianCpi = Stats.Median(peers.Where(x => x.Now.Installs >= 5 && x.Now.Cpi is not null).Select(x => x.Now.Cpi!.Value));
        var medianCtr = Stats.Median(peers.Where(x => x.Now.Impressions >= 2000 && x.Now.Ctr is not null).Select(x => x.Now.Ctr!.Value));
        var n = r.Now;
        if (medianCpi is not null && n.Installs >= 5 && n.Cpi >= medianCpi * 1.5m) flags.Add("high_cpi");
        if (medianCpi is not null && n.Installs == 0 && n.Spend >= medianCpi * 2) flags.Add("wasted_spend");
        if (medianCtr is not null && n.Impressions >= 2000 && n.Ctr <= medianCtr * 0.5m && peers.Count > 1) flags.Add("low_ctr");
        if (n.Impressions >= 2000 && r.Prev.Impressions >= 2000 && Stats.PctChange(n.Cpm, r.Prev.Cpm) >= 25) flags.Add("rising_cpm");
        if (n.Clicks >= 100 && r.Prev.Clicks >= 100 && Stats.PctChange(n.Cvr, r.Prev.Cvr) <= -25) flags.Add("declining_cvr");
        if (r.D1 is not null && overallD1 is not null && r.CohortUsers >= 30)
        {
            if (r.D1 <= overallD1 * 0.7m) flags.Add("poor_quality");
            if (r.D1 >= overallD1 * 1.2m) flags.Add("good_retention");
        }
        var efficient = medianCpi is not null && n.Cpi is not null && n.Cpi <= medianCpi * 0.8m;
        var retentionOk = r.D1 is not null && overallD1 is not null && r.CohortUsers >= 30 && r.D1 >= overallD1;
        if (efficient && n.Installs >= 20 && retentionOk) flags.Add("scale_candidate");
        else if ((efficient || medianCpi is null && n.Installs > 0) && (n.Installs < 20 || !retentionOk)) flags.Add("investigate_before_scaling");
        return flags;
    }

    private static object EntityJson(EntityRow r) => new
    {
        key = r.Key, platform = r.Platform, level = r.Level, campaignId = r.CampaignId, campaignName = r.CampaignName, adsetId = r.AdsetId, adsetName = r.AdsetName,
        adId = r.AdId, adName = r.AdName, status = r.Status, channel = r.Channel, metrics = r.Now.ToJson(), previous = r.Prev.ToJson(),
        change = new
        {
            spend = Stats.PctChange(r.Now.Spend, r.Prev.Spend), cpi = Stats.PctChange(r.Now.Cpi, r.Prev.Cpi), ctr = Stats.PctChange(r.Now.Ctr, r.Prev.Ctr),
            cpm = Stats.PctChange(r.Now.Cpm, r.Prev.Cpm), installs = Stats.PctChange(r.Now.Installs, r.Prev.Installs), cvr = Stats.PctChange(r.Now.Cvr, r.Prev.Cvr),
        },
        spendShare = r.SpendShare, d1 = r.D1, d7 = r.D7, cohortUsers = r.CohortUsers, flags = r.Flags, spendTrend = r.SpendTrend, cpiTrend = r.CpiTrend,
    };

    /// <summary>Country performance with GA4 retention and opportunity flags.</summary>
    internal object CountryView(Project p, Filters f, string? currency)
    {
        var today = Today(p);
        var now = data.Countries(p.Id, f with { Country = null }).Where(r => r.Currency == currency).ToArray();
        var prev = data.Countries(p.Id, f.Previous() with { Country = null }).Where(r => r.Currency == currency).ToArray();
        var cells = data.Cohorts(p.Id, "country", f.Start, f.End);
        var users = data.Ga4Dims(p.Id, f.Start, f.End, "country").GroupBy(r => r.Value).ToDictionary(g => g.Key, g => g.Sum(x => x.NewUsers ?? 0));
        var overall = Perf.Of(now);
        var overallD1 = RetentionAnalytics.Curve(data.Cohorts(p.Id, "all", f.Start, f.End), today)[1].Percent;
        var targets = (p.TargetCountries ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var rows = now.GroupBy(r => r.Country).OrderByDescending(g => g.Sum(x => x.Spend ?? 0)).Select(g =>
        {
            var perf = Perf.Of(g);
            var pp = Perf.Of(prev.Where(x => x.Country == g.Key));
            var curve = RetentionAnalytics.Curve(cells.Where(c => c.SegmentValue == g.Key), today);
            var flags = new List<string>();
            if (perf.Installs >= 5 && overall.Cpi is not null && perf.Cpi <= overall.Cpi * 0.8m && (curve[1].Percent is null || overallD1 is null || curve[1].Percent >= overallD1 * 0.9m))
                flags.Add("growth_opportunity");
            if (perf.Installs >= 5 && overall.Cpi is not null && perf.Cpi >= overall.Cpi * 1.5m) flags.Add("high_cpi");
            if (curve[1].Percent is not null && overallD1 is not null && curve[1].CohortUsers >= 30 && curve[1].Percent <= overallD1 * 0.7m) flags.Add("poor_quality");
            return new
            {
                country = g.Key, metrics = perf.ToJson(), previous = pp.ToJson(), spendShare = overall.Spend > 0 ? Math.Round(perf.Spend * 100m / overall.Spend, 1) : (decimal?)null,
                ga4NewUsers = users.GetValueOrDefault(g.Key), d1 = curve[1].Percent, d7 = curve[7].Percent, cohortUsers = curve[1].CohortUsers,
                isTarget = targets.Contains(g.Key), flags,
            };
        }).ToList<object>();
        // Organic demand in countries without spend (GA4 new users) is a cheap signal for where to test next.
        var organic = users.Where(kv => now.All(r => r.Country != kv.Key) && kv.Value > 0 && kv.Key is not "(not set)")
            .OrderByDescending(kv => kv.Value).Take(10)
            .Select(kv => new { country = kv.Key, ga4NewUsers = kv.Value, d1 = RetentionAnalytics.Curve(cells.Where(c => c.SegmentValue == kv.Key), today)[1].Percent, isTarget = targets.Contains(kv.Key) })
            .ToArray();
        var untested = targets.Where(t => now.All(r => r.Country != t)).ToArray();
        return new { rows, organicWithoutSpend = organic, untestedTargetCountries = untested };
    }

    // ---------------------------------------------------------------- creatives

    public object Creatives(string projectId, Filters f)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var notes = new List<string>();
        var (currency, rows, _) = AdRowsInPrimaryCurrency(p, f with { Country = null }, notes);
        var items = CreativeItems(p, f, rows, currency);
        var hasAssets = items.Any(i => i.Platform == Integrations.GoogleAds);
        var scored = CreativeIntel.Score(items, today);
        var formats = scored.Where(s => s.Item.Format != "text").GroupBy(s => s.Item.Format).Select(g => new
        {
            format = g.Key, count = g.Count(), metrics = Perf.Sum(g.Select(x => x.Total)).ToJson(),
        }).ToArray();
        var hooks = CreativeIntel.Hooks(scored);
        var themes = scored.Where(s => s.Item.Format != "text").GroupBy(s => CreativeIntel.Theme(s.Item)).Select(g => new
        {
            theme = g.Key, count = g.Count(), metrics = Perf.Sum(g.Select(x => x.Total)).ToJson(),
        }).ToArray();
        if (hasAssets) notes.Add(ReportingNotes.AssetOverlap);
        notes.Add("Retention (D1/D7) by creative is unavailable: Google Ads and Meta do not pass the creative to GA4. Connect an MMP to measure it.");
        notes.Add("Country-specific creative results are not synchronized yet (needs a per-ad country breakdown).");
        return new
        {
            range = f.Range, currency,
            creatives = scored.Select(s => s.ToJson()),
            formats, hooks, themes,
            suggestions = CreativeIntel.Suggestions(scored, formats.Select(x => x.format).ToArray()),
            rules = CreativeIntel.Rules, notes,
        };
    }

    // ---------------------------------------------------------------- funnel

    public object Funnel(string projectId, Filters f)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var ads = Perf.Of(data.Ads(projectId, f));
        var events = data.Ga4Events(projectId, f.Start, f.End).GroupBy(e => e.EventName)
            .ToDictionary(g => g.Key, g => (Count: g.Sum(x => x.EventCount ?? 0), Users: g.Sum(x => x.Users ?? 0)));
        var targets = projects.Targets(projectId);
        long? Ev(string? name) => name is not null && events.TryGetValue(name, out var v) ? v.Users : null;
        var tutorialEvent = p.TutorialEvent ?? (events.ContainsKey("tutorial_complete") ? "tutorial_complete" : null);
        var tutorialBegin = events.ContainsKey("tutorial_begin") ? "tutorial_begin" : null;
        var purchaseEvent = p.PurchaseEvent ?? "in_app_purchase";
        var cells = data.Cohorts(projectId, "all", f.Start, f.End);
        var heat = RetentionAnalytics.Heatmap(cells, today);
        var d1Base = heat.Where(h => h.Cells[1].Status != CellStatus.Immature).Sum(h => h.Users);
        var d1Active = heat.Where(h => h.Cells[1].Status != CellStatus.Immature).Sum(h => h.Cells[1].ActiveUsers ?? 0);
        var ga = data.Ga4Daily(projectId, f.Start, f.End);
        var hasGa = ga.Count > 0;
        var revenue = ga.Sum(x => x.TotalRevenue ?? 0);
        var adRevenue = ga.Sum(x => x.AdRevenue ?? 0);

        var steps = new List<FunnelStep>
        {
            new("impressions", "Impressions", ads.Impressions == 0 ? null : ads.Impressions, "Ad platforms", "ad"),
            new("clicks", "Clicks", ads.Impressions == 0 ? null : ads.Clicks, "Ad platforms", "ad"),
            new("store_visits", "Store visits", null, "Google Play Console", "store", "Store listing visitors come from Play Console statistics (reports bucket). Not connected, so click → store visit → install cannot be separated."),
            new("installs", "Installs (ad-attributed)", ads.Installs is null ? null : (long)ads.Installs.Value, "Ad platforms", "store",
                ads.Installs is null ? "No install conversion tracking on the ad accounts." : "Platform-attributed installs (paid only)."),
            new("first_open", "First open", hasGa ? Ev("first_open") : null, "GA4 first_open", "onboarding",
                "Counted by GA4 inside the game, for paid and organic players together. Ad platforms count installs their own way (store conversions, modelled, up to 30 days after a click), so the two numbers never match exactly: fewer first opens means some installs never opened the game or GA4 has not received them yet."),
            new("tutorial_begin", "Tutorial started", hasGa && tutorialBegin is not null ? Ev(tutorialBegin) : null, "GA4 " + (tutorialBegin ?? "tutorial_begin"), "onboarding",
                tutorialBegin is null ? "No tutorial_begin event found." : null),
            new("tutorial_complete", "Tutorial completed", hasGa && tutorialEvent is not null ? Ev(tutorialEvent) : null, "GA4 " + (tutorialEvent ?? "(not configured)"), "onboarding",
                tutorialEvent is null ? "Set the tutorial completion event in Project settings." : null),
            new("returning_d1", "Returned on day 1", hasGa && d1Base > 0 ? d1Active : null, "GA4 cohorts", "retention",
                $"Of {d1Base:N0} users whose cohort reached day 1."),
            new("purchasers", "Purchasers", hasGa && events.ContainsKey(purchaseEvent) ? Ev(purchaseEvent) : hasGa && ga.Sum(x => x.Purchasers ?? 0) > 0 ? ga.Sum(x => x.Purchasers ?? 0) : null,
                "GA4 " + purchaseEvent, "monetization",
                hasGa && !events.ContainsKey(purchaseEvent) && ga.Sum(x => x.Purchasers ?? 0) == 0 ? $"No {purchaseEvent} events: the game has no in-app purchases, or they are not tracked." : null),
            new("ad_viewers", "Ad viewers", hasGa && events.ContainsKey("ad_impression") ? Ev("ad_impression") : null, "GA4 ad_impression", "monetization",
                events.ContainsKey("ad_impression") ? null : "No ad_impression events: ad revenue is not tracked in GA4 (link AdMob or log ad_impression)."),
        };

        // Conversion between consecutive available steps; the weakest relative to its target (or previous period) is the bottleneck.
        var prevF = f.Previous();
        var prevSteps = FunnelValues(projectId, prevF, today, tutorialEvent, tutorialBegin, purchaseEvent);
        var output = new List<object>();
        FunnelStep? last = null;
        var drops = new List<(FunnelStep From, FunnelStep To, decimal Rate, decimal? PrevRate)>();
        foreach (var s in steps)
        {
            decimal? rate = null, prevRate = null;
            if (s.Value is not null && last?.Value is > 0)
            {
                rate = Math.Round(s.Value.Value * 100m / last.Value.Value, 2);
                if (prevSteps.TryGetValue(s.Key, out var pv) && prevSteps.TryGetValue(last.Key, out var pl) && pl is > 0 && pv is not null)
                    prevRate = Math.Round(pv.Value * 100m / pl.Value, 2);
                drops.Add((last, s, rate.Value, prevRate));
            }
            output.Add(new { key = s.Key, label = s.Label, value = s.Value, source = s.Source, stage = s.Stage, note = s.Note, conversionFromPrevious = rate, previousConversion = prevRate, fromStep = s.Value is not null ? last?.Label : null });
            if (s.Value is not null) last = s;
        }

        var diagnosis = new List<object>();
        var ctrTarget = TargetFor(targets, "ctr", f)?.Target;
        if (ads.Ctr is not null)
            diagnosis.Add(new
            {
                stage = "ad", metric = "CTR", value = ads.Ctr, target = ctrTarget,
                verdict = ctrTarget is null ? "no target set" : ads.Ctr >= ctrTarget ? "on target" : "below target",
                meaning = "Low CTR points to the creative or targeting (advertising problem).",
            });
        foreach (var d in drops.Where(d => d.PrevRate is not null && d.Rate < d.PrevRate * 0.8m))
            diagnosis.Add(new
            {
                stage = d.To.Stage, metric = $"{d.From.Label} → {d.To.Label}", value = d.Rate, previous = d.PrevRate,
                verdict = "declined ≥ 20% vs previous period",
                meaning = StageMeaning(d.To.Stage),
            });
        // Monetization steps are excluded: payer and ad-viewer rates are not comparable with the acquisition funnel.
        var worst = drops.Where(d => d.From.Stage != "ad" && d.To.Key is not "first_open" && d.To.Stage != "monetization").OrderBy(d => d.Rate).FirstOrDefault();
        return new
        {
            range = f.Range, steps = output, diagnosis,
            biggestDrop = worst.To is null ? null : new { from = worst.From.Label, to = worst.To.Label, rate = worst.Rate, stage = worst.To.Stage, meaning = StageMeaning(worst.To.Stage) },
            revenue = hasGa ? new { total = revenue, ad = adRevenue, purchase = ga.Sum(x => x.PurchaseRevenue ?? 0), currency = ga.Select(x => x.Currency).FirstOrDefault(c => c is not null) } : null,
            notes = new[]
            {
                "Ad steps are paid traffic only; GA4 steps include organic users. Ratios across that boundary compare different populations.",
                "GA4 users per step are summed per day; events that fire once per user (first_open, tutorial) are close to unique users, repeated events overstate them.",
                "Diagnosis compares each step with its own previous period and your targets. No universal benchmark is assumed.",
            },
        };
    }

    private Dictionary<string, long?> FunnelValues(string projectId, Filters f, DateOnly today, string? tutorialEvent, string? tutorialBegin, string purchaseEvent)
    {
        var ads = Perf.Of(data.Ads(projectId, f));
        var events = data.Ga4Events(projectId, f.Start, f.End).GroupBy(e => e.EventName).ToDictionary(g => g.Key, g => g.Sum(x => x.Users ?? 0));
        var heat = RetentionAnalytics.Heatmap(data.Cohorts(projectId, "all", f.Start, f.End), today);
        long? E(string? n) => n is not null && events.TryGetValue(n, out var v) ? v : null;
        return new Dictionary<string, long?>
        {
            ["impressions"] = ads.Impressions, ["clicks"] = ads.Clicks, ["installs"] = ads.Installs is null ? null : (long)ads.Installs,
            ["first_open"] = E("first_open"), ["tutorial_begin"] = E(tutorialBegin), ["tutorial_complete"] = E(tutorialEvent),
            ["returning_d1"] = heat.Where(h => h.Cells[1].Status != CellStatus.Immature).Sum(h => h.Cells[1].ActiveUsers ?? 0),
            ["purchasers"] = E(purchaseEvent), ["ad_viewers"] = E("ad_impression"),
        };
    }

    private static string StageMeaning(string stage) => stage switch
    {
        "ad" => "Advertising problem: creative, audience or bidding.",
        "store" => "Store conversion problem: listing, screenshots, ratings, or install attribution.",
        "onboarding" => "Onboarding problem: loading time, first-session experience, tutorial length or difficulty.",
        "retention" => "Retention problem: core loop, difficulty curve, content depth, notifications.",
        "monetization" => "Monetization problem: offers, pricing, ad placement and frequency.",
        _ => "",
    };

    private sealed record FunnelStep(string Key, string Label, long? Value, string Source, string Stage, string? Note = null);

    // ---------------------------------------------------------------- attribution reconciliation

    public object Attribution(string projectId, Filters f)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var ads = data.Ads(projectId, f with { Platform = null, Campaign = null });
        var events = data.Ga4Events(projectId, f.Start, f.End).Where(e => e.EventName == "first_open").ToDictionary(e => e.Date, e => e.Users);
        var sm = data.Ga4Dims(projectId, f.Start, f.End, "source_medium");
        static string Bucket(string sourceMedium)
        {
            var s = sourceMedium.ToLowerInvariant();
            if (Regex.IsMatch(s, @"^google\s*/\s*(cpc|ppc|paid)") || s.Contains("googleads") || s.Contains("google ads")) return "google_ads";
            if (Regex.IsMatch(s, @"(facebook|instagram|\bfb\b|\bmeta\b|\big\b)") && !s.Contains("organic")) return "meta";
            if (s.Contains("organic") || s.Contains("google-play")) return "organic";
            if (s.Contains("(direct)") || s.Contains("(not set)")) return "unattributed";
            return "other";
        }
        var rows = Stats.Days(f.Start, f.End).Select(d =>
        {
            var day = ads.Where(a => a.Date == d).ToArray();
            decimal? Inst(string platform) { var x = day.Where(a => a.Platform == platform).ToArray(); return x.Length == 0 || x.All(a => a.Installs is null) ? null : x.Sum(a => a.Installs ?? 0); }
            var dims = sm.Where(r => r.Date == d).GroupBy(r => Bucket(r.Value)).ToDictionary(g => g.Key, g => g.Sum(r => r.NewUsers ?? 0));
            return new
            {
                date = d, partial = d >= today,
                googleAdsInstalls = Inst(Integrations.GoogleAds), metaInstalls = Inst(Integrations.Meta),
                ga4FirstOpen = events.TryGetValue(d, out var fo) ? fo : null,
                ga4NewUsersGoogleAds = dims.GetValueOrDefault("google_ads"), ga4NewUsersMeta = dims.GetValueOrDefault("meta"),
                ga4NewUsersOrganic = dims.GetValueOrDefault("organic"), ga4NewUsersUnattributed = dims.GetValueOrDefault("unattributed"), ga4NewUsersOther = dims.GetValueOrDefault("other"),
            };
        }).ToArray();
        decimal? g = rows.All(r => r.googleAdsInstalls is null) ? null : rows.Sum(r => r.googleAdsInstalls ?? 0);
        decimal? m = rows.All(r => r.metaInstalls is null) ? null : rows.Sum(r => r.metaInstalls ?? 0);
        var gaG = rows.Sum(r => r.ga4NewUsersGoogleAds);
        var gaM = rows.Sum(r => r.ga4NewUsersMeta);
        var conns = p.Connections.Where(c => c.Integration is Integrations.GoogleAds or Integrations.Meta or Integrations.Ga4)
            .Select(c => new { integration = c.Integration, id = c.ExternalId, timeZone = c.TimeZone, currency = c.Currency }).ToArray();
        return new
        {
            range = f.Range, rows,
            totals = new
            {
                googleAdsInstalls = g, metaInstalls = m, ga4FirstOpen = rows.Sum(r => r.ga4FirstOpen ?? 0),
                ga4NewUsersGoogleAds = gaG, ga4NewUsersMeta = gaM, ga4NewUsersOrganic = rows.Sum(r => r.ga4NewUsersOrganic), ga4NewUsersUnattributed = rows.Sum(r => r.ga4NewUsersUnattributed),
                googleMatchRate = g is > 0 ? Math.Round(gaG * 100m / g.Value, 1) : (decimal?)null,
                metaMatchRate = m is > 0 ? Math.Round(gaM * 100m / m.Value, 1) : (decimal?)null,
                platformSum = g is null && m is null ? (decimal?)null : (g ?? 0) + (m ?? 0),
            },
            connections = conns,
            definitions = new object[]
            {
                new { source = "Google Ads installs", definition = "Conversions from enabled DOWNLOAD-category conversion actions (e.g. Google Play first open via Firebase/Play). Credited to the date of the ad click/interaction, and can still increase for the length of the conversion window (default 30 days).", timezone = "Google Ads account time zone" },
                new { source = "Meta installs", definition = "mobile_app_install actions attributed by Meta (default 7-day click / 1-day view), reported on the impression date. Requires the Meta SDK or an MMP.", timezone = "Meta ad account time zone" },
                new { source = "GA4 first_open", definition = "Every first app open on a device, from every source (paid + organic). Counted on the day it happens.", timezone = "GA4 property time zone" },
                new { source = "GA4 new users by source", definition = "GA4's own last-click attribution of first-time users (first-user source / medium). Users without a known source appear as (direct)/(not set).", timezone = "GA4 property time zone" },
            },
            guidance = new[]
            {
                "Never add platform-reported installs together: Meta and Google Ads can both claim the same install.",
                "A Google Ads count well above GA4's google / cpc users usually means GA4 cannot see the referrer (Direct), not that installs are fake.",
                "Ad platforms keep crediting recent days as conversions arrive; compare days that are at least 7 days old.",
                "Time zones differ by source; a day boundary mismatch moves installs between adjacent days.",
            },
        };
    }

    // ---------------------------------------------------------------- forecasting & budget

    public object Forecast(string projectId, Filters f, decimal? dailyBudget)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var notes = new List<string>();
        var history = f with { Start = today.AddDays(-28), End = today.AddDays(-1) };
        var (currency, rows, _) = AdRowsInPrimaryCurrency(p, history with { Country = null }, notes);
        var daily = Stats.Days(history.Start, history.End).Select(d => (Date: d, Perf: Perf.Of(rows.Now.Where(r => r.Date == d)))).ToArray();
        var model = Forecasting.Build(daily.Select(x => (x.Date, x.Perf.Spend, x.Perf.Installs)).ToArray());
        var cells = data.Cohorts(projectId, "all", today.AddDays(-60), today);
        var curve = RetentionAnalytics.Curve(cells, today);
        var ga = LoadGa4(projectId, history);
        var ltv = LtvEstimate(curve, ga.Arpdau);
        var gaCurrency = ga.Days.Select(d => d.Currency).FirstOrDefault(c => c is not null);
        var sameCurrency = gaCurrency is not null && string.Equals(gaCurrency, currency, StringComparison.OrdinalIgnoreCase);
        var budget = dailyBudget ?? (daily.Length == 0 ? 0 : Math.Round(daily.TakeLast(7).Average(x => x.Perf.Spend), 2));
        var scenarios = new[] { 0.5m, 0.75m, 1m, 1.25m, 1.5m, 2m }.Select(mult =>
        {
            var b = Math.Round(budget * mult, 2);
            var inst = model.InstallsAt(b);
            var cpi = inst is > 0 ? Math.Round(b / inst.Value, 4) : (decimal?)null;
            return new
            {
                multiplier = mult, dailyBudget = b, installsPerDay = inst is null ? (decimal?)null : Math.Round(inst.Value, 1), cpi,
                installs30d = inst is null ? (decimal?)null : Math.Round(inst.Value * 30, 0), spend30d = b * 30,
                d7RevenuePerDay = sameCurrency && inst is not null && ltv.D7 is not null ? Math.Round(inst.Value * ltv.D7.Value, 2) : (decimal?)null,
                d30RoasPct = sameCurrency && cpi is > 0 && ltv.D30 is not null ? Math.Round(ltv.D30.Value * 100m / cpi.Value, 1) : (decimal?)null,
            };
        }).ToArray();

        // Budget reallocation proposals: campaigns ranked by value per unit spend (installs × D1 quality ÷ spend).
        var entities = BuildEntities(p, history, "campaign", rows, today);
        var overallD1 = curve[1].Percent;
        var ranked = entities.Where(e => e.Now.Spend > 0 && e.Now.Installs is not null).Select(e =>
        {
            var quality = e.D1 is not null && overallD1 is > 0 && e.CohortUsers >= 30 ? e.D1.Value / overallD1.Value : 1m;
            var value = (e.Now.Installs ?? 0) * quality / e.Now.Spend;
            return (Entity: e, Quality: quality, Value: value, Evidence: e.Now.Installs >= 20);
        }).OrderByDescending(x => x.Value).ToArray();
        var proposals = new List<object>();
        if (ranked.Length >= 2)
        {
            var best = ranked.First(x => x.Evidence || ranked.All(r => !r.Evidence));
            foreach (var worst in ranked.Reverse().Where(x => x.Entity.Key != best.Entity.Key && x.Value < best.Value * 0.6m).Take(2))
            {
                var move = Math.Round(worst.Entity.Now.Spend / Math.Max(1, history.Days) * 0.25m, 2);
                proposals.Add(new
                {
                    from = worst.Entity.CampaignName, to = best.Entity.CampaignName, platformFrom = worst.Entity.Platform, platformTo = best.Entity.Platform,
                    moveDailyBudget = move, currency,
                    rationale = $"{best.Entity.CampaignName} returns {best.Value * 100:0.##} quality-adjusted installs per 100 {currency} vs {worst.Value * 100:0.##} for {worst.Entity.CampaignName}.",
                    confidence = best.Evidence && worst.Evidence ? "medium" : "low",
                    validation = "Move in a single step of ≤25%, hold 5-7 days, and compare CPI and D1 of both campaigns before moving more.",
                });
            }
        }
        if (proposals.Count == 0) notes.Add("No reallocation proposed: fewer than two campaigns with comparable data, or efficiency differences are small.");

        var revenueTrend = Stats.Slope(ga.Days.Select(d => (double)(d.Revenue ?? 0)).ToArray());
        return new
        {
            currency, historyRange = history.Range, basedOnDays = daily.Count(x => x.Perf.Spend > 0), currentDailyBudget = budget,
            model = model.Describe(),
            cpiForecast = model.CpiForecast(today, 14),
            scenarios,
            ltv = new { ltv.D7, ltv.D30, ltv.D90, currency = gaCurrency, method = ltv.Method },
            revenue = new { last28Days = ga.Revenue, currency = gaCurrency, dailyTrendPerDay = revenueTrend is null ? null : (decimal?)Math.Round((decimal)revenueTrend.Value, 4) },
            reallocation = proposals,
            notes = notes.Concat(new[]
            {
                "All forecasts are estimates from your last 28 days, not guarantees. Platforms' auction dynamics change.",
                sameCurrency ? "Revenue and ROAS use GA4 revenue in the same currency as spend." : $"Revenue/ROAS estimates are unavailable because GA4 currency ({gaCurrency ?? "none"}) differs from spend currency ({currency ?? "none"}).",
                "This app has read-only access to ad accounts: it never changes budgets. Apply any change yourself in the ad platform.",
            }).ToArray(),
        };
    }
}

