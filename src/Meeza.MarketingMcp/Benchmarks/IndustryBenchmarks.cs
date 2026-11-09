using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meeza.MarketingMcp.Benchmarks;

/// <summary>
/// Published mobile-game marketing benchmarks (CPI, ad metrics, retention, ROAS, store quality) and
/// documented practices of large studios. This is a curated, sourced snapshot embedded in the binary.
/// It is static reference data, never fetched at runtime and never mixed into the user's own numbers.
/// </summary>
public static class IndustryBenchmarks
{
    private const string Resource = "Meeza.MarketingMcp.Benchmarks.benchmarks.json";

    private static readonly Lazy<JsonObject> Root = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)
                           ?? throw new InvalidOperationException("Benchmark data resource is missing.");
        return JsonNode.Parse(stream)!.AsObject();
    });

    public static JsonObject All => Root.Value;

    public static IReadOnlyList<string> Categories =>
        Metrics.Select(m => (string?)m["category"]).OfType<string>().Distinct().ToArray();

    public static IReadOnlyList<string> Genres =>
        Metrics.Select(m => (string?)m["genre"]).OfType<string>().Where(g => g != "all").Distinct().Order().ToArray();

    private static IEnumerable<JsonObject> Metrics => (All["metrics"] as JsonArray ?? []).OfType<JsonObject>();

    /// <summary>
    /// Filters the snapshot. Genre keeps rows for that genre plus all-genre rows; country keeps rows for
    /// that ISO country, plus regional/global rows that include it, plus rows with no geography.
    /// </summary>
    public static JsonObject Query(string? category = null, string? genre = null, string? country = null, bool includePlaybook = true)
    {
        category = Norm(category);
        genre = Norm(genre);
        country = Norm(country)?.ToUpperInvariant();

        var metrics = Metrics.Where(m =>
            (category is null || Eq(m["category"], category)) &&
            (genre is null || Eq(m["genre"], genre) || Eq(m["genre"], "all") || m["genre"] is null) &&
            (country is null || CoversCountry(m, country)));

        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metricArray = new JsonArray();
        foreach (var m in metrics)
        {
            if ((string?)m["source"] is { } s) usedSources.Add(s);
            metricArray.Add(m.DeepClone());
        }

        var result = new JsonObject
        {
            ["asOf"] = All["asOf"]?.DeepClone(),
            ["currency"] = All["currency"]?.DeepClone(),
            ["disclaimer"] = All["disclaimer"]?.DeepClone(),
            ["metrics"] = metricArray,
        };

        if (includePlaybook && (category is null || category == "playbook"))
        {
            var playbook = new JsonArray();
            foreach (var p in (All["playbook"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)p["source"] is { } s) usedSources.Add(s);
                playbook.Add(p.DeepClone());
            }
            result["playbook"] = playbook;
        }

        result["sources"] = new JsonArray((All["sources"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(s => usedSources.Contains((string?)s["id"] ?? ""))
            .Select(s => (JsonNode)s.DeepClone())
            .ToArray());
        return result;
    }

    private static bool CoversCountry(JsonObject m, string country)
    {
        if (m["countries"] is JsonArray list)
            return list.Any(c => string.Equals((string?)c, country, StringComparison.OrdinalIgnoreCase));
        // No explicit country list: a global or geography-less benchmark applies everywhere.
        return m["region"] is null || Eq(m["region"], "global");
    }

    private static bool Eq(JsonNode? node, string value) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, value, StringComparison.OrdinalIgnoreCase);

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) || s.Trim().Equals("all", StringComparison.OrdinalIgnoreCase) ? null : s.Trim().ToLowerInvariant();

    public static string Serialize(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
