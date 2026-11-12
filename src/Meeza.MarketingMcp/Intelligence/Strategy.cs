using System.Text.Json.Nodes;
using Meeza.MarketingMcp.Benchmarks;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>One line of the health check: the project's value against its target or a sourced published benchmark.</summary>
/// <param name="Status">good | watch | bad | info | unknown</param>
public sealed record HealthCheck(string Key, string Label, string Value, string? Reference, string? ReferenceSource, string Status, string Note);

/// <summary>A prioritised marketing move. "Why" cites the project's own numbers; practices cite their published source.</summary>
public sealed record StrategyMove(
    int Priority, string Area, string Title, string Why, IReadOnlyList<string> Steps, string Kpi, string Horizon,
    string? Practice, string? PracticeSource, IReadOnlyList<string> FindingIds);

public sealed record BenchmarkSource(string Id, string Name, string? Url);

public sealed record StrategyPlan(
    string Stage, string StageLabel, string Verdict, string Range, IReadOnlyList<HealthCheck> Health, IReadOnlyList<StrategyMove> Moves,
    IReadOnlyList<Finding> Problems, IReadOnlyList<string> Caveats, IReadOnlyList<BenchmarkSource> Sources);

/// <summary>
/// Turns the project's data into a diagnosis and a prioritised marketing plan. Deterministic: every statement comes
/// from stored data, the project's own targets, or the sourced benchmark snapshot (cited). Nothing is changed in any
/// ad account; moves are proposals.
/// </summary>
public sealed partial class AnalyticsEngine
{
    /// <summary>Minimum mature cohort users before retention is used to judge the game (sample-size rule, not a benchmark).</summary>
    internal const int MinRetentionUsers = 100;

    public StrategyPlan Strategy(string projectId, Filters f)
    {
        var p = projects.Require(projectId);
        var today = Today(p);
        var targets = projects.Targets(projectId);
        var notes = new List<string>();
        var (currency, rows, _) = AdRowsInPrimaryCurrency(p, f with { Country = null }, notes);
        var cur = Perf.Of(rows.Now);
        var prev = Perf.Of(rows.Prev);
        var status = Status(p).ToArray();
        var findings = Findings(projectId, f);
        var ga = LoadGa4(projectId, f);
        var gaCurrency = ga.Days.Select(d => d.Currency).FirstOrDefault(c => c is not null);
        var curve = RetentionAnalytics.Curve(data.Cohorts(projectId, "all", f.Start, f.End), today);
        var (d1, d7) = (curve[1], curve[7]);
        var events = data.Ga4Events(projectId, f.Start, f.End).GroupBy(e => e.EventName).ToDictionary(g => g.Key, g => g.Sum(x => x.Users ?? 0));
        var tutorialEvent = p.TutorialEvent ?? "tutorial_complete";
        var opens = events.GetValueOrDefault(p.FirstOpen); var done = events.GetValueOrDefault(tutorialEvent);
        decimal? tutorialRate = opens > 0 && events.ContainsKey(tutorialEvent) ? Math.Round(done * 100m / opens, 1) : null;
        var org = OrganicVsPaidNumbers(projectId, f);
        var newUsers = org.Paid + org.Organic + org.Unattributed;
        var userDays = ga.Days.Sum(d => d.ActiveUsers ?? 0);
        var platformsWithSpend = rows.Now.Where(r => r.Spend > 0).Select(r => r.Platform).Distinct().ToArray();
        var scored = CreativeIntel.Score(CreativeItems(p, f, rows, currency), today);
        var activeCreatives = scored.Count(s => s.Total.Spend > 0);
        var countryInfo = CountryRows(p, f, currency);
        var targetCountries = (p.TargetCountries ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var spendCountries = countryInfo.Where(c => c.Perf.Spend > 0).Select(c => c.Country).ToHashSet();
        var organicDemand = data.Ga4Dims(projectId, f.Start, f.End, "country").GroupBy(r => r.Value)
            .Select(g => (Country: g.Key, Users: g.Sum(x => x.NewUsers ?? 0)))
            .Where(x => x.Users > 0 && !spendCountries.Contains(x.Country) && x.Country is not "(not set)")
            .OrderByDescending(x => x.Users).Take(3).ToArray();
        string Money(decimal? v) => v is null ? "n/a" : $"{currency} {v:N2}";
        string Pct(decimal? v) => v is null ? "n/a" : $"{v:0.#}%";
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? Src(string? id) { if (id is not null) used.Add(id); return id; }

        // ---------------------------------------------------------------- health check
        var health = new List<HealthCheck>();
        var core = status.Where(s => s.Integration is Integrations.GoogleAds or Integrations.Ga4).ToArray();
        var broken = status.Where(s => s.Freshness is "never" or "error" or "stale").ToArray();
        health.Add(new("data", "Data connections", $"{status.Length - broken.Length} of {status.Length} current",
            null, null, broken.Any(b => core.Contains(b)) ? "bad" : broken.Length > 0 ? "watch" : status.Length == 0 ? "bad" : "good",
            broken.Length == 0 ? "All connected sources are synchronized." : "Not current: " + string.Join(", ", broken.Select(b => $"{Label(b.Integration)} ({b.Freshness})"))));

        health.Add(new("install_tracking", "Install tracking", cur.Installs is null ? "missing" : $"{cur.Installs:N0} installs", null, null,
            rows.Now.Count == 0 ? "unknown" : cur.Installs is null ? "bad" : "good",
            cur.Installs is null ? "Ad platforms report no installs, so CPI cannot be judged." : "Ad platforms report installs (platform-attributed)."));

        health.Add(RetentionCheck("d1", "Day-1 retention", d1, TargetFor(targets, "d1", f)?.Target,
            [("ret-adjust-avg-d1", "watch"), ("ret-ga-top25-d1", "good")], Src));
        health.Add(RetentionCheck("d7", "Day-7 retention", d7, TargetFor(targets, "d7", f)?.Target,
            [("ret-ga-median-d7", "watch"), ("ret-ga-top25-d7", "good")], Src));

        health.Add(new("tutorial", "Tutorial completion", tutorialRate is null ? "n/a" : $"{tutorialRate}%",
            null, null, tutorialRate is null ? "unknown" : "info",
            tutorialRate is null ? $"No '{tutorialEvent}' or {p.FirstOpen} events in GA4 for this period." : $"{done:N0} of {opens:N0} new players. No published benchmark is used; compare against your own previous versions."));

        var cpiTarget = TargetFor(targets, "cpi", f)?.Target;
        var cpiChange = Stats.PctChange(cur.Cpi, prev.Cpi);
        var (cpiRef, cpiRefSrc) = CpiBenchmark(p, currency);
        health.Add(new("cpi", "CPI", cur.Cpi is null ? "n/a" : $"{Money(cur.Cpi)}{(cpiChange is null ? "" : $" ({cpiChange:+0;-0}% vs previous)")}",
            cpiTarget is not null ? $"target {Money(cpiTarget)}" : cpiRef?.Text, cpiTarget is null ? Src(cpiRefSrc) : null,
            cur.Cpi is null ? "unknown"
                : cpiTarget is not null ? (cur.Cpi <= cpiTarget ? "good" : cur.Cpi <= cpiTarget * 1.2m ? "watch" : "bad")
                : cpiRef is not null ? (cur.Cpi <= cpiRef.Value ? "good" : cur.Cpi <= cpiRef.Value * 1.5m ? "watch" : "bad")
                : cpiChange >= 20 ? "bad" : cpiChange >= 10 ? "watch" : "info",
            cpiTarget is not null || cpiRef is not null ? "Compared with your target or a same-currency published benchmark."
                : $"Published CPI benchmarks are in USD and your spend is in {currency ?? "another currency"}; amounts are never converted, so CPI is judged on its trend. Set a CPI target in KPI targets."));

        var revenueTracked = ga.Revenue is > 0;
        var sameCurrency = gaCurrency is not null && currency is not null && string.Equals(gaCurrency, currency, StringComparison.OrdinalIgnoreCase);
        health.Add(new("monetization", "Revenue measurement",
            ga.Days.Count == 0 ? "no GA4 data" : revenueTracked ? $"{gaCurrency} {ga.Revenue:N2} in period" : "no revenue recorded", null, null,
            ga.Days.Count == 0 ? "unknown" : !revenueTracked && userDays >= 300 ? "bad" : !revenueTracked ? "watch" : !sameCurrency ? "watch" : "good",
            !revenueTracked ? "Without revenue in GA4, LTV and ROAS cannot be measured and budget decisions rest on CPI alone."
                : !sameCurrency ? $"GA4 reports revenue in {gaCurrency} while ad spend is in {currency}; ROAS stays unavailable until both use one currency."
                : "Revenue and spend use the same currency, so ROAS is measurable."));

        health.Add(new("organic", "Organic share of new users", newUsers == 0 ? "n/a" : $"{org.Organic * 100m / newUsers:0}% ({org.Organic:N0} of {newUsers:N0})",
            null, null, newUsers == 0 ? "unknown" : "info", "GA4 first-user channel. Organic users are free installs; store listing and ratings drive them."));

        var fatigued = scored.Count(s => s.Fatigued);
        health.Add(new("creatives", "Creatives in rotation", rows.Now.Count == 0 ? "n/a" : $"{activeCreatives} with spend{(fatigued > 0 ? $", {fatigued} fatigued" : "")}",
            null, null, rows.Now.Count == 0 ? "unknown" : fatigued > 0 ? "watch" : "info",
            "Top advertisers find winners through volume: most creatives lose, a few take most of the spend."));

        health.Add(new("channels", "Paid channels", platformsWithSpend.Length == 0 ? "none" : string.Join(" + ", platformsWithSpend.Select(Label)), null, null,
            platformsWithSpend.Length == 0 ? "unknown" : platformsWithSpend.Length == 1 ? "watch" : "good",
            platformsWithSpend.Length == 1 ? "All spend is on one platform: no comparison point and no fallback if its costs rise." : "More than one channel is running."));

        // ---------------------------------------------------------------- stage
        var check = health.ToDictionary(h => h.Key);
        var measurementBlocked = check["data"].Status == "bad" || check["install_tracking"].Status == "bad" || ga.Days.Count == 0;
        var retentionKnown = d1.Percent is not null && d1.CohortUsers >= MinRetentionUsers;
        var retentionBad = retentionKnown && (check["d1"].Status == "bad" || d7.CohortUsers >= MinRetentionUsers && check["d7"].Status == "bad");
        var costBad = check["cpi"].Status == "bad";
        var (stage, stageLabel, verdict) =
            measurementBlocked ? ("measure", "Fix measurement first", "Key data is missing or out of date, so decisions would rest on incomplete numbers. Repair tracking before changing budgets.")
            : !retentionKnown ? ("validate", "Validate the game", d1.Percent is null
                ? "There is no mature retention data for this period yet, so it is too early to judge whether the game keeps players. Collect a clean test sample before scaling."
                : $"Day-1 retention is based on only {d1.CohortUsers:N0} users, too few to judge the game. Collect a larger, clean test sample before scaling.")
            : retentionBad ? ("improve", "Improve the game before scaling", $"Players are not staying (D1 {Pct(d1.Percent)}{(d7.Percent is null ? "" : $", D7 {Pct(d7.Percent)}")}). More spend would buy users who leave; improve the first session and early game first.")
            : costBad ? ("optimize", "Lower acquisition cost", $"Retention is acceptable but CPI ({Money(cur.Cpi)}) is off target. Improve creatives and targeting before adding budget.")
            : ("scale", "Scale carefully", $"Retention{(d7.Percent is null ? "" : " and cost")} support growth (D1 {Pct(d1.Percent)}). Add budget in steps and watch CPI and D7 at each step.");

        // ---------------------------------------------------------------- moves
        var moves = new List<StrategyMove>();
        void Add(string area, string title, string why, IEnumerable<string> steps, string kpi, string horizon, string? practiceId = null, IEnumerable<string>? findingIds = null)
        {
            var practice = practiceId is null ? null : Playbook(practiceId);
            moves.Add(new StrategyMove(0, area, title, why, steps.ToArray(), kpi, horizon,
                practice?.Text, practice is null ? null : Src(practice.Source), (findingIds ?? []).Where(id => findings.Any(x => x.Id == id)).ToArray()));
        }

        // 1. measurement
        var measureSteps = new List<string>();
        foreach (var b in broken) measureSteps.Add($"{Label(b.Integration)} {b.ExternalId}: {(b.Freshness == "never" ? "has never synced" : b.Freshness)}{(b.LastError is null ? "" : $" ({Trim(b.LastError, 120)})")}. Fix it in Integrations, then Sync now.");
        if (cur.Installs is null && rows.Now.Count > 0) measureSteps.Add("Attach an install conversion (Google Ads: first_open from Firebase; Meta: app install event) to every campaign.");
        if (!revenueTracked && ga.Days.Count > 0) measureSteps.Add("Link AdMob to Firebase (or log ad_impression with value and currency from your mediation SDK) so revenue reaches GA4.");
        if (revenueTracked && !sameCurrency && currency is not null) measureSteps.Add($"Use one currency for GA4 revenue ({gaCurrency}) and ad spend ({currency}): change the GA4 property currency or the ad account currency so ROAS can be computed.");
        if (measureSteps.Count > 0)
            Add("Measurement", "Fix tracking so every decision is based on complete data",
                $"{measureSteps.Count} measurement gap(s) found. Conclusions about cost, retention and value depend on them.",
                measureSteps, "All sources 'fresh'; installs and revenue non-zero", "This week", null, ["revenue-missing", .. broken.Select(b => $"data-{b.Integration}-{b.ExternalId}")]);

        // 2. product / first session
        var tutorialFinding = findings.FirstOrDefault(x => x.Id == "funnel-tutorial");
        if (retentionBad || tutorialFinding is not null)
            Add("Game & onboarding", "Improve the first session to lift Day-1 retention",
                string.Join(" ", new[]
                {
                    d1.Percent is null ? null : $"D1 is {Pct(d1.Percent)} from {d1.CohortUsers:N0} users ({check["d1"].Reference}).",
                    tutorialRate is null ? null : $"{tutorialRate}% of new players finish the tutorial.",
                }.Where(x => x is not null)),
                [
                    "Find the step where new players leave (tutorial_step / level events in GA4) and shorten or simplify it.",
                    "Make the first win happen within the first minute; cut text and permission prompts before it.",
                    "Check crashes and load time on low-end Android devices in Play Console vitals before buying more traffic.",
                    "Release the change, then compare D1 of cohorts after the release with those before it (Retention page).",
                ],
                "D1 retention and tutorial completion", "Next 2-4 weeks", "Launch", ["retention-d1", "retention-d7", "funnel-tutorial"]);

        // 3. validation test
        if (stage == "validate")
        {
            var installsNeeded = 300; // rule of thumb for a first D1/D7 read with ~100+ mature users per metric
            var cost = cur.Cpi is > 0 ? $" At the current CPI that costs about {Money(cur.Cpi * installsNeeded)}." : "";
            Add("Testing", "Run a clean test to get a reliable retention read",
                $"Only {d1.CohortUsers:N0} users have a mature Day-1 result in {f.Range}. Decisions on scaling need a larger sample.{cost}",
                [
                    $"Buy about {installsNeeded} installs over 1-2 weeks in one or two representative countries{(targetCountries.Length > 0 ? $" (targets: {string.Join(", ", targetCountries.Take(3))})" : "")}, at a steady daily budget.",
                    "Keep creatives and targeting stable during the test so the result reflects the game, not the setup.",
                    "Judge D1 and D7 once cohorts are mature (D7 needs 9 days after the last install day).",
                    "Choose 30d or 90d in the date filter to include more cohorts in the read.",
                ],
                "Mature D1/D7 with ≥100 users", "Next 2 weeks", "Launch");
        }

        // 4. budget efficiency
        var wasted = findings.Where(x => x.Id == "wasted-spend" || x.Id.StartsWith("high-cpi-", StringComparison.Ordinal) || x.Id.StartsWith("quality-", StringComparison.Ordinal)).ToArray();
        if (wasted.Length > 0 || costBad)
            Add("Budget", "Move budget away from campaigns that waste it",
                wasted.Length > 0 ? string.Join(" ", wasted.Take(3).Select(x => x.Problem + ".")) : $"CPI is {Money(cur.Cpi)} ({check["cpi"].Reference ?? "trend"}).",
                [
                    .. wasted.Take(3).Select(x => $"{x.Problem}: {x.Action}"),
                    "Use Budget & forecast to see the expected installs for each reallocation before changing anything in the ad account.",
                ],
                "Blended CPI", "This week", null, wasted.Select(x => x.Id));

        // 5. creatives
        if (rows.Now.Count > 0)
        {
            var winners = scored.Where(s => s.Classification == "best").Take(2).Select(s => s.Item.Name).ToArray();
            Add("Creatives", "Test new creatives every week",
                $"{activeCreatives} creative(s) had spend in {f.Range}{(fatigued > 0 ? $", {fatigued} show fatigue" : "")}{(winners.Length > 0 ? $"; best: {string.Join(", ", winners)}" : "")}.",
                [
                    "Ship 3-5 new video variations per week; change the first 3 seconds (hook) rather than the whole ad.",
                    winners.Length > 0 ? $"Build variations of {winners[0]} (same core, new hook, new opening frame)." : "Find a first winner: test different hooks (satisfying solve, near-fail, challenge) on small budgets.",
                    "Show real gameplay; ads that misrepresent the game buy installs that churn on Day 1.",
                    "Pause creatives the Creatives page marks as underperforming; keep each test to a small budget.",
                ],
                "IPM and CPI of new creatives vs the current best", "Ongoing, weekly", "Produce creatives at industrial volume", ["creative-fatigue", "creative-cut"]);
        }

        // 6. countries
        var untested = targetCountries.Where(c => !spendCountries.Contains(c)).ToArray();
        var growth = countryInfo.Where(c => c.Flags.Contains("growth_opportunity")).Select(c => c.Country).ToArray();
        if (untested.Length > 0 || growth.Length > 0 || organicDemand.Length > 0)
            Add("Markets", "Expand country by country",
                string.Join(" ", new[]
                {
                    growth.Length > 0 ? $"Cheaper than average without worse D1: {string.Join(", ", growth)}." : null,
                    organicDemand.Length > 0 ? $"Organic demand without ad spend: {string.Join(", ", organicDemand.Select(o => $"{o.Country} ({o.Users:N0} new users)"))}." : null,
                    untested.Length > 0 ? $"Target countries with no spend yet: {string.Join(", ", untested)}." : null,
                }.Where(x => x is not null)),
                [
                    growth.Length > 0 ? $"Raise the share of {string.Join(", ", growth)} by 20-30% and re-check CPI and D7 after a week." : "Pick one new country at a time and give it its own campaign or budget.",
                    organicDemand.Length > 0 ? $"Test {organicDemand[0].Country} first: players already find the game there without ads." : "Start with cheaper markets to validate before expensive ones.",
                    "Keep a country only if its CPI and D7 are at least as good as the project average.",
                ],
                "CPI and D7 per country", "Next month", "Many ad groups, regional rollout", growth.Select(g => $"country-{g}"));

        // 7. channels
        if (platformsWithSpend.Length == 1 && !measurementBlocked)
        {
            var other = platformsWithSpend[0] == Integrations.GoogleAds ? Integrations.Meta : Integrations.GoogleAds;
            var otherLinked = p.Connections.Any(c => c.Integration == other);
            Add("Channels", $"Add a second channel ({Label(other)})",
                $"All spend in {f.Range} is on {Label(platformsWithSpend[0])}. A second channel gives a cost comparison and a fallback.",
                [
                    otherLinked ? $"{Label(other)} is linked to this project; make sure its connection is current, then run a small test." : $"Link a {Label(other)} account to this project in Project settings.",
                    "Start with 10-20% of the budget and the best creatives from the current channel.",
                    "Compare CPI and D7 by channel after two weeks (Retention → segment: channel).",
                ],
                "CPI and D7 by channel", "Next month", "Go beyond Meta and Google: AppLovin and in-app networks");
        }

        // 8. scale
        if (stage == "scale")
            Add("Scale", "Increase budget in steps",
                $"D1 {Pct(d1.Percent)}{(d7.Percent is null ? "" : $", D7 {Pct(d7.Percent)}")} and CPI {Money(cur.Cpi)} support more spend.",
                [
                    "Raise budgets on the most efficient campaigns by 20-30% at a time, at most every 3-4 days, so the algorithms re-stabilize.",
                    "Stop raising when CPI rises more than 20% or D7 of new cohorts falls below the project average.",
                    "Set KPI targets (CPI, D1, D7, ROAS) so every card shows how far each step is from them.",
                ],
                "CPI, D7 and (when measurable) ROAS at each step", "Next month", "Many ad groups, regional rollout");

        if (moves.Count == 0)
            Add("Monitoring", "Keep monitoring", "No rule found a gap in this period.", ["Review this page weekly.", "Set KPI targets so deviations are flagged."], "All KPIs", "Weekly");

        var order = new[] { "Measurement", "Game & onboarding", "Testing", "Budget", "Creatives", "Markets", "Channels", "Scale", "Monitoring" };
        var ordered = moves.OrderBy(m => Array.IndexOf(order, m.Area)).Select((m, i) => m with { Priority = i + 1 }).ToArray();

        var caveats = new List<string>
        {
            "Benchmarks are published industry figures (cited) and describe other games; your own targets take precedence when set.",
            "Root causes are hypotheses until validated; each move lists the metric that confirms it.",
            "Nothing is changed in any ad account. All moves are proposals.",
        };
        if (d1.Percent is not null && d1.CohortUsers < MinRetentionUsers)
            caveats.Insert(0, $"Retention samples are small ({d1.CohortUsers:N0} mature users for D1); treat retention-based statements as directional.");
        caveats.AddRange(notes);

        return new StrategyPlan(stage, stageLabel, verdict, f.Range, health, ordered, findings.Take(6).ToArray(), caveats, Sources(used));
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Ref(decimal Value, string Text, string Source);

    private static HealthCheck RetentionCheck(string key, string label, CurvePoint point, decimal? target, (string Id, string StatusAtOrAbove)[] refs, Func<string?, string?> src)
    {
        var bench = refs.Select(r => (Ref: Benchmark(r.Id), r.StatusAtOrAbove)).Where(x => x.Ref is not null).ToArray();
        var refText = target is not null ? $"target {target:0.#}%" : string.Join(" · ", bench.Select(b => b.Ref!.Text));
        var refSource = target is not null ? null : string.Join(", ", bench.Select(b => src(b.Ref!.Source)).Distinct());
        if (point.Percent is null)
            return new(key, label, "n/a", refText, refSource, "unknown", "No mature cohort reached this day in the selected period.");
        var value = $"{point.Percent:0.#}% ({point.CohortUsers:N0} user{(point.CohortUsers == 1 ? "" : "s")})";
        string status;
        if (target is not null) status = point.Percent >= target ? "good" : point.Percent >= target * 0.9m ? "watch" : "bad";
        else
        {
            status = "bad";
            foreach (var b in bench.OrderBy(b => b.Ref!.Value)) if (point.Percent >= b.Ref!.Value) status = b.StatusAtOrAbove;
        }
        var small = point.CohortUsers < MinRetentionUsers;
        var note = (status switch
        {
            "good" => target is not null ? "At or above your target." : "At or above the higher published reference.",
            "watch" => target is not null ? "Within 10% of your target." : "Between the two published references.",
            _ => target is not null ? "Below your target." : "Below the lower published reference.",
        }) + (small ? $" Small sample (<{MinRetentionUsers} users): directional only." : "");
        return new(key, label, value, refText, refSource, small ? "unknown" : status, note);
    }

    /// <summary>A CPI benchmark is only used when the spend currency matches the benchmark currency (amounts are never converted).</summary>
    private static (Ref? Ref, string? Source) CpiBenchmark(Project p, string? currency)
    {
        var benchCurrency = (string?)IndustryBenchmarks.All["currency"];
        if (currency is null || !string.Equals(currency, benchCurrency, StringComparison.OrdinalIgnoreCase)) return (null, null);
        var genre = (p.Genre ?? "").ToLowerInvariant();
        var casual = genre.Contains("puzzle") || genre.Contains("casual") || genre.Contains("sort") || genre.Contains("match");
        var android = (p.Platforms ?? "android").StartsWith("android", StringComparison.OrdinalIgnoreCase) && !(p.Platforms ?? "").Contains("ios");
        var r = Benchmark(casual ? android ? "cpi-casual-android" : "cpi-casual-ios" : "cpi-global");
        return (r, r?.Source);
    }

    private static Ref? Benchmark(string id)
    {
        var m = (IndustryBenchmarks.All["metrics"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => (string?)x["id"] == id);
        if (m is null || m["value"] is null) return null;
        var value = (decimal)m["value"]!;
        var unit = (string?)m["unit"];
        // Compact label, e.g. "27% Adjust gaming average" from segment "Adjust: gaming average (top-app sample)".
        var segment = ((string?)m["segment"] ?? id).Split('(')[0].Replace(":", "").Trim();
        var text = $"{value:0.##}{(unit == "%" ? "%" : " " + unit)} {segment}";
        return new Ref(value, text, (string?)m["source"] ?? "");
    }

    private sealed record PracticeRef(string Text, string Source);

    private static PracticeRef? Playbook(string practice)
    {
        var x = (IndustryBenchmarks.All["playbook"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(e => (string?)e["practice"] == practice || (string?)e["theme"] == practice);
        return x is null ? null : new PracticeRef($"{x["practice"]}: {x["detail"]}", (string?)x["source"] ?? "");
    }

    private static IReadOnlyList<BenchmarkSource> Sources(IEnumerable<string> ids)
    {
        var all = (IndustryBenchmarks.All["sources"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        return ids.Distinct().Select(id => all.FirstOrDefault(s => (string?)s["id"] == id))
            .Where(s => s is not null)
            .Select(s => new BenchmarkSource((string)s!["id"]!, (string?)s["name"] ?? (string?)s["title"] ?? (string)s["id"]!, (string?)s["url"]))
            .ToArray();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
