using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Tests;

public class CreativeLabTests
{
    private static CampaignPerformance Ad(string name, decimal spend, decimal? installs, long impressions = 10_000, long? reach = null, string platform = "meta") =>
        new CampaignPerformance
        {
            Platform = platform, AccountId = "1", Level = "ad", CampaignId = "c", AdId = name, AdName = name, Currency = "USD",
            Spend = spend, Impressions = impressions, Reach = reach, Clicks = 100, Installs = installs,
            StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 7),
        }.WithDerivedMetrics();

    // Median CPI of the 10+ install ads below is 2.0.
    private static readonly CampaignPerformance[] Baseline = [Ad("m1", 20, 10), Ad("m2", 40, 20), Ad("m3", 60, 30)];

    private static CreativeLab.Item Judge(CampaignPerformance ad) =>
        CreativeLab.Classify(Baseline.Append(ad)).Single().Items.Single(i => i.AdId == ad.AdId);

    [Fact] public void Cheap_ad_with_enough_installs_is_a_winner() => Assert.Equal(CreativeLab.Scale, Judge(Ad("w", 15, 12)).Stage);      // CPI 1.25
    [Fact] public void Expensive_ad_is_cut() => Assert.Equal(CreativeLab.Cut, Judge(Ad("l", 40, 12)).Stage);                              // CPI 3.33
    [Fact] public void Few_installs_and_small_spend_is_still_testing() => Assert.Equal(CreativeLab.Testing, Judge(Ad("t", 4, 2)).Stage);
    [Fact] public void Zero_installs_after_two_median_cpis_is_cut() => Assert.Equal(CreativeLab.Cut, Judge(Ad("z", 5, 0)).Stage);
    [Fact] public void Zero_installs_on_tiny_spend_is_testing() => Assert.Equal(CreativeLab.Testing, Judge(Ad("z2", 3, 0)).Stage);
    [Fact] public void Untracked_installs_are_not_judged() => Assert.Equal(CreativeLab.Untracked, Judge(Ad("u", 30, null)).Stage);

    [Fact]
    public void High_frequency_needs_refresh_unless_it_is_a_loser()
    {
        Assert.Equal(CreativeLab.Refresh, Judge(Ad("f", 24, 12, impressions: 40_000, reach: 8_000)).Stage); // CPI 2.0, freq 5
        Assert.Equal(CreativeLab.Cut, Judge(Ad("f2", 60, 12, impressions: 40_000, reach: 8_000)).Stage);    // CPI 5.0 wins over fatigue
    }

    [Fact]
    public void Each_platform_is_judged_against_its_own_median()
    {
        // Google CPI 4.0 is normal for Google here even though Meta's median is 2.0.
        var google = new[] { Ad("g1", 40, 10, platform: "google_ads"), Ad("g2", 80, 20, platform: "google_ads"), Ad("g3", 48, 12, platform: "google_ads") };
        var item = CreativeLab.Classify(Baseline.Concat(google)).Single().Items.Single(i => i.AdId == "g3");
        Assert.Equal(CreativeLab.Steady, item.Stage);
    }

    [Fact]
    public void Stage_spend_shares_add_up_to_100()
    {
        var board = CreativeLab.Classify(Baseline.Append(Ad("x", 10, 0)).Append(Ad("y", 5, null))).Single();
        Assert.Equal(100m, Math.Round(board.Stages.Sum(s => s.SpendSharePercent)));
    }

    [Theory]
    [InlineData("Playable - Merge challenge", "playable")]
    [InlineData("UGC - Streamer reaction", "ugc")]
    [InlineData("Video - Level 42 fail", "video")]
    [InlineData("Static - Rewards chest", "static")]
    [InlineData("App Ad #8812", "other")]
    public void Format_is_guessed_from_name(string name, string format) => Assert.Equal(format, CreativeLab.FormatOf(name));
}
