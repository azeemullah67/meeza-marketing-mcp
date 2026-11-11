using Meeza.Marketing.Shared.Models;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Intelligence;

public sealed record Evidence(string Metric, string Value, string? Comparison, string Source);

public sealed record Finding
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Problem { get; init; }
    /// <summary>critical | high | medium | low | info</summary>
    public required string Severity { get; init; }
    public required IReadOnlyList<Evidence> Evidence { get; init; }
    public string? Segment { get; init; }
    public string? ComparisonPeriod { get; init; }
    public required string Hypothesis { get; init; }
    /// <summary>high | medium | low — how sure the data makes us about the problem (not the hypothesis).</summary>
    public required string Confidence { get; init; }
    public required string Action { get; init; }
    public string? ExpectedImpact { get; init; }
    public required IReadOnlyList<string> Validation { get; init; }
    /// <summary>
    /// Set by the 30-day check: true when the same problem is also found in the most recent days, so it is current
    /// and not an effect of older data (a test period before launch, an old build, one bad week). Only confirmed
    /// problems become tasks. Null when the finding was not validated (findings outside the check).
    /// </summary>
    public bool? Confirmed { get; init; }
    /// <summary>How the problem was validated, in plain words.</summary>
    public string? Validated { get; init; }
}

/// <summary>
/// Deterministic analyst: rules over the project's stored data. Every finding cites the numbers it is based on and
/// labels root causes as hypotheses. No finding is produced without data, and thresholds are relative to the project's
/// own history or its configured targets, never to invented benchmarks.
/// </summary>
public sealed partial class AnalyticsEngine
{
    /// <summary>Days of spend before a new campaign's CPI is judged against the others (platform learning phase).</summary>
    public const int LearningDays = 3;

    /// <summary>
    /// The D1 action, built from what was measured: tutorial completion and D1 by country, so the person sees whether
    /// the first session or the audience mix is the likelier cause instead of a generic checklist.
    /// </summary>
    private string D1Action(Project p, string projectId, Filters f, decimal? d1, decimal? target, DateOnly today)
    {
        static string M(decimal? v, string _) => v is null ? "n/a" : $"{v:0.#}%";
        var parts = new List<string> { target is null ? $"D1 is {M(d1, "%")}." : $"D1 is {M(d1, "%")} against a {M(target, "%")} target." };
        var events = data.Ga4Events(projectId, f.Start, f.End).GroupBy(e => e.EventName).ToDictionary(g => g.Key, g => g.Sum(x => x.Users ?? 0));
        var tutorial = p.TutorialEvent ?? "tutorial_complete";
        if (events.TryGetValue("first_open", out var opens) && opens >= 30 && events.TryGetValue(tutorial, out var done))
            parts.Add($"{done * 100m / opens:0}% of new players reach {tutorial} ({done:N0} of {opens:N0}); open Funnel and fix the step that loses the most players first.");
        // "(not set)" is not a country anyone can target: GA4 could not place those players (testers, VPNs).
        var countries = data.Cohorts(projectId, "country", f.Start, f.End).Where(c => c.SegmentValue is { Length: > 0 } v && v != "(not set)").GroupBy(c => c.SegmentValue)
            .Select(g => (Country: g.Key, Point: RetentionAnalytics.Curve(g, today)[1]))
            .Where(x => x.Point.Percent is not null && x.Point.CohortUsers >= 30).OrderByDescending(x => x.Point.CohortUsers).Take(3).ToArray();
        if (countries.Length > 1)
        {
            parts.Add("D1 by country: " + string.Join(", ", countries.Select(x => $"{x.Country} {M(x.Point.Percent, "%")} ({x.Point.CohortUsers} players)")) + ".");
            var low = countries.Where(x => d1 is not null && x.Point.Percent <= d1 * 0.7m).Select(x => x.Country).ToArray();
            parts.Add(low.Length > 0 ? $"{string.Join(", ", low)} pulls D1 down: review targeting there before changing the game."
                : "Countries retain alike, so the first session, not the audience mix, is the likelier cause.");
        }
        else if (countries.Length == 1)
            parts.Add($"Almost all measured players are from {countries[0].Country}, so the audience mix is not the cause; the first session is.");
        return string.Join(" ", parts);
    }

    private static readonly string[] SeverityOrder = ["critical", "high", "medium", "low", "info"];

    /// <param name="afterSync">True when called at the end of a sync: sources are judged by how that sync ended, not
    /// reported as "syncing".</param>
    public IReadOnlyList<Finding> Findings(string projectId, Filters f, bool afterSync = false)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var list = new List<Finding>();
        var cmp = $"{f.Range} vs {f.Previous().Range}";
        var notes = new List<string>();
        var (currency, rows, _) = AdRowsInPrimaryCurrency(p, f with { Country = null }, notes);
        var cur = Perf.Of(rows.Now);
        var prev = Perf.Of(rows.Prev);
        var targets = projects.Targets(projectId);
        string M(decimal? v, string unit = "") => v is null ? "n/a" : unit == "money" ? $"{currency} {v:0.##}" : unit == "%" ? $"{v:0.##}%" : $"{v:0.##}";

        // ---- data quality first: conclusions on stale data are unreliable.
        foreach (var s in Status(p, syncing: afterSync ? false : null).Where(s => s.Freshness is "never" or "stale" or "error"))
            list.Add(new Finding
            {
                Id = $"data-{s.Integration}-{s.ExternalId}", Category = "data_quality", Severity = s.Freshness == "error" && s.LastSuccessAt is not null ? "medium" : "high",
                Problem = $"{Label(s.Integration)} data is {(s.Freshness == "never" ? "missing" : "out of date")} ({s.ExternalId})",
                Evidence = [new("Last successful sync", s.LastSuccessAt ?? "never", null, "Sync log"), new("Last error", s.LastError ?? "none", null, "Sync log")],
                Hypothesis = s.LastError?.Contains("token", StringComparison.OrdinalIgnoreCase) == true ? "The access token expired or was revoked." : "The connection is failing; see the error.",
                Confidence = "high", Action = $"Fix the {Label(s.Integration)} connection in Integrations (renew the token / check access), then run Sync now.",
                ExpectedImpact = "Restores complete data; every other finding is more reliable afterwards.",
                Validation = ["Integrations shows 'fresh' for this account.", "The latest data date matches yesterday or today."],
            });

        // ---- acquisition volume and cost
        var ga = LoadGa4(projectId, f);
        var gaPrev = LoadGa4(projectId, f.Previous());
        if (ga.FirstOpens is { } fo && gaPrev.FirstOpens is { } pfo && pfo >= 20 && Stats.PctChange(fo, pfo) <= -20)
        {
            var spendChange = Stats.PctChange(cur.Spend, prev.Spend);
            var cpiChange = Stats.PctChange(cur.Cpi, prev.Cpi);
            var org = OrganicVsPaidNumbers(projectId, f);
            var orgPrev = OrganicVsPaidNumbers(projectId, f.Previous());
            var cause = spendChange <= -15 ? $"Ad spend fell {spendChange:0}%, which alone explains most of the paid decline."
                : cpiChange >= 15 ? $"CPI rose {cpiChange:0}%: the same budget bought fewer installs."
                : orgPrev.Organic > 0 && Stats.PctChange(org.Organic, orgPrev.Organic) <= -25 ? $"Organic new users fell {Stats.PctChange(org.Organic, orgPrev.Organic):0}% while paid stayed similar."
                : "No single driver stands out; check daily delivery for paused campaigns or limited-by-budget days.";
            list.Add(new Finding
            {
                Id = "installs-down", Category = "acquisition", Severity = Stats.PctChange(fo, pfo) <= -40 ? "high" : "medium",
                Problem = $"Installs decreased {Stats.PctChange(fo, pfo):0}%", ComparisonPeriod = cmp,
                Evidence =
                [
                    new("GA4 first opens", fo.ToString("N0"), $"previous {pfo:N0}", "GA4"),
                    new("Ad spend", M(cur.Spend, "money"), $"previous {M(prev.Spend, "money")} ({spendChange:+0;-0}%)", "Ad platforms"),
                    new("CPI", M(cur.Cpi, "money"), $"previous {M(prev.Cpi, "money")}", "Ad platforms"),
                    new("Organic new users", org.Organic.ToString("N0"), $"previous {orgPrev.Organic:N0}", "GA4 channel group"),
                ],
                Hypothesis = cause, Confidence = "medium",
                Action = spendChange <= -15 ? "Confirm the spend drop was intended (budget, end dates, payment issues, disapproved ads)." : "Open Campaigns, sort by CPI change, and look at CPM/CTR/conversion changes on the largest campaigns.",
                Validation = ["Compare daily spend and installs in Campaigns → daily chart.", "Check campaign status/end dates in the ad platform."],
            });
        }

        if (cur.Cpi is not null && prev.Cpi is not null && prev.Installs >= 20 && Stats.PctChange(cur.Cpi, prev.Cpi) >= 20)
        {
            // CPI = CPM ÷ (10 × CTR% × CVR%). Log-decompose the change into its three drivers.
            double L(decimal? a, decimal? b) => a is > 0 && b is > 0 ? Math.Log((double)(a.Value / b.Value)) : 0;
            var parts = new Dictionary<string, double>
            {
                ["CPM (auction cost)"] = L(cur.Cpm, prev.Cpm), ["CTR (creative appeal)"] = -L(cur.Ctr, prev.Ctr), ["click-to-install (store/attribution)"] = -L(cur.Cvr, prev.Cvr),
            };
            var main = parts.OrderByDescending(kv => kv.Value).First();
            list.Add(new Finding
            {
                Id = "cpi-up", Category = "acquisition", Severity = Stats.PctChange(cur.Cpi, prev.Cpi) >= 40 ? "high" : "medium",
                Problem = $"CPI increased {Stats.PctChange(cur.Cpi, prev.Cpi):0}%", ComparisonPeriod = cmp,
                Evidence =
                [
                    new("CPI", M(cur.Cpi, "money"), $"previous {M(prev.Cpi, "money")}", "Ad platforms"),
                    new("CPM", M(cur.Cpm, "money"), $"{Stats.PctChange(cur.Cpm, prev.Cpm):+0;-0}%", "Ad platforms"),
                    new("CTR", M(cur.Ctr, "%"), $"{Stats.PctChange(cur.Ctr, prev.Ctr):+0;-0}%", "Ad platforms"),
                    new("Click-to-install", M(cur.Cvr, "%"), $"{Stats.PctChange(cur.Cvr, prev.Cvr):+0;-0}%", "Ad platforms"),
                ],
                Hypothesis = $"Most of the CPI change comes from {main.Key}. CPI = CPM ÷ (CTR × click-to-install), so the largest moving factor is the likely driver.",
                Confidence = prev.Installs >= 50 && cur.Installs >= 50 ? "high" : "medium",
                Action = main.Key.StartsWith("CPM") ? "Check audience/geo saturation and bid strategy; broaden targeting or test new countries."
                    : main.Key.StartsWith("CTR") ? "Refresh creatives: see Creatives for fatigued ads and test new hooks."
                    : "Check the store listing and install tracking; a click-to-install drop can also be a tracking change.",
                ExpectedImpact = cur.Installs is > 0 && prev.Cpi is > 0 ? $"Returning to the previous CPI would add ≈{(cur.Spend / prev.Cpi.Value - cur.Installs.Value):0} installs for the same spend." : null,
                Validation = ["Track the driver metric daily for 5-7 days after the change.", "Compare against the previous period with the same budget."],
            });
        }

        // ---- campaigns: wasted spend, high CPI, quality
        var entities = BuildEntities(p, f, "campaign", rows, today);
        var wasted = entities.Where(e => e.Flags.Contains("wasted_spend")).ToArray();
        if (wasted.Length > 0)
            list.Add(new Finding
            {
                Id = "wasted-spend", Category = "budget", Severity = wasted.Sum(e => e.Now.Spend) >= cur.Spend * 0.15m ? "high" : "medium",
                Problem = $"{wasted.Length} campaign(s) spent without tracked installs", Segment = string.Join(", ", wasted.Select(e => e.CampaignName)), ComparisonPeriod = f.Range,
                Evidence = wasted.Select(e => new Evidence(e.CampaignName ?? e.CampaignId, M(e.Now.Spend, "money"), $"{e.Now.Clicks:N0} clicks, 0 installs", Label(e.Platform))).ToArray(),
                Hypothesis = "Either the campaign is not converting, or its installs are not tracked (wrong conversion action / missing SDK event).",
                Confidence = "medium",
                Action = "Verify install tracking for these campaigns first; if tracking is fine, pause them or cut budget.",
                ExpectedImpact = $"Up to {M(wasted.Sum(e => e.Now.Spend), "money")} per period could be redirected.",
                Validation = ["Confirm the conversion action/app event is attached to the campaign.", "Check GA4 new users by campaign for these names."],
            });
        // A campaign in its first days is still in the platform's learning phase: its CPI is not yet comparable.
        static bool Learning(EntityRow e) => e.Prev.Spend == 0 && e.SpendTrend.Count(t => t.Value > 0) < LearningDays;
        foreach (var e in entities.Where(e => e.Flags.Contains("high_cpi") && !Learning(e)).Take(3))
            list.Add(new Finding
            {
                Id = $"high-cpi-{e.CampaignId}", Category = "campaign", Severity = "medium",
                Problem = $"High CPI: {e.CampaignName}", Segment = e.CampaignName, ComparisonPeriod = f.Range,
                Evidence = [new("CPI", M(e.Now.Cpi, "money"), $"median {M(Stats.Median(entities.Where(x => x.Now.Installs >= 5 && x.Now.Cpi is not null).Select(x => x.Now.Cpi!.Value)), "money")}", Label(e.Platform)), new("Spend", M(e.Now.Spend, "money"), $"{e.SpendShare}% of spend", Label(e.Platform))],
                Hypothesis = "Targeting, bidding or creative mix is less efficient than the account's other campaigns.",
                Confidence = e.Now.Installs >= 30 ? "high" : "medium",
                Action = "Lower its budget or bid target, or move budget to the most efficient campaign (see Budget).",
                Validation = ["CPI converges toward the median within 7 days of the change."],
            });
        foreach (var e in entities.Where(e => e.Flags.Contains("poor_quality")))
            list.Add(new Finding
            {
                Id = $"quality-{e.CampaignId}", Category = "retention", Severity = "medium",
                Problem = $"Low-quality users from {e.CampaignName}", Segment = e.CampaignName, ComparisonPeriod = f.Range,
                Evidence = [new("D1 retention", M(e.D1, "%"), "project average shown in Retention", "GA4 cohorts by first-user campaign"), new("Cohort users", $"{e.CohortUsers:N0}", null, "GA4")],
                Hypothesis = "The campaign reaches people who install but are not interested in the game (broad targeting, misleading creative, or incentivized placements).",
                Confidence = e.CohortUsers >= 100 ? "high" : "medium",
                Action = "Do not scale this campaign. Review its placements/audiences and whether creatives match real gameplay.",
                Validation = ["Compare D1/D7 of this campaign's cohorts with the project average for 2 more weeks."],
            });
        string[]? gaCampaigns = null;
        foreach (var e in entities.Where(e => e.Flags.Contains("investigate_before_scaling") && e.Now.Installs >= 5).Take(2))
        {
            var evidence = new List<Evidence> { new("CPI", M(e.Now.Cpi, "money"), null, Label(e.Platform)), new("Installs", $"{e.Now.Installs:0}", null, Label(e.Platform)) };
            if (e.CohortUsers is null && e.Now.Installs >= 20)
            {
                // Enough installs, but no retention for them: the campaign's players cannot be found in GA4/Adjust.
                // Waiting for more installs will not fix that; linking the attribution will.
                gaCampaigns ??= data.Cohorts(projectId, "campaign", f.Start, f.End).GroupBy(c => c.SegmentValue)
                    .OrderByDescending(g => g.Where(c => c.DayN == 0).Sum(c => c.CohortUsers)).Select(g => g.Key).Take(4).ToArray();
                evidence.Add(new("GA4 first-user campaigns", gaCampaigns.Length == 0 ? "none recorded" : string.Join(", ", gaCampaigns), $"no \"{e.CampaignName}\"", "GA4"));
                list.Add(new Finding
                {
                    Id = $"unattributed-{e.CampaignId}", Category = "tracking", Severity = "medium",
                    Problem = $"No retention data for {e.CampaignName}: its players are not attributed", Segment = e.CampaignName, ComparisonPeriod = f.Range,
                    Evidence = evidence,
                    Hypothesis = e.Platform == Integrations.GoogleAds
                        ? "Google Ads is not linked to the game's Firebase project, so GA4 does not record this campaign as the players' first-user campaign."
                        : "Without an attribution SDK (Adjust) the game's analytics cannot tell which players came from this campaign.",
                    Confidence = "high",
                    Action = e.Platform == Integrations.GoogleAds
                        ? "Link Google Ads to Firebase (Firebase console → Project settings → Integrations → Google Ads), so this campaign's D1/D7 can be measured before scaling it."
                        : "Connect Adjust (or another MMP) for this game, so this campaign's D1/D7 can be measured before scaling it.",
                    Validation = ["Its row in Retention (segment: campaign) shows cohort users a few days after the link."],
                });
                continue;
            }
            evidence.Add(new("D1", M(e.D1, "%"), e.CohortUsers is null ? "no GA4 cohort match" : $"{e.CohortUsers} users", "GA4"));
            list.Add(new Finding
            {
                Id = $"investigate-{e.CampaignId}", Category = "campaign", Severity = "low",
                Problem = $"Investigate before scaling: {e.CampaignName}", Segment = e.CampaignName, ComparisonPeriod = f.Range,
                Evidence = evidence,
                Hypothesis = "Cheap installs are only valuable if those users stay; retention evidence is missing or below average.",
                Confidence = "low",
                Action = e.Now.Installs < 20 ? $"Keep the budget steady until it has ≥20 installs (now {e.Now.Installs:0}) and a matured D1/D7 cohort."
                    : $"Keep the budget steady: D1 of its players is {M(e.D1, "%")} ({e.CohortUsers} players), below the project's; scale only once it matches.",
                Validation = ["Check this campaign's row in Retention (segment: campaign)."],
            });
        }

        // ---- retention
        var curve = RetentionAnalytics.Curve(data.Cohorts(projectId, "all", f.Start, f.End), today);
        var prevCurve = RetentionAnalytics.Curve(data.Cohorts(projectId, "all", f.Previous().Start, f.Previous().End), today);
        foreach (var d in new[] { 1, 7 })
        {
            var now = curve[d]; var before = prevCurve[d];
            var target = TargetFor(targets, $"d{d}", f)?.Target;
            var dropped = now.Percent is not null && before.Percent is not null && now.CohortUsers >= 50 && before.CohortUsers >= 50 && Stats.PctChange(now.Percent, before.Percent) <= -15;
            var belowTarget = now.Percent is not null && target is not null && now.Percent < target * 0.9m && now.CohortUsers >= 30;
            if (!dropped && !belowTarget) continue;
            list.Add(new Finding
            {
                Id = $"retention-d{d}", Category = "retention", Severity = d == 1 ? "high" : "medium",
                Problem = dropped ? $"D{d} retention dropped {Stats.PctChange(now.Percent, before.Percent):0}%" : $"D{d} retention below target",
                ComparisonPeriod = cmp,
                Evidence =
                [
                    new($"D{d} retention", M(now.Percent, "%"), dropped ? $"previous {M(before.Percent, "%")}" : $"target {M(target, "%")}", "GA4 cohorts (mature only)"),
                    new("Cohort users", $"{now.CohortUsers:N0}", $"{now.Cohorts} cohorts", "GA4"),
                ],
                Hypothesis = d == 1
                    ? "Day-1 losses usually come from the first session: onboarding friction, difficulty, performance, or a change in acquisition mix (new country/campaign)."
                    : "Day-7 losses point to the mid-game loop: content depth, difficulty spikes, lack of goals/rewards.",
                Confidence = now.CohortUsers >= 200 ? "high" : "medium",
                Action = d == 1 ? D1Action(p, projectId, f, now.Percent, target, today) : "Look at level progression events for where players stop; test difficulty or reward changes.",
                Validation = ["Compare the curve by app version / campaign in Retention.", "Re-check once the newest cohorts mature (they are excluded until then)."],
            });
        }

        // ---- funnel: tutorial
        var events = data.Ga4Events(projectId, f.Start, f.End).GroupBy(e => e.EventName).ToDictionary(g => g.Key, g => g.Sum(x => x.Users ?? 0));
        var prevEvents = data.Ga4Events(projectId, f.Previous().Start, f.Previous().End).GroupBy(e => e.EventName).ToDictionary(g => g.Key, g => g.Sum(x => x.Users ?? 0));
        var tutorial = p.TutorialEvent ?? "tutorial_complete";
        if (events.TryGetValue("first_open", out var opens) && opens >= 30 && events.TryGetValue(tutorial, out var done))
        {
            var rate = Math.Round(done * 100m / opens, 1);
            decimal? prevRate = prevEvents.TryGetValue("first_open", out var po) && po >= 30 && prevEvents.TryGetValue(tutorial, out var pd) ? Math.Round(pd * 100m / po, 1) : null;
            if (rate < 60 || prevRate is not null && rate < prevRate * 0.85m)
            {
                // Walk the onboarding steps in order and find where the most new players are lost, so the advice
                // points at that step instead of assuming the tutorial is too long.
                var path = (p.NuxEvents is { Count: > 0 } nux
                        ? nux.Select(s => (Event: s.Event, Label: s.Label ?? s.Event))
                        : new[] { (p.TutorialStart, p.TutorialStart), ("tutorial_step", "tutorial_step"), (tutorial, tutorial) })
                    .Where(s => events.ContainsKey(s.Event)).DistinctBy(s => s.Event).ToList();
                if (!path.Any(s => s.Event == tutorial)) path.Add((tutorial, tutorial));
                var losses = new List<(string From, string To, long Lost, long Base)>();
                var (prevLabel, prevUsers) = ("first_open", opens);
                foreach (var s in path)
                {
                    var u = events[s.Event];
                    losses.Add((prevLabel, s.Label, Math.Max(0, prevUsers - u), prevUsers));
                    (prevLabel, prevUsers) = (s.Label, u);
                }
                var worst = losses.MaxBy(l => l.Lost);
                var worstPct = worst.Base > 0 ? Math.Round(worst.Lost * 100m / worst.Base, 0) : 0;
                var beforeTutorial = worst.From == "first_open";
                var genericStep = !beforeTutorial && (worst.To == "tutorial_step" || worst.From == "tutorial_step") && p.NuxEvents is not { Count: > 0 };
                list.Add(new Finding
                {
                    Id = "funnel-tutorial", Category = "funnel", Severity = rate < 40 ? "high" : "medium",
                    Problem = $"Only {rate}% of new players complete the tutorial", ComparisonPeriod = prevRate is null ? f.Range : cmp,
                    Evidence = losses.Select(l => new Evidence($"{l.From} → {l.To}", $"{l.Lost:N0} players lost",
                            l.Base > 0 ? $"{l.Lost * 100m / l.Base:0.#}% of {l.Base:N0}" : null, "GA4"))
                        .Append(new Evidence($"{tutorial} users", done.ToString("N0"), prevRate is null ? $"{rate}% of first opens" : $"{rate}% of first opens, previous {prevRate}%", "GA4"))
                        .ToArray(),
                    Hypothesis = beforeTutorial
                        ? $"The largest loss is before the tutorial starts: {worst.Lost:N0} of {opens:N0} new players ({worstPct}%) open the game but never reach {worst.To}. The tutorial's length cannot cause that; something before it does (loading time, a crash, or a screen or prompt shown first)."
                        : $"The largest loss is between {worst.From} and {worst.To}: {worst.Lost:N0} of {worst.Base:N0} players ({worstPct}%) stop there.",
                    Confidence = opens >= 200 ? "medium" : "low",
                    Action = beforeTutorial
                        ? $"Find out why {worstPct}% of new players never reach {worst.To}: check loading time and crashes on first launch, and remove or delay anything shown before it (permission or consent prompts, splash screens)."
                        : genericStep
                            ? $"Find the exact tutorial step where players stop between {worst.From} and {worst.To}: tutorial_step has no step number, so log the step number as a parameter or add each step as its own event in Project settings → first-time player journey."
                            : $"Look at what players must do between {worst.From} and {worst.To} and make that step clearer or easier; {worstPct}% of players stop there.",
                    ExpectedImpact = "Recovering players at the biggest drop is the largest single lever on D1 retention.",
                    Validation = ["Compare the same steps for the next app version (Funnel page).", "D1 retention of the next cohorts (Retention → app version)."],
                });
            }
        }

        // ---- revenue
        var gaDays = data.Ga4Daily(projectId, f.Start, f.End);
        if (gaDays.Sum(d => d.ActiveUsers ?? 0) >= 300 && gaDays.Sum(d => d.TotalRevenue ?? 0) == 0)
            list.Add(new Finding
            {
                Id = "revenue-missing", Category = "revenue", Severity = "medium",
                Problem = "No revenue recorded in GA4", ComparisonPeriod = f.Range,
                Evidence = [new("User-days", gaDays.Sum(d => d.ActiveUsers ?? 0).ToString("N0"), null, "GA4"), new("Revenue", "0", null, "GA4"), new("ad_impression users", events.GetValueOrDefault("ad_impression").ToString("N0"), null, "GA4")],
                Hypothesis = "Ad revenue is not reaching GA4 (AdMob not linked to Firebase, or ad_impression events logged without value), or the game is not monetizing yet.",
                Confidence = "high", Action = "Link AdMob to Firebase or log ad_impression with value/currency from the mediation SDK, so LTV and ROAS can be measured.",
                ExpectedImpact = "Enables ROAS, LTV and budget decisions based on value instead of CPI alone.",
                Validation = ["GA4 shows non-zero totalAdRevenue the day after the change."],
            });

        // ---- ROAS target
        var roasTarget = TargetFor(targets, "roas_d7", f)?.Target;
        var ltv = LtvEstimate(curve, ga.Arpdau);
        if (roasTarget is not null && cur.Cpi is > 0 && ltv.D7 is not null)
        {
            var roas = Math.Round(ltv.D7.Value * 100m / cur.Cpi.Value, 1);
            if (roas < roasTarget * 0.8m)
                list.Add(new Finding
                {
                    Id = "roas-d7", Category = "roas", Severity = "high", Problem = $"Estimated D7 ROAS {roas}% vs target {roasTarget}%", ComparisonPeriod = f.Range,
                    Evidence = [new("Estimated D7 LTV", M(ltv.D7, "money"), ltv.Method, "GA4"), new("CPI", M(cur.Cpi, "money"), null, "Ad platforms")],
                    Hypothesis = "Either acquisition costs are too high for current monetization, or monetization/retention is too low for this CPI.",
                    Confidence = "low", Action = "Improve D1-D7 retention and ARPDAU before scaling; shift budget to cheaper, better-retaining countries.",
                    Validation = ["Recompute after 2 weeks with matured cohorts."],
                });
        }

        // ---- creatives
        var scored = CreativeIntel.Score(CreativeItems(p, f, rows, currency), today);
        var fatigued = scored.Where(s => s.Fatigued && s.Item.Format != "text").ToArray();
        if (fatigued.Length > 0)
            list.Add(new Finding
            {
                Id = "creative-fatigue", Category = "creative", Severity = "medium", Problem = $"{fatigued.Length} creative(s) show fatigue", ComparisonPeriod = f.Range,
                Segment = string.Join(", ", fatigued.Select(x => x.Item.Name).Take(5)),
                Evidence = fatigued.Take(5).Select(x => new Evidence(x.Item.Name, x.FatigueEvidence ?? "", null, Label(x.Item.Platform))).ToArray(),
                Hypothesis = "The audience has seen these creatives repeatedly; response falls as frequency rises.",
                Confidence = "medium", Action = "Launch refreshed versions (new first 3 seconds) and lower budget on the fatigued ones.",
                Validation = ["New creative's CTR/IPM matches the fatigued creative's early performance."],
            });
        var losers = scored.Where(s => s.Classification == "underperforming").ToArray();
        if (losers.Length > 0)
            list.Add(new Finding
            {
                Id = "creative-cut", Category = "creative", Severity = "low", Problem = $"{losers.Length} creative(s) to stop spending on", ComparisonPeriod = f.Range,
                Segment = string.Join(", ", losers.Select(x => x.Item.Name).Take(5)),
                Evidence = losers.Take(5).Select(x => new Evidence(x.Item.Name, $"score {x.Score}", x.Reasons.FirstOrDefault(), Label(x.Item.Platform))).ToArray(),
                Hypothesis = "These creatives have spent enough to judge and convert well below the median creative.",
                Confidence = losers.All(x => x.Confidence != "low") ? "medium" : "low",
                Action = "Pause them (or remove the assets) and move the budget to the best creative's variations.",
                Validation = ["Blended CPI of the campaign drops after removal."],
            });

        // ---- countries
        var countryView = CountryRows(p, f, currency);
        foreach (var c in countryView.Where(c => c.Flags.Contains("growth_opportunity")).Take(3))
            list.Add(new Finding
            {
                Id = $"country-{c.Country}", Category = "country", Severity = "info", Problem = $"Growth opportunity: {c.Country}", Segment = c.Country, ComparisonPeriod = f.Range,
                Evidence = [new("CPI", M(c.Perf.Cpi, "money"), $"overall {M(cur.Cpi, "money")}", "Ad platforms"), new("Installs", $"{c.Perf.Installs:0}", null, "Ad platforms"), new("D1", M(c.D1, "%"), null, "GA4")],
                Hypothesis = "Users in this country are cheaper to acquire without worse early retention.",
                Confidence = c.Perf.Installs >= 30 ? "medium" : "low",
                Action = $"Test a dedicated {c.Country} campaign or raise its share by 20-30%, then re-check CPI and D7.",
                Validation = ["CPI stays below the overall CPI at the higher budget.", "D7 for the country is at least the project average."],
            });

        // ---- anomalies on the last complete day
        var lastDay = today.AddDays(-1);
        var recentRows = rows.Now.Concat(rows.Prev).Where(r => r.Date >= lastDay.AddDays(-14) && r.Date <= lastDay).ToArray();
        // A campaign that first spent on the last day explains its own jump; it is a launch, not an anomaly.
        var launched = recentRows.Where(r => (r.Spend ?? 0) > 0).GroupBy(r => r.CampaignId).Where(g => g.Min(r => r.Date) == lastDay).Select(g => g.Key).ToHashSet();
        var window = recentRows.Where(r => !launched.Contains(r.CampaignId)).GroupBy(r => r.Date).ToDictionary(g => g.Key, g => Perf.Of(g));
        if (window.ContainsKey(lastDay) && window.Count >= 8)
        {
            foreach (var (name, sel) in new (string, Func<Perf, decimal?>)[] { ("Spend", x => x.Spend), ("Installs", x => x.Installs), ("CPI", x => x.Cpi) })
            {
                var hist = window.Where(kv => kv.Key < lastDay).Select(kv => sel(kv.Value)).Where(v => v is not null).Select(v => (double)v!.Value).ToArray();
                var last = sel(window[lastDay]);
                if (hist.Length < 7 || last is null) continue;
                var mean = hist.Average(); var sd = Math.Sqrt(hist.Sum(x => (x - mean) * (x - mean)) / (hist.Length - 1));
                if (sd <= 0) continue;
                var z = ((double)last.Value - mean) / sd;
                if (Math.Abs(z) < 2.5) continue;
                list.Add(new Finding
                {
                    Id = $"anomaly-{name.ToLowerInvariant()}", Category = "anomaly", Severity = "medium",
                    Problem = $"Unusual {name.ToLowerInvariant()} on {Db.Iso(lastDay)}", ComparisonPeriod = $"{Db.Iso(lastDay)} vs previous {hist.Length} days",
                    Evidence = [new(name, $"{last:0.##}", $"typical {mean:0.##} ± {sd:0.##} (z = {z:0.0})", "Ad platforms")],
                    Hypothesis = z > 0 ? "A budget change, a new campaign, or an auction shift." : "A paused campaign, budget cap, disapproval, or tracking outage.",
                    Confidence = "medium", Action = "Check the campaign change history in the ad platform for that day.",
                    Validation = ["See whether the next day returns to the typical range."],
                });
            }
        }

        // ---- KPI targets not met
        foreach (var t in targets.Where(t => t.ScopeType == "all" && t.Metric is "cpi" or "ctr" or "cpm" or "cpc" or "ipm"))
        {
            decimal? v = t.Metric switch { "cpi" => cur.Cpi, "ctr" => cur.Ctr, "cpm" => cur.Cpm, "cpc" => cur.Cpc, _ => cur.Ipm };
            if (v is null) continue;
            var higher = ProjectStore.TargetMetrics[t.Metric].HigherIsBetter;
            var achievement = higher ? v.Value / t.Target : t.Target / v.Value;
            if (achievement >= 0.9m || list.Any(x => x.Id == "cpi-up" && t.Metric == "cpi")) continue;
            list.Add(new Finding
            {
                Id = $"target-{t.Metric}", Category = "target", Severity = achievement < 0.7m ? "high" : "low",
                Problem = $"{ProjectStore.TargetMetrics[t.Metric].Label} is off target ({achievement * 100:0}% of target)", ComparisonPeriod = f.Range,
                Evidence = [new(ProjectStore.TargetMetrics[t.Metric].Label, $"{v:0.##}", $"target {t.Target:0.##}", "Ad platforms")],
                Hypothesis = "See the campaign and creative findings for the drivers.", Confidence = "high",
                Action = "Work through the campaign/creative findings; revisit the target if it was set without data.",
                Validation = ["KPI card shows ≥ 90% achievement."],
            });
        }

        return list.OrderBy(x => Array.IndexOf(SeverityOrder, x.Severity)).ToArray();
    }

    internal (long Paid, long Organic, long Unattributed) OrganicVsPaidNumbers(string projectId, Filters f)
    {
        var rows = data.Ga4Dims(projectId, f.Start, f.End, "channel");
        long K(string k) => rows.Where(r => ChannelKind(r.Value) == k).Sum(r => r.NewUsers ?? 0);
        return (K("paid"), K("organic"), K("unattributed"));
    }

    internal sealed record CountryRow(string Country, Perf Perf, decimal? D1, IReadOnlyList<string> Flags);

    internal IReadOnlyList<CountryRow> CountryRows(Project p, Filters f, string? currency)
    {
        var today = Today(p);
        var now = data.Countries(p.Id, f with { Country = null }).Where(r => r.Currency == currency).ToArray();
        var overall = Perf.Of(now);
        var cells = data.Cohorts(p.Id, "country", f.Start, f.End);
        var overallD1 = RetentionAnalytics.Curve(data.Cohorts(p.Id, "all", f.Start, f.End), today)[1].Percent;
        return now.GroupBy(r => r.Country).Select(g =>
        {
            var perf = Perf.Of(g);
            var d1 = RetentionAnalytics.Curve(cells.Where(c => c.SegmentValue == g.Key), today)[1].Percent;
            var flags = new List<string>();
            if (perf.Installs >= 5 && overall.Cpi is not null && perf.Cpi <= overall.Cpi * 0.8m && (d1 is null || overallD1 is null || d1 >= overallD1 * 0.9m)) flags.Add("growth_opportunity");
            return new CountryRow(g.Key, perf, d1, flags);
        }).ToArray();
    }

    internal IReadOnlyList<CreativeItem> CreativeItems(Project p, Filters f, AdSets rows, string? currency)
    {
        var meta = data.Creatives(p.Id);
        var items = new List<CreativeItem>();
        foreach (var g in rows.Now.Where(r => r.Platform == Integrations.Meta).GroupBy(r => r.AdId))
        {
            var m = meta.FirstOrDefault(c => c.Platform == Integrations.Meta && c.AdId == g.Key);
            var first = g.OrderByDescending(x => x.Date).First();
            items.Add(new CreativeItem($"meta|{g.Key}", Integrations.Meta, m?.AssetId ?? g.Key, m?.Name ?? first.AdName ?? g.Key, m?.Format ?? "unknown",
                first.CampaignId, first.CampaignName, first.AdStatus, m?.ThumbnailUrl, m?.PreviewUrl, m?.DurationSeconds, m?.Title, m?.Body, null,
                g.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => Perf.Of(x)),
                g.Where(x => x.Reach > 0).Select(x => (decimal)(x.Impressions ?? 0) / x.Reach!.Value).DefaultIfEmpty().Average() is var fr && fr > 0 ? Math.Round(fr, 2) : null));
        }
        foreach (var g in data.Assets(p.Id, f with { Country = null }).Where(a => a.Currency == currency).GroupBy(a => (a.AssetId, a.FieldType)))
        {
            var m = meta.FirstOrDefault(c => c.Platform == Integrations.GoogleAds && c.AssetId == g.Key.AssetId && c.FieldType == g.Key.FieldType);
            items.Add(new CreativeItem($"google|{g.Key.AssetId}|{g.Key.FieldType}", Integrations.GoogleAds, g.Key.AssetId, m?.Name ?? $"Asset {g.Key.AssetId}",
                m?.Format ?? "unknown", g.First().CampaignId ?? "", rows.Now.FirstOrDefault(r => r.CampaignId == g.First().CampaignId)?.CampaignName, null, m?.ThumbnailUrl, m?.PreviewUrl, m?.DurationSeconds, m?.Title, m?.Body,
                m?.PerformanceLabel, g.GroupBy(x => x.Date).ToDictionary(x => x.Key, x => Perf.Of(x)), null) { FieldType = g.Key.FieldType });
        }
        return items;
    }
}
