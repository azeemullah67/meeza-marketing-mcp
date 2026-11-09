using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Tests;

public class InsightsTests
{
    private static CampaignPerformance Row(string platform, string name, decimal spend, decimal? installs, string currency = "USD") =>
        new CampaignPerformance
        {
            Platform = platform, AccountId = "1", Level = "ad", CampaignId = name, CampaignName = name, AdId = name, AdName = name,
            Currency = currency, Spend = spend, Impressions = 10_000, Clicks = 100, Installs = installs,
            StartDate = new DateOnly(2026, 10, 1), EndDate = new DateOnly(2026, 10, 7),
        }.WithDerivedMetrics();

    private static PerformanceTotals Totals(string platform, decimal spend, decimal installs, string currency = "USD") =>
        Aggregation.ByPlatform([Row(platform, "x", spend, installs, currency)])[0];

    [Fact]
    public void Previous_range_has_same_length_and_ends_before_start()
    {
        var r = new DateRange(new DateOnly(2026, 10, 28), new DateOnly(2026, 11, 3));
        Assert.Equal(new DateRange(new DateOnly(2026, 10, 21), new DateOnly(2026, 10, 27)), r.Previous());
    }

    [Fact]
    public void Change_is_null_when_base_missing_or_zero()
    {
        Assert.Null(Insights.Change(5, null).ChangePercent);
        Assert.Null(Insights.Change(5, 0).ChangePercent);
        Assert.Equal(25m, Insights.Change(5, 4).ChangePercent);
        Assert.Equal(-50m, Insights.Change(2, 4).ChangePercent);
    }

    [Fact]
    public void Compare_matches_platform_and_currency_only()
    {
        var cmp = Insights.Compare([Totals("meta", 120, 60)], [Totals("meta", 100, 100, "PKR")]);
        Assert.Null(cmp[0].Spend.ChangePercent); // different currency => no comparison
    }

    [Fact]
    public void Detects_spend_without_installs_and_cpi_outliers()
    {
        var rows = new[]
        {
            Row("meta", "a", 10, 10), Row("meta", "b", 10, 10), Row("meta", "c", 10, 10),
            Row("meta", "pricey", 50, 5),   // CPI 10 vs median 1
            Row("meta", "dud", 20, null),   // spend, no installs
        };
        var alerts = Insights.Detect(rows, Aggregation.ByPlatform(rows), null);
        Assert.Contains(alerts, a => a.Rule == "spend_without_installs" && a.Items!.Single().StartsWith("dud"));
        Assert.Contains(alerts, a => a.Rule == "cpi_outlier" && a.Items!.Single().StartsWith("pricey"));
    }

    [Fact]
    public void Cpi_increase_is_critical_and_sorted_first()
    {
        var rows = new[] { Row("google_ads", "a", 100, 10) };       // CPI 10
        var previous = new[] { Totals("google_ads", 100, 20) };    // CPI 5
        var alerts = Insights.Detect(rows, Aggregation.ByPlatform(rows), previous);
        Assert.Equal("cpi_increase", alerts[0].Rule);
        Assert.Equal("critical", alerts[0].Severity);
    }
}
