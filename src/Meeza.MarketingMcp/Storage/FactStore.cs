using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Meeza.MarketingMcp.Storage;

// Row shapes written by the syncers. Property names map to snake_case columns.
public sealed record AdDailyRow(
    DateOnly Date, string CampaignId, string? CampaignName, string? CampaignStatus, string? Channel,
    string AdsetId, string? AdsetName, string? AdsetStatus, string AdId, string? AdName, string? AdStatus,
    string Currency, decimal? Spend, long? Impressions, long? Reach, long? Clicks, decimal? Conversions,
    decimal? Installs, string? InstallSource, decimal? ConversionValue,
    long? VideoViews = null, long? VideoP25 = null, long? VideoP100 = null, long? Thruplays = null);

public sealed record CountryDailyRow(
    DateOnly Date, string CampaignId, string? CampaignName, string Country, string Currency,
    decimal? Spend, long? Impressions, long? Clicks, decimal? Conversions, decimal? Installs);

public sealed record AssetDailyRow(
    DateOnly Date, string? CampaignId, string AdsetId, string AdId, string AssetId, string FieldType, string Currency,
    decimal? Spend, long? Impressions, long? Clicks, decimal? Installs);

public sealed record CreativeRow(
    string CreativeKey, string? AdId, string? AssetId, string? Name, string? Format, string? FieldType,
    string? PreviewUrl, string? ThumbnailUrl, string? VideoId, decimal? DurationSeconds, string? Title, string? Body, string? PerformanceLabel);

public sealed record Ga4DailyRow(
    DateOnly Date, long? ActiveUsers, long? NewUsers, long? Active7d, long? Active28d, long? Sessions, decimal? EngagementSeconds,
    decimal? TotalRevenue, decimal? AdRevenue, decimal? PurchaseRevenue, long? Purchasers, string? Currency);

public sealed record Ga4DimRow(DateOnly Date, string Dimension, string Value, long? ActiveUsers, long? NewUsers, decimal? TotalRevenue);

public sealed record Ga4EventRow(DateOnly Date, string EventName, long? EventCount, long? Users);

public sealed record Ga4CohortRow(string SegmentType, string SegmentValue, DateOnly CohortDate, int DayN, long ActiveUsers, long CohortUsers);

public sealed record AdjustDailyRow(
    DateOnly Date, long? Installs, long? Clicks, long? Impressions, long? Sessions, long? Daus,
    decimal? Cost, decimal? Revenue, decimal? AdRevenue, decimal? AllRevenue, string Currency);

public sealed record AdjustCampaignDailyRow(
    DateOnly Date, string Network, string CampaignId, string CampaignName, string Country, string Platform,
    long? Installs, long? Clicks, long? Impressions, long? Sessions, decimal? Cost, decimal? AllRevenue, string Currency);

public sealed record AdjustCohortRow(string SegmentType, string SegmentValue, DateOnly CohortDate, int DayN, long ActiveUsers, long CohortUsers);

public sealed record ReachWindowRow(DateOnly WindowStart, DateOnly WindowEnd, long? Reach, long? Impressions, decimal? Frequency);

/// <summary>Result of replacing one window of facts.</summary>
public sealed record WriteResult(int Written, int Deleted, bool Skipped, string? Warning);

/// <summary>
/// Idempotent writes of synchronized facts. Each call replaces exactly one (project, account, date window) slice inside a
/// single transaction: re-running a sync for the same window yields the same rows (no duplicates), entities the platform
/// no longer reports for those days disappear, and nothing outside the window is touched.
/// </summary>
public sealed class FactStore(MarketingDb db)
{
    public MarketingDb Db => db;

    /// <summary>
    /// Replaces rows of <paramref name="table"/> for the scope and date window with <paramref name="rows"/>.
    /// Safety: when the platform returns NO rows for a window that already holds data, nothing is deleted
    /// (an empty answer after a partial outage must never wipe good history) and a warning is returned instead.
    /// Pass <paramref name="allowEmptyReplace"/> only when emptiness is confirmed (e.g. account has no spend at all).
    /// </summary>
    public WriteResult ReplaceWindow<T>(
        string table, IReadOnlyDictionary<string, object?> scope, string dateColumn, DateOnly from, DateOnly to,
        IReadOnlyCollection<T> rows, bool allowEmptyReplace = false, string? extraWhere = null, object? extraArgs = null)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var r = ReplaceWindow(c, tx, table, scope, dateColumn, from, to, rows, allowEmptyReplace, extraWhere, extraArgs);
        tx.Commit();
        return r;
    }

    public WriteResult ReplaceWindow<T>(
        SqliteConnection c, SqliteTransaction tx, string table, IReadOnlyDictionary<string, object?> scope, string dateColumn,
        DateOnly from, DateOnly to, IReadOnlyCollection<T> rows, bool allowEmptyReplace = false, string? extraWhere = null, object? extraArgs = null)
    {
        Guard(table);
        var where = new StringBuilder(string.Join(" AND ", scope.Keys.Select(k => $"{k}=@{k}")));
        where.Append($" AND {dateColumn} BETWEEN @__from AND @__to");
        if (extraWhere is not null) where.Append(" AND ").Append(extraWhere);
        var args = new Dictionary<string, object?>(scope) { ["__from"] = Storage.Db.Iso(from), ["__to"] = Storage.Db.Iso(to) };
        if (extraArgs is not null)
            foreach (var p in extraArgs.GetType().GetProperties()) args[p.Name] = p.GetValue(extraArgs);

        var existing = c.Scalar<long>($"SELECT COUNT(*) FROM {table} WHERE {where}", args, tx);
        if (rows.Count == 0 && existing > 0 && !allowEmptyReplace)
            return new WriteResult(0, 0, true,
                $"The platform returned no rows for {Storage.Db.Iso(from)}..{Storage.Db.Iso(to)}, but {existing} stored rows exist for that window. Kept the stored data; verify the account in the platform UI.");

        var deleted = c.Exec($"DELETE FROM {table} WHERE {where}", args, tx);
        var written = Insert(c, tx, table, scope, rows);
        return new WriteResult(written, deleted, false, null);
    }

    /// <summary>Inserts or updates rows keyed by the table's primary key (no window delete). Used for metadata tables.</summary>
    public int Upsert<T>(string table, IReadOnlyDictionary<string, object?> scope, IReadOnlyCollection<T> rows)
    {
        Guard(table);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var n = Insert(c, tx, table, scope, rows, upsert: true);
        tx.Commit();
        return n;
    }

    private static int Insert<T>(SqliteConnection c, SqliteTransaction tx, string table, IReadOnlyDictionary<string, object?> scope, IReadOnlyCollection<T> rows, bool upsert = false)
    {
        if (rows.Count == 0) return 0;
        var props = Props(typeof(T));
        var timestampColumn = table is "creatives" ? "updated_at" : "synced_at";
        var cols = scope.Keys.Concat(props.Select(p => p.Column)).Append(timestampColumn).ToArray();
        var sql = $"INSERT {(upsert ? "OR REPLACE " : "")}INTO {table}({string.Join(",", cols)}) VALUES({string.Join(",", cols.Select(x => "@" + x))})";
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        var parameters = cols.ToDictionary(x => x, x => cmd.Parameters.Add(new SqliteParameter("@" + x, null)));
        foreach (var (k, v) in scope) parameters[k].Value = DbValue(v);
        parameters[timestampColumn].Value = Storage.Db.Now();
        var n = 0;
        foreach (var row in rows)
        {
            foreach (var p in props) parameters[p.Column].Value = DbValue(p.Get(row!));
            n += cmd.ExecuteNonQuery();
        }
        return n;
    }

    private static object DbValue(object? v) => v switch
    {
        null => DBNull.Value,
        DateOnly d => Storage.Db.Iso(d),
        decimal m => (double)m,
        bool b => b ? 1 : 0,
        _ => v,
    };

    private static readonly HashSet<string> Tables =
        ["ad_daily", "campaign_country_daily", "asset_daily", "creatives", "reach_windows", "ga4_daily", "ga4_dim_daily", "ga4_events_daily", "ga4_cohorts",
         "adjust_daily", "adjust_campaign_daily", "adjust_cohorts"];

    private static void Guard(string table)
    {
        if (!Tables.Contains(table)) throw new ArgumentException($"Unknown fact table {table}.");
    }

    private sealed record Prop(string Column, Func<object, object?> Get);
    private static readonly ConcurrentDictionary<Type, Prop[]> PropCache = new();

    private static Prop[] Props(Type t) => PropCache.GetOrAdd(t, type => type
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.Name != "EqualityContract")
        .Select(p => new Prop(Snake(p.Name), o => p.GetValue(o)))
        .ToArray());

    public static string Snake(string name)
    {
        // AdsetId -> adset_id, DayN -> day_n, Active7d -> active_7d, VideoP25 -> video_p25.
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && (char.IsUpper(ch) || char.IsDigit(ch) && char.IsLower(name[i - 1]))) sb.Append('_');
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }
}
