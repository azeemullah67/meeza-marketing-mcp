using System.Text.Json;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Meta.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Dashboard;

namespace Meeza.Marketing.Tests;

public class ReportLevelTests
{
    [Theory]
    [InlineData("ad", ReportLevel.Ad)]
    [InlineData("ads", ReportLevel.Ad)]
    [InlineData("adset", ReportLevel.AdSet)]
    [InlineData("ad_group", ReportLevel.AdSet)]
    [InlineData("Ad-Groups", ReportLevel.AdSet)]
    [InlineData(null, ReportLevel.Campaign)]
    public void Levels_parse(string? input, ReportLevel expected) => Assert.Equal(expected, ReportLevels.Parse(input));

    [Fact]
    public void Unknown_level_throws() => Assert.Throws<ArgumentException>(() => ReportLevels.Parse("keyword"));
}

public class AdLevelMappingTests
{
    private static readonly DateRange Range = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7));

    [Fact]
    public void Meta_ad_row_keeps_ad_and_adset_identity()
    {
        var row = JsonDocument.Parse("""
            {"account_id":"1","account_currency":"USD","campaign_id":"10","campaign_name":"C","adset_id":"20","adset_name":"S",
             "ad_id":"30","ad_name":"Video A","spend":"10","impressions":"1000","clicks":"20",
             "actions":[{"action_type":"mobile_app_install","value":"4"}]}
            """).RootElement;
        var p = MetaMapping.ToPerformance(row, Range, null, level: ReportLevel.Ad);
        Assert.Equal("ad", p.Level);
        Assert.Equal(("20", "S", "30", "Video A"), (p.AdSetId, p.AdSetName, p.AdId, p.AdName));
        Assert.Equal(2.5m, p.Cpi);
    }

    [Fact]
    public void Meta_daily_row_is_parsed()
    {
        var d = MetaMapping.ToDaily(JsonDocument.Parse("""{"account_currency":"PKR","date_start":"2026-10-03","spend":"1500.5","impressions":"9000","clicks":"80"}""").RootElement, "act_1", null);
        Assert.Equal(new DateOnly(2026, 10, 3), d.Date);
        Assert.Equal("PKR", d.Currency);
        Assert.Null(d.Installs);
    }

    [Fact]
    public void Google_unnamed_ad_gets_readable_fallback_name_and_status()
    {
        var account = new AdAccount("google_ads", "1234567890", "G", "USD", "UTC", "ENABLED");
        var row = JsonDocument.Parse("""
            {"campaign":{"id":"1","name":"UAC"},"adGroup":{"id":"2","name":"AG"},
             "adGroupAd":{"status":"PAUSED","ad":{"id":"777","type":"APP_AD"}},
             "metrics":{"costMicros":"2000000","impressions":"100","clicks":"5"}}
            """).RootElement;
        var p = GoogleAdsMapping.ToLevelPerformance(row, account, Range, ReportLevel.Ad, 4m);
        Assert.Equal("App Ad #777", p.AdName);
        Assert.Equal("PAUSED", p.Status);
        Assert.Equal("AG", p.AdSetName);
        Assert.Equal(0.5m, p.Cpi);
    }
}

public class DashboardDemoTests
{
    private static readonly DateRange Range = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7));

    [Theory]
    [InlineData(ReportLevel.Ad)]
    [InlineData(ReportLevel.AdSet)]
    [InlineData(ReportLevel.Campaign)]
    public void Demo_rollups_preserve_total_spend(ReportLevel level)
    {
        var ad = Rows(DemoData.Overview(Range, ReportLevel.Ad, null));
        var rolled = Rows(DemoData.Overview(Range, level, null));
        Assert.Equal(ad.Sum(r => r.GetProperty("spend").GetDecimal()), rolled.Sum(r => r.GetProperty("spend").GetDecimal()));
        Assert.All(rolled, r => Assert.Equal(level.Name(), r.GetProperty("level").GetString()));
    }

    [Fact]
    public void Demo_is_always_labelled() =>
        Assert.Contains(DemoData.Note, DemoData.Overview(Range, ReportLevel.Ad, null).Notes);

    [Fact]
    public void Demo_platform_filter_applies() =>
        Assert.All(Rows(DemoData.Overview(Range, ReportLevel.Ad, "meta")), r => Assert.Equal("meta", r.GetProperty("platform").GetString()));

    private static JsonElement[] Rows(ToolResponse r) =>
        JsonSerializer.SerializeToElement(r.Data, Json.Output).GetProperty("rows").EnumerateArray().ToArray();
}

public class AccessKeyTests
{
    [Fact]
    public void Key_is_long_and_compared_exactly()
    {
        Assert.True(AccessKey.Value.Length >= 32);
        Assert.True(AccessKey.Matches(AccessKey.Value));
        Assert.False(AccessKey.Matches(AccessKey.Value[..^1]));
        Assert.False(AccessKey.Matches(null));
    }
}
