using System.Text.Json;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Tests;

public class AssetPerformanceTests
{
    private static readonly DateRange Range = new(new DateOnly(2026, 10, 6), new DateOnly(2026, 11, 4));
    private static readonly AdAccount Account = new("google_ads", "1234567890", "Gapsy", "PKR", "Asia/Karachi", "ENABLED");

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Shape returned by the live Google Ads API for ad_group_ad_asset_view.
    private const string VideoRow = """
        {"campaign":{"id":"24420439908","name":"Gapsy | PH | Installs"},
         "adGroup":{"id":"202169310656","name":"Ad group 1"},
         "adGroupAd":{"ad":{"id":"830451829109"}},
         "asset":{"id":"435811827466","type":"YOUTUBE_VIDEO","youtubeVideoAsset":{"youtubeVideoId":"cMsLQqpo4Ok","youtubeVideoTitle":"Clear It in 15 Moves"}},
         "adGroupAdAssetView":{"fieldType":"YOUTUBE_VIDEO","performanceLabel":"LEARNING","enabled":true},
         "metrics":{"costMicros":"28461244","impressions":"22","clicks":"2","conversions":0}}
        """;

    [Fact]
    public void Video_asset_row_is_normalized()
    {
        var a = GoogleAdsMapping.ToAssetPerformance(Parse(VideoRow), Account, Range, 1m);

        Assert.Equal("435811827466", a.AssetId);
        Assert.Equal("video", a.AssetType);
        Assert.Equal("YOUTUBE_VIDEO", a.FieldType);
        Assert.Equal("Clear It in 15 Moves", a.Label);
        Assert.Equal("https://www.youtube.com/watch?v=cMsLQqpo4Ok", a.PreviewUrl);
        Assert.Equal("LEARNING", a.PerformanceLabel);
        Assert.Equal(28.4612m, a.Spend);
        Assert.Equal("PKR", a.Currency);
        Assert.Equal(1m, a.Installs);
        Assert.Equal(28.4612m, a.Cpi);
        Assert.Equal("830451829109", a.AdId);
    }

    [Fact]
    public void Text_asset_uses_its_text_and_null_installs_mean_untracked()
    {
        var row = Parse("""
            {"adGroup":{"id":"1"},"adGroupAd":{"ad":{"id":"2"}},
             "asset":{"id":"3","type":"TEXT","textAsset":{"text":"Can You Clear the Board?"}},
             "adGroupAdAssetView":{"fieldType":"HEADLINE","performanceLabel":"BEST","enabled":true},
             "metrics":{"costMicros":"0","impressions":"50","clicks":"3"}}
            """);
        var a = GoogleAdsMapping.ToAssetPerformance(row, Account, Range, null);

        Assert.Equal("text", a.AssetType);
        Assert.Equal("Can You Clear the Board?", a.Label);
        Assert.Null(a.PreviewUrl);
        Assert.Null(a.Installs);
        Assert.Null(a.Cpi);
        Assert.Equal(6m, a.Ctr);
    }

    [Fact]
    public void Asset_key_separates_the_same_asset_used_in_two_fields()
    {
        var headline = Parse("""{"adGroup":{"id":"1"},"adGroupAd":{"ad":{"id":"2"}},"asset":{"id":"3"},"adGroupAdAssetView":{"fieldType":"HEADLINE"}}""");
        var description = Parse("""{"adGroup":{"id":"1"},"adGroupAd":{"ad":{"id":"2"}},"asset":{"id":"3"},"adGroupAdAssetView":{"fieldType":"DESCRIPTION"}}""");
        Assert.NotEqual(GoogleAdsMapping.AssetKey(headline), GoogleAdsMapping.AssetKey(description));
    }

    [Theory]
    [InlineData("BEST", CreativeLab.Scale)]
    [InlineData("GOOD", CreativeLab.Steady)]
    [InlineData("LOW", CreativeLab.Cut)]
    [InlineData("LEARNING", CreativeLab.Testing)]
    [InlineData("PENDING", CreativeLab.Testing)]
    [InlineData(null, CreativeLab.Testing)]
    public void Assets_are_judged_by_googles_rating(string? label, string expected) =>
        Assert.Equal(expected, CreativeLab.DecideAsset(label).Stage);

    private static AssetPerformance Asset(string id, string label, string currency = "PKR", bool enabled = true) => new()
    {
        Platform = "google_ads", AccountId = "1", Currency = currency, AdId = "ad", AssetId = id, AssetType = "text",
        FieldType = "HEADLINE", Label = id, PerformanceLabel = label, Enabled = enabled, Spend = 5, Impressions = 100, Clicks = 4,
        StartDate = Range.Start, EndDate = Range.End,
    };

    private static CampaignPerformance AppAd(string currency = "PKR") => new CampaignPerformance
    {
        Platform = "google_ads", AccountId = "1", Level = "ad", AdId = "ad", AdName = "App Ad #1", Currency = currency,
        Spend = 1687, Impressions = 11667, Clicks = 290, Installs = 57, StartDate = Range.Start, EndDate = Range.End,
    }.WithDerivedMetrics();

    [Fact]
    public void Assets_are_attached_to_their_currency_board_without_changing_ad_spend()
    {
        var boards = CreativeLab.Classify([AppAd()], [Asset("a", "BEST"), Asset("b", "LOW"), Asset("c", "LEARNING"), Asset("d", "LEARNING")]);
        var b = Assert.Single(boards);

        Assert.Equal(4, b.Assets.Count);
        Assert.Equal(1, b.AssetStages[CreativeLab.Scale]);
        Assert.Equal(1, b.AssetStages[CreativeLab.Cut]);
        Assert.Equal(2, b.AssetStages[CreativeLab.Testing]);
        Assert.Equal(1687m, b.Stages.Sum(s => s.Spend)); // asset spend overlaps, so it is never added to stages
        Assert.Equal(CreativeLab.Cut, b.Assets[0].Stage);  // things to act on come first
    }

    [Fact]
    public void Disabled_assets_are_left_out()
    {
        var b = CreativeLab.Classify([AppAd()], [Asset("a", "GOOD"), Asset("old", "LOW", enabled: false)]).Single();
        Assert.Equal(["a"], b.Assets.Select(a => a.AssetId));
    }

    [Fact]
    public void Board_without_assets_keeps_working() =>
        Assert.Empty(CreativeLab.Classify([AppAd()], []).Single().Assets);
}

public class VitalsFreshnessTests
{
    private static readonly DateOnly Fresh = new(2026, 11, 4); // exclusive: latest complete day is Nov 3

    [Fact]
    public void Range_inside_available_data_is_unchanged()
    {
        var r = new DateRange(new(2026, 10, 1), new(2026, 10, 31));
        Assert.Equal((r, (string?)null), Meeza.Marketing.PlayConsole.PlayConsoleService.FitToFreshness(r, Fresh));
    }

    [Fact]
    public void Range_ending_after_latest_day_is_clamped()
    {
        var (r, note) = Meeza.Marketing.PlayConsole.PlayConsoleService.FitToFreshness(new(new(2026, 10, 29), new(2026, 11, 4)), Fresh);
        Assert.Equal(new DateRange(new(2026, 10, 29), new(2026, 11, 3)), r);
        Assert.NotNull(note);
    }

    [Fact]
    public void Today_moves_to_the_latest_available_day_instead_of_failing()
    {
        var (r, note) = Meeza.Marketing.PlayConsole.PlayConsoleService.FitToFreshness(new(new(2026, 11, 5), new(2026, 11, 5)), Fresh);
        Assert.Equal(new DateRange(new(2026, 11, 3), new(2026, 11, 3)), r);
        Assert.Contains("2 days late", note);
    }

    [Fact]
    public void Unknown_freshness_leaves_range_alone()
    {
        var r = new DateRange(new(2026, 11, 5), new(2026, 11, 5));
        Assert.Equal(r, Meeza.Marketing.PlayConsole.PlayConsoleService.FitToFreshness(r, null).Range);
    }
}
