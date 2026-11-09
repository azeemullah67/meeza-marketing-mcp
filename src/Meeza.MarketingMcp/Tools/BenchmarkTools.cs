using System.ComponentModel;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Benchmarks;
using ModelContextProtocol.Server;

namespace Meeza.MarketingMcp.Tools;

[McpServerToolType]
public sealed class BenchmarkTools
{
    [McpServerTool(Name = "get_industry_benchmarks", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Published mobile-game marketing benchmarks with sources: CPI by OS, genre and country, ad metrics (CTR, CPM, IPM), " +
                 "retention (D1/D7/D30), ROAS/payback, Google Play quality thresholds, channel mix, and documented practices of large game studios " +
                 "(creative testing, playables, soft launch, channel strategy). Use it to put the user's own numbers in context. " +
                 "Benchmarks are in USD and are industry medians or averages from third-party reports, not targets for any specific game.")]
    public Task<string> GetIndustryBenchmarks(
        [Description("Optional category: cpi, ad_metrics, retention, roas, quality, channels, playbook.")] string? category = null,
        [Description("Optional game genre, e.g. puzzle, casual, hypercasual, strategy, rpg, casino, simulation, action, midcore.")] string? genre = null,
        [Description("Optional ISO country code, e.g. US, PK, PH, IN, BR.")] string? country = null,
        CancellationToken ct = default) =>
        ToolRunner.RunAsync("get_industry_benchmarks", r =>
        {
            r.Data = IndustryBenchmarks.Query(category, genre, country);
            r.Notes.Add("Benchmarks are third-party industry figures (see sources and periods on each row). Ranges differ between sources because of different samples, attribution and definitions.");
            r.Notes.Add("Compare like with like: benchmark CPIs are USD. Convert the user's spend to USD only if the user asks, and say which rate was used.");
            r.Notes.Add("Available genres: " + string.Join(", ", IndustryBenchmarks.Genres) + ".");
            return Task.CompletedTask;
        });
}
