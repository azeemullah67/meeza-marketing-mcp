using System.Globalization;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.PlayConsole.Models;

/// <summary>Parsing for Play Console bulk report CSVs and Reporting API rows.</summary>
public static class PlayReports
{
    // ---------------------------------------------------------------- CSV

    /// <summary>Minimal RFC 4180 CSV parser (quoted fields, escaped quotes, CRLF).</summary>
    public static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else field.Append(c);
                continue;
            }
            switch (c)
            {
                case '"': inQuotes = true; break;
                case ',': row.Add(field.ToString()); field.Clear(); break;
                case '\r': break;
                case '\n':
                    row.Add(field.ToString()); field.Clear();
                    if (row.Count > 1 || row[0].Length > 0) rows.Add(row.ToArray());
                    row.Clear();
                    break;
                case (char)0xFEFF: break; // byte order mark
                default: field.Append(c); break;
            }
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    /// <summary>Converts CSV rows into header -> value dictionaries.</summary>
    public static List<Dictionary<string, string>> ToRecords(string csv)
    {
        var rows = ParseCsv(csv);
        if (rows.Count == 0) return [];
        var header = rows[0].Select(h => h.Trim()).ToArray();
        return rows.Skip(1).Select(r =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < header.Length && i < r.Length; i++) d[header[i]] = r[i].Trim();
            return d;
        }).ToList();
    }

    public static DateOnly? Date(Dictionary<string, string> rec) =>
        rec.TryGetValue("Date", out var s) && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;

    public static decimal? Number(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---------------------------------------------------------------- Installs

    /// <summary>
    /// Summarizes an installs report. Column names have changed over the years, so every numeric column is
    /// handled generically: "Daily ..." and "... events" columns are summed over the range, while
    /// "Total ..."/"Active ..." columns are point-in-time values and the latest one is reported.
    /// </summary>
    public static object SummarizeInstalls(IEnumerable<Dictionary<string, string>> records, DateRange range, string? groupColumn)
    {
        var inRange = records.Where(r => Date(r) is { } d && d >= range.Start && d <= range.End).ToList();
        var groups = groupColumn is null
            ? inRange.GroupBy(_ => (string?)null)
            : inRange.GroupBy(r => r.GetValueOrDefault(groupColumn) is { Length: > 0 } g ? g : "Unknown");

        return groups.Select(g =>
        {
            var sums = new SortedDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            var latest = new SortedDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in g.OrderBy(Date))
            {
                foreach (var (col, raw) in rec)
                {
                    if (col is "Date" or "Package Name" || col == groupColumn) continue;
                    if (Number(raw) is not { } v) continue;
                    if (col.StartsWith("Total", StringComparison.OrdinalIgnoreCase) || col.StartsWith("Active", StringComparison.OrdinalIgnoreCase))
                        latest[col] = v;
                    else
                        sums[col] = sums.GetValueOrDefault(col) + v;
                }
            }
            return new
            {
                group = g.Key,
                countryName = groupColumn == "Country" ? Aggregation.CountryName(g.Key) : null,
                days = g.Select(Date).Distinct().Count(),
                summedOverRange = sums,
                latestValues = latest,
                primaryInstalls = PrimaryInstalls(sums),
            };
        })
        .OrderByDescending(x => x.primaryInstalls?.Value ?? 0)
        .ToArray();
    }

    public static readonly string[] PreferredInstallColumns = ["Daily User Installs", "Install events", "Daily Device Installs"];

    private static InstallFigure? PrimaryInstalls(IReadOnlyDictionary<string, decimal> sums)
    {
        foreach (var c in PreferredInstallColumns)
            if (sums.TryGetValue(c, out var v)) return new InstallFigure(c, v);
        return null;
    }

    public sealed record InstallFigure(string Column, decimal Value);

    // ---------------------------------------------------------------- Ratings

    public static object SummarizeRatings(IEnumerable<Dictionary<string, string>> records, DateRange range, string? groupColumn)
    {
        var inRange = records.Where(r => Date(r) is { } d && d >= range.Start && d <= range.End).ToList();
        var groups = groupColumn is null
            ? inRange.GroupBy(_ => (string?)null)
            : inRange.GroupBy(r => r.GetValueOrDefault(groupColumn) is { Length: > 0 } g ? g : "Unknown");

        return groups.Select(g =>
        {
            var ordered = g.OrderBy(Date).ToList();
            var daily = ordered.Select(r => Number(r.GetValueOrDefault("Daily Average Rating"))).Where(v => v is not null).Select(v => v!.Value).ToList();
            var totals = ordered.Select(r => Number(r.GetValueOrDefault("Total Average Rating"))).Where(v => v is not null).ToList();
            return new
            {
                group = g.Key,
                countryName = groupColumn == "Country" ? Aggregation.CountryName(g.Key) : null,
                latestTotalAverageRating = totals.LastOrDefault(),
                firstTotalAverageRating = totals.FirstOrDefault(),
                meanOfDailyAverageRatings = daily.Count > 0 ? MetricMath.Round(daily.Average()) : (decimal?)null,
                daysWithNewRatings = daily.Count,
            };
        }).ToArray();
    }

    /// <summary>Months (yyyyMM) overlapping the range, used to locate monthly report files.</summary>
    public static IEnumerable<string> Months(DateRange range)
    {
        var m = new DateOnly(range.Start.Year, range.Start.Month, 1);
        while (m <= range.End)
        {
            yield return m.ToString("yyyyMM", CultureInfo.InvariantCulture);
            m = m.AddMonths(1);
        }
    }

    // ---------------------------------------------------------------- Reviews

    public static object ToReview(JsonElement r)
    {
        JsonElement? user = null, dev = null;
        foreach (var c in r.Arr("comments"))
        {
            user ??= c.Prop("userComment");
            dev ??= c.Prop("developerComment");
        }
        return new
        {
            reviewId = r.Str("reviewId"),
            author = r.Str("authorName"),
            starRating = user?.Long("starRating"),
            text = user?.Str("text")?.Trim(),
            originalText = user?.Str("originalText")?.Trim(),
            language = user?.Str("reviewerLanguage"),
            lastModified = Timestamp(user, "lastModified"),
            appVersionName = user?.Str("appVersionName"),
            appVersionCode = user?.Long("appVersionCode"),
            androidOsVersion = user?.Long("androidOsVersion"),
            device = user?.Str("device"),
            deviceManufacturer = user?.Str("deviceMetadata.manufacturer"),
            thumbsUp = user?.Long("thumbsUpCount"),
            thumbsDown = user?.Long("thumbsDownCount"),
            developerReply = dev?.Str("text"),
            developerReplyAt = Timestamp(dev, "lastModified"),
        };
    }

    public static long? Stars(JsonElement r) =>
        r.Arr("comments").Select(c => c.Long("userComment.starRating")).FirstOrDefault(s => s is not null);

    private static DateTimeOffset? Timestamp(JsonElement? e, string path) =>
        e?.Long(path + ".seconds") is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    // ---------------------------------------------------------------- Vitals

    public static VitalsRow ToVitalsRow(JsonElement row)
    {
        var date = new DateOnly((int)(row.Long("startTime.year") ?? 1), (int)(row.Long("startTime.month") ?? 1), (int)(row.Long("startTime.day") ?? 1));
        var dims = row.Arr("dimensions").ToDictionary(
            d => d.Str("dimension") ?? "?",
            d => d.Str("stringValue") ?? d.Str("int64Value") ?? d.Str("valueLabel"));
        var metrics = row.Arr("metrics").ToDictionary(
            m => m.Str("metric") ?? "?",
            m => m.Dec("decimalValue.value"));
        return new VitalsRow(date, dims, metrics);
    }

    public sealed record VitalsRow(DateOnly Date, Dictionary<string, string?> Dimensions, Dictionary<string, decimal?> Metrics);

    /// <summary>
    /// Summarizes daily rate metrics over the period, weighting each day by distinctUsers so the result is
    /// comparable to Play Console's period rates. Rates are returned as percentages.
    /// </summary>
    public static IReadOnlyList<object> SummarizeVitals(IReadOnlyList<VitalsRow> rows, IReadOnlyList<string> rateMetrics)
    {
        var groups = rows.GroupBy(r => string.Join("|", r.Dimensions.OrderBy(d => d.Key).Select(d => $"{d.Key}={d.Value}")));
        return groups.Select(g =>
        {
            var summary = new Dictionary<string, decimal?>();
            decimal users = 0;
            foreach (var metric in rateMetrics)
            {
                decimal weighted = 0, weight = 0;
                foreach (var r in g)
                {
                    if (r.Metrics.GetValueOrDefault(metric) is not { } v) continue;
                    var w = r.Metrics.GetValueOrDefault("distinctUsers") ?? 1;
                    weighted += v * w;
                    weight += w;
                }
                summary[metric + "Percent"] = weight > 0 ? MetricMath.Round(weighted / weight * 100m) : null;
            }
            foreach (var r in g) users += r.Metrics.GetValueOrDefault("distinctUsers") ?? 0;
            return new
            {
                dimensions = g.First().Dimensions,
                days = g.Count(),
                userDaysObserved = users,
                rates = summary,
            };
        })
        .OrderByDescending(x => x.userDaysObserved)
        .Cast<object>()
        .ToArray();
    }
}

