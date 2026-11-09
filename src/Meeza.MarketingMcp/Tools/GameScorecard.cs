using System.Text.Json.Nodes;
using Meeza.Marketing.Analytics;
using Meeza.MarketingMcp.Benchmarks;

namespace Meeza.MarketingMcp.Tools;

public sealed record MetricGrade(string Metric, decimal? Value, long SampleUsers, string Grade, string Benchmark);

public sealed record Scorecard(
    string Verdict, string Headline, bool Provisional,
    IReadOnlyList<MetricGrade> Retention, IReadOnlyList<string> Reasons, IReadOnlyList<string> NextSteps);

/// <summary>
/// Soft-launch go / no-go rules, the way publishers judge a prototype: retention first (it says whether players
/// like the game), then whether lifetime value can beat the cost per install. Pure, so it is unit-tested.
/// </summary>
public static class GameScorecard
{
    public const long MinUsersForVerdict = 100;

    /// <summary>D1 below this is weak for any genre (rule of thumb used by publishers; the median game sits in the low 20s).</summary>
    public const decimal D1Floor = 25m;

    // Fallbacks match benchmarks.json (GameAnalytics 2026 report, 2025 data) in case the file changes.
    public static decimal D1Top25 => Bench("ret-ga-top25-d1", 30m);
    public static decimal D1Top10 => Bench("ret-ga-top10-d1", 40m);
    public static decimal D7Median => Bench("ret-ga-median-d7", 4m);
    public static decimal D7Top25 => Bench("ret-ga-top25-d7", 6.5m);
    public static decimal D7Top10 => Bench("ret-ga-top10-d7", 11.5m);
    public static decimal D30Median => Bench("ret-ga-median-d30", 0.74m);
    public static decimal D30Top25 => Bench("ret-ga-top25-d30", 1.7m);

    public static Scorecard Evaluate(decimal? d1, long d1Users, decimal? d7, long d7Users, decimal? d30, long d30Users)
    {
        var grades = new List<MetricGrade>
        {
            Grade("D1", d1, d1Users, D1Floor, D1Top25, D1Top10, $"weak < {D1Floor}%, top 25% ≥ {D1Top25}%, top 10% ≥ {D1Top10}%"),
            Grade("D7", d7, d7Users, D7Median, D7Top25, D7Top10, $"median {D7Median}%, top 25% ≥ {D7Top25}%, top 10% ≥ {D7Top10}%"),
            Grade("D30", d30, d30Users, D30Median, D30Top25, null, $"median {D30Median}%, top 25% ≥ {D30Top25}%"),
        };
        var reasons = new List<string>();
        var next = new List<string>();

        if (d1 is null || d1Users < MinUsersForVerdict)
        {
            reasons.Add(d1 is null
                ? "No install cohort has reached day 1 yet."
                : $"Only {d1Users} users have reached day 1; at least {MinUsersForVerdict} are needed before retention means anything.");
            next.Add($"Keep the campaign running until about {MinUsersForVerdict * 3} installs have had 7 days to mature, then check again.");
            next.Add("Make sure Firebase Analytics is in the live build so every install shows up (run check_analytics_tracking).");
            return new Scorecard("insufficient_data", "Too early to judge: not enough players have been around long enough.", true, grades, reasons, next);
        }

        var hasD7 = d7 is not null && d7Users >= MinUsersForVerdict;
        string verdict, headline;
        if (d1 < D1Floor || (hasD7 && d7 < D7Median))
        {
            verdict = "fix_the_game";
            headline = "Players are leaving too fast. Fix the first session before buying more installs.";
            if (d1 < D1Floor) reasons.Add($"D1 {d1}% is below the {D1Floor}% floor: most players don't come back the next day.");
            if (hasD7 && d7 < D7Median) reasons.Add($"D7 {d7}% is below the median game ({D7Median}%).");
            next.Add("Watch 5-10 new players try the game (or record sessions): find where they quit in the first 3 minutes.");
            next.Add("Check the tutorial and first levels: too hard, too slow, or unclear goal are the usual D1 killers.");
            next.Add("Check crashes and load time on low-end Android devices (play_get_android_vitals).");
            next.Add("Make sure the ad creative shows the real game; misleading ads bring players who leave on day 0.");
            next.Add("Keep spend minimal (test budget only) until D1 improves.");
        }
        else if (d1 >= D1Top10 && (!hasD7 || d7 >= D7Top10))
        {
            verdict = "strong";
            headline = "Retention is top-10% level. This game can work; the question is now monetisation and CPI.";
            reasons.Add($"D1 {d1}% is at or above the top-10% mark ({D1Top10}%).");
            if (hasD7) reasons.Add($"D7 {d7}% is at or above the top-10% mark ({D7Top10}%).");
            next.Add("Measure revenue per user (ARPDAU, ad + IAP) and compare D7/D30 LTV with CPI.");
            next.Add("Test higher-value countries (e.g. US/UK/DE) with a small budget to see CPI and monetisation there.");
            next.Add("Scale creatives that win on IPM; add new creative variants weekly.");
        }
        else if (d1 >= D1Top25 && (!hasD7 || d7 >= D7Top25))
        {
            verdict = "promising";
            headline = "Retention is top-25% level. Worth iterating and testing monetisation.";
            reasons.Add($"D1 {d1}% is at or above the top-25% mark ({D1Top25}%).");
            if (hasD7) reasons.Add($"D7 {d7}% is at or above the top-25% mark ({D7Top25}%).");
            next.Add("Improve mid-game (days 2-7): daily rewards, level variety, goals that bring players back.");
            next.Add("Track revenue in GA4 (AdMob link or ad_impression value) so LTV can be compared with CPI.");
            next.Add("Keep CPI tests going in 2-3 countries to find where installs are cheapest for this retention.");
        }
        else
        {
            verdict = "iterate";
            headline = "Average retention. It can work with improvements, but not yet worth scaling.";
            reasons.Add($"D1 {d1}% is between the {D1Floor}% floor and the top-25% mark ({D1Top25}%).");
            if (hasD7) reasons.Add($"D7 {d7}% vs top-25% mark {D7Top25}%.");
            next.Add("A/B test the first session (tutorial length, first 10 levels' difficulty curve).");
            next.Add("Add a reason to return tomorrow (daily challenge, streak, rewards).");
            next.Add("Re-measure with a fresh cohort of 300+ installs after each update.");
        }

        var provisional = !hasD7;
        if (provisional) reasons.Add("D7 isn't available for enough users yet, so this verdict is provisional.");
        return new Scorecard(verdict, headline, provisional, grades, reasons, next);
    }

    private static MetricGrade Grade(string metric, decimal? value, long users, decimal low, decimal good, decimal? great, string benchmark)
    {
        var grade = value is null || users < MinUsersForVerdict ? "not_enough_data"
            : great is not null && value >= great ? "excellent"
            : value >= good ? "good"
            : value >= low ? "average"
            : "weak";
        return new MetricGrade(metric, value, users, grade, benchmark);
    }

    private static decimal Bench(string id, decimal fallback)
    {
        try
        {
            var m = (IndustryBenchmarks.All["metrics"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => (string?)x["id"] == id);
            return m?["value"] is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : fallback;
        }
        catch { return fallback; }
    }

    /// <summary>Lifetime value estimate = ARPDAU × expected active days from the fitted retention curve.</summary>
    public static decimal? Ltv(decimal? arpdau, RetentionFit? fit, int horizon) =>
        arpdau is null || fit is null ? null : Math.Round(arpdau.Value * (decimal)RetentionMath.ExpectedActiveDays(fit, horizon), 4);
}
