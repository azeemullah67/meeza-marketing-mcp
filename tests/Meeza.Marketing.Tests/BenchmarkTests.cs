using System.Text.Json.Nodes;
using Meeza.MarketingMcp.Benchmarks;

namespace Meeza.Marketing.Tests;

public class BenchmarkTests
{
    private static JsonObject[] Metrics => IndustryBenchmarks.All["metrics"]!.AsArray().OfType<JsonObject>().ToArray();
    private static JsonObject[] Playbook => IndustryBenchmarks.All["playbook"]!.AsArray().OfType<JsonObject>().ToArray();
    private static HashSet<string> SourceIds =>
        IndustryBenchmarks.All["sources"]!.AsArray().OfType<JsonObject>().Select(s => (string)s["id"]!).ToHashSet();

    [Fact]
    public void Snapshot_loads_and_is_dated()
    {
        Assert.NotEmpty(Metrics);
        Assert.NotEmpty(Playbook);
        Assert.False(string.IsNullOrWhiteSpace((string?)IndustryBenchmarks.All["asOf"]));
        Assert.Equal("USD", (string?)IndustryBenchmarks.All["currency"]);
    }

    [Fact]
    public void Every_figure_and_practice_cites_a_known_source()
    {
        var ids = SourceIds;
        Assert.All(Metrics.Concat(Playbook), m => Assert.Contains((string)m["source"]!, ids));
    }

    [Fact]
    public void Every_source_has_an_https_url_and_publisher()
    {
        foreach (var s in IndustryBenchmarks.All["sources"]!.AsArray().OfType<JsonObject>())
        {
            Assert.StartsWith("https://", (string)s["url"]!);
            Assert.False(string.IsNullOrWhiteSpace((string?)s["publisher"]));
        }
    }

    [Fact]
    public void Every_metric_has_a_value_or_range_and_a_unit()
    {
        Assert.All(Metrics, m =>
        {
            Assert.True(m["value"] is not null || (m["low"] is not null && m["high"] is not null), (string?)m["id"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)m["unit"]), (string?)m["id"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)m["metric"]), (string?)m["id"]);
            if (m["low"] is not null && m["high"] is not null)
                Assert.True((decimal)m["low"]! <= (decimal)m["high"]!, (string?)m["id"]);
        });
    }

    [Fact]
    public void Metric_ids_are_unique()
    {
        var ids = Metrics.Select(m => (string)m["id"]!).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void Category_filter_only_returns_that_category_and_its_sources()
    {
        var r = IndustryBenchmarks.Query(category: "cpi");
        var metrics = r["metrics"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.NotEmpty(metrics);
        Assert.All(metrics, m => Assert.Equal("cpi", (string?)m["category"]));
        Assert.Null(r["playbook"]);
        var cited = metrics.Select(m => (string)m["source"]!).ToHashSet();
        Assert.All(r["sources"]!.AsArray().OfType<JsonObject>(), s => Assert.Contains((string)s["id"]!, cited));
    }

    [Fact]
    public void Country_filter_excludes_other_countries()
    {
        var r = IndustryBenchmarks.Query(category: "cpi", country: "us");
        Assert.All(r["metrics"]!.AsArray().OfType<JsonObject>(), m =>
        {
            if (m["countries"] is JsonArray c) Assert.Contains(c, x => (string?)x == "US");
        });
    }
}
