using System.Text.Json;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Tools;

namespace Meeza.Marketing.Tests;

public class RetentionMathTests
{
    private static readonly DateOnly Day0 = new(2026, 10, 1);

    [Fact]
    public void Blend_only_counts_cohorts_that_reached_the_day()
    {
        // Cohort A (Oct 1, 100 users) is old enough for D1 and D7; cohort B (Oct 8, 300 users) only for D1.
        var pts = new[]
        {
            new CohortPoint(Day0, 0, 100, 100), new CohortPoint(Day0, 1, 40, 100), new CohortPoint(Day0, 7, 10, 100),
            new CohortPoint(Day0.AddDays(7), 0, 300, 300), new CohortPoint(Day0.AddDays(7), 1, 60, 300),
        };
        var curve = RetentionMath.Blend(pts, lastCompleteDay: Day0.AddDays(10), maxDay: 7);

        Assert.Equal(25m, RetentionMath.On(curve, 1));          // (40 + 60) / 400
        Assert.Equal(400, RetentionMath.UsersOn(curve, 1));
        Assert.Equal(10m, RetentionMath.On(curve, 7));          // only cohort A: 10 / 100
        Assert.Equal(100, RetentionMath.UsersOn(curve, 7));
    }

    [Fact]
    public void Blend_returns_null_for_days_no_cohort_has_reached()
    {
        var curve = RetentionMath.Blend([new CohortPoint(Day0, 0, 50, 50), new CohortPoint(Day0, 1, 20, 50)], Day0.AddDays(2), 7);
        Assert.Null(RetentionMath.On(curve, 7));
        Assert.Equal(0, RetentionMath.UsersOn(curve, 7));
    }

    [Fact]
    public void Missing_rows_count_as_zero_active_for_matured_cohorts()
    {
        var curve = RetentionMath.Blend([new CohortPoint(Day0, 0, 50, 50)], Day0.AddDays(5), 3);
        Assert.Equal(0m, RetentionMath.On(curve, 3));
    }

    [Fact]
    public void Power_law_fit_recovers_the_curve()
    {
        // R(d) = 0.4 × d^-0.5  → D1 40%, D4 20%, D16 10%
        var days = new[] { (1, 40m), (4, 20m), (16, 10m) }.Select(x => new RetentionDay(x.Item1, x.Item2, 1000, 5)).ToArray();
        var fit = RetentionMath.Fit(days)!;
        Assert.Equal(0.4, fit.A, 3);
        Assert.Equal(-0.5, fit.B, 3);
        Assert.Equal(0.1, fit.At(16), 3);
        // 1 (day 0) + Σ 0.4/√d for d = 1..30
        var expected = 1 + Enumerable.Range(1, 30).Sum(d => 0.4 / Math.Sqrt(d));
        Assert.Equal(expected, RetentionMath.ExpectedActiveDays(fit, 30), 6);
    }

    [Fact]
    public void Fit_needs_two_points_with_enough_users()
    {
        Assert.Null(RetentionMath.Fit([new RetentionDay(1, 30m, 1000, 3)]));
        Assert.Null(RetentionMath.Fit([new RetentionDay(1, 30m, 10, 1), new RetentionDay(7, 8m, 10, 1)]));
    }
}

public class GameScorecardTests
{
    [Fact]
    public void Too_few_users_is_insufficient_data()
    {
        var c = GameScorecard.Evaluate(45m, 57, null, 0, null, 0);
        Assert.Equal("insufficient_data", c.Verdict);
        Assert.All(c.Retention, g => Assert.Equal("not_enough_data", g.Grade));
    }

    [Theory]
    [InlineData(18, null, "fix_the_game")]
    [InlineData(32, 2.0, "fix_the_game")]   // good D1, but D7 below the median game
    [InlineData(27, null, "iterate")]
    [InlineData(33, 7.0, "promising")]
    [InlineData(42, 12.0, "strong")]
    [InlineData(42, 7.0, "promising")]
    public void Verdict_follows_retention(double d1, double? d7, string verdict)
    {
        var c = GameScorecard.Evaluate((decimal)d1, 500, d7 is null ? null : (decimal)d7.Value, d7 is null ? 0 : 400, null, 0);
        Assert.Equal(verdict, c.Verdict);
        Assert.Equal(d7 is null, c.Provisional);
        Assert.NotEmpty(c.NextSteps);
    }

    [Fact]
    public void Ltv_is_arpdau_times_expected_active_days()
    {
        var fit = new RetentionFit(0.4, -0.5, 3);
        var ltv = GameScorecard.Ltv(0.02m, fit, 30);
        Assert.Equal(Math.Round(0.02m * (decimal)RetentionMath.ExpectedActiveDays(fit, 30), 4), ltv);
        Assert.Null(GameScorecard.Ltv(null, fit, 30));
        Assert.Null(GameScorecard.Ltv(0.02m, null, 30));
    }

    [Fact]
    public void Thresholds_come_from_the_benchmark_file()
    {
        Assert.Equal(30m, GameScorecard.D1Top25);
        Assert.Equal(40m, GameScorecard.D1Top10);
        Assert.Equal(4m, GameScorecard.D7Median);
    }
}

public class AnalyticsParsingTests
{
    [Theory]
    [InlineData("properties/123456789", "123456789")]
    [InlineData(" 123456789 ", "123456789")]
    public void Property_ids_are_normalized(string input, string expected) =>
        Assert.Equal(expected, AnalyticsOptions.NormalizePropertyId(input));

    [Theory]
    [InlineData("G-ABC123XYZ")]
    [InlineData("12")]
    public void Measurement_ids_and_junk_are_rejected(string input) =>
        Assert.Throws<ArgumentException>(() => AnalyticsOptions.NormalizePropertyId(input));

    [Fact]
    public void Report_rows_are_parsed_by_header_name()
    {
        const string json = """
            {"dimensionHeaders":[{"name":"date"}],"metricHeaders":[{"name":"activeUsers","type":"TYPE_INTEGER"},{"name":"newUsers","type":"TYPE_INTEGER"}],
             "rows":[{"dimensionValues":[{"value":"20261104"}],"metricValues":[{"value":"57"},{"value":"41"}]}],
             "metadata":{"currencyCode":"USD","timeZone":"Asia/Karachi","subjectToThresholding":true}}
            """;
        using var doc = JsonDocument.Parse(json);
        var r = AnalyticsClient.ParseReport(doc.RootElement);
        var row = Assert.Single(r.Rows);
        Assert.Equal(57m, r.Metric(row, "activeUsers"));
        Assert.Equal(41m, r.Metric(row, "newUsers"));
        Assert.Null(r.Metric(row, "sessions"));
        Assert.Equal(new DateOnly(2026, 11, 4), AnalyticsService.ParseGaDate(r.Dimension(row, "date")!));
        Assert.Equal("USD", r.CurrencyCode);
        Assert.True(r.SubjectToThresholding);
    }

    [Fact]
    public void Platform_filter_maps_names_and_rejects_unknown()
    {
        Assert.Null(AnalyticsService.PlatformFilter(null));
        Assert.Null(AnalyticsService.PlatformFilter("all"));
        Assert.Contains("Android", Json.Serialize(AnalyticsService.PlatformFilter("android")));
        Assert.Throws<ArgumentException>(() => AnalyticsService.PlatformFilter("windows"));
    }

    [Fact]
    public void Permission_errors_explain_how_to_grant_access()
    {
        var msg = AnalyticsClient.DescribeError(403, """{"error":{"message":"User does not have sufficient permissions for this property."}}""");
        Assert.Contains("Property access management", msg);
        var disabled = AnalyticsClient.DescribeError(403, """{"error":{"message":"Google Analytics Data API has not been used in project 1 before or it is disabled."}}""");
        Assert.Contains("Enable the Google Analytics Data API", disabled);
    }
}

public class DailyAnomalyTests
{
    private static IReadOnlyList<DailyPerformance> Series(params (decimal spend, decimal installs)[] days) =>
        days.Select((d, i) => new DailyPerformance("google_ads", "1", "PKR", new DateOnly(2026, 10, 1).AddDays(i), d.spend, 1000, 50, d.installs)).ToArray();

    [Fact]
    public void Steady_series_raises_nothing()
    {
        var s = Series(Enumerable.Range(0, 15).Select(i => (100m + i % 3, 20m + i % 2)).ToArray());
        Assert.Empty(Insights.DetectDailyAnomalies(s));
    }

    [Fact]
    public void Install_collapse_on_the_last_day_is_flagged()
    {
        var days = Enumerable.Range(0, 14).Select(i => (100m + i % 3, 20m + i % 2)).Append((100m, 4m)).ToArray();
        var alerts = Insights.DetectDailyAnomalies(Series(days));
        Assert.Contains(alerts, a => a.Rule == "daily_installs_drop" && a.Severity == "warning");
        Assert.Contains(alerts, a => a.Rule == "daily_cpi_spike" && a.Severity == "warning");
    }

    [Fact]
    public void Short_history_is_skipped()
    {
        Assert.Empty(Insights.DetectDailyAnomalies(Series((100m, 20m), (100m, 20m), (1000m, 1m))));
    }
}

public class ProgressionMathTests
{
    private static LevelEventRow Row(string level, string ev, long users, long events, string? success = null, string mode = "campaign") =>
        new(mode, level, ev, success, users, events);

    [Fact]
    public void Build_orders_levels_numerically_and_computes_reach_and_drop()
    {
        var rows = new[]
        {
            Row("(not set)", "first_open", 200, 200), Row("(not set)", "tutorial_complete", 120, 120),
            Row("10", "level_start", 20, 25), Row("10", "level_end", 10, 12, "1"), Row("10", "level_end", 15, 30, "0"),
            Row("1", "level_start", 100, 110), Row("1", "level_first_move", 95, 95), Row("1", "level_end", 90, 90, "1"), Row("1", "level_end", 5, 5, "0"),
            Row("2", "level_start", 80, 100), Row("2", "level_end", 60, 60, "1"), Row("2", "level_quit", 10, 10),
            Row("puzzle-1", "level_start", 7, 7, mode: "clear-board"),
        };
        var rep = ProgressionMath.Build("p", "r", rows, "campaign", thresholded: false, dataLoss: false);

        Assert.Equal(200, rep.NewUsers);
        Assert.Equal(120, rep.TutorialCompleted);
        Assert.Equal(["1", "2", "10"], rep.Levels.Select(l => l.Level).ToArray());

        var l1 = rep.Levels[0];
        Assert.Equal(100, l1.PlayersStarted);
        Assert.Equal(1.1m, l1.AttemptsPerPlayer);
        Assert.Equal(94.7m, l1.WinRatePercent);        // 90 wins / 95 ends
        Assert.Equal(100m, l1.ReachPercent);
        Assert.Equal(20m, l1.DropToNextPercent);       // 100 -> 80

        var l2 = rep.Levels[1];
        Assert.Equal(80m, l2.ReachPercent);
        Assert.Equal(10, l2.PlayersQuit);
        Assert.Equal(75m, l2.DropToNextPercent);       // 80 -> 20

        var l10 = rep.Levels[2];
        Assert.Equal(28.6m, l10.WinRatePercent);       // 12 / 42
        Assert.Null(l10.DropToNextPercent);            // last level

        Assert.Equal("2", ProgressionMath.BiggestDrop(rep.Levels)!.Level);
        Assert.Equal(20, ProgressionMath.PlayersAt(rep.Levels, "10"));
        Assert.Equal(0, ProgressionMath.PlayersAt(rep.Levels, "5"));

        var modes = rep.Modes.ToDictionary(m => m.Mode);
        Assert.Equal(235, modes["campaign"].Attempts);
        Assert.Equal(3, modes["campaign"].LevelsPlayed);
        Assert.Equal(7, modes["clear-board"].MostPlayersOnOneLevel);
        Assert.Equal(["0", "1"], rep.SuccessValuesSeen);
        Assert.Equal(0, rep.UnattributedLevelEvents);
    }

    [Fact]
    public void Build_reports_missing_success_and_unattributed_levels()
    {
        var rows = new[]
        {
            Row("(not set)", "level_start", 60, 70), Row("(not set)", "level_end", 50, 55),
            Row("1", "level_start", 30, 40), Row("1", "level_end", 25, 30),           // no success value
            Row("2", "level_start", 10, 10),
        };
        var rep = ProgressionMath.Build("p", "r", rows, "campaign", false, false);
        Assert.Equal(125, rep.UnattributedLevelEvents);
        Assert.Equal(60, rep.UnattributedLevelPlayers);
        Assert.Empty(rep.SuccessValuesSeen);
        Assert.Null(rep.Levels[0].WinRatePercent);
        Assert.Equal(0, rep.Levels[0].PlayersWon);
    }

    [Fact]
    public void Build_returns_empty_levels_for_unknown_mode_but_still_lists_modes()
    {
        var rep = ProgressionMath.Build("p", "r", [Row("1", "level_start", 5, 5)], "daily", false, false);
        Assert.Empty(rep.Levels);
        Assert.Single(rep.Modes);
    }
}
