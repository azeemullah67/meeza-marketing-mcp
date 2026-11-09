using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace Meeza.MarketingMcp.Storage;

/// <summary>
/// Local SQLite store for synchronized reporting data, projects, KPI targets and sync history.
/// One file per user profile (override with MEEZA_DB_PATH). WAL mode lets the dashboard read while a sync writes,
/// and every write that replaces data runs in a single transaction so a failed sync never leaves partial data.
/// </summary>
public sealed class MarketingDb
{
    private readonly string _connectionString;

    public MarketingDb(string path)
    {
        Path = path;
        if (path != ":memory:" && !path.StartsWith("file:", StringComparison.Ordinal))
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        Migrate();
    }

    public string Path { get; }

    public static string DefaultPath =>
        Environment.GetEnvironmentVariable("MEEZA_DB_PATH") is { Length: > 0 } custom
            ? custom
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeezaMarketingMcp", "marketing.db");

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=10000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        c.Exec("PRAGMA journal_mode=WAL;");
        c.Exec("CREATE TABLE IF NOT EXISTS schema_migrations(version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL)");
        var applied = c.Query("SELECT version FROM schema_migrations", null, r => r.GetInt32(0)).ToHashSet();
        foreach (var (version, sql) in Migrations.All.OrderBy(m => m.Version))
        {
            if (applied.Contains(version)) continue;
            using var tx = c.BeginTransaction();
            c.Exec(sql, null, tx);
            c.Exec("INSERT INTO schema_migrations(version, applied_at) VALUES(@v, @at)", new { v = version, at = Db.Now() }, tx);
            tx.Commit();
        }
    }

    public int SchemaVersion
    {
        get
        {
            using var c = Open();
            return c.Scalar<long?>("SELECT MAX(version) FROM schema_migrations") is { } v ? (int)v : 0;
        }
    }
}

/// <summary>Tiny parameterized-SQL helpers (no ORM). Parameters come from an anonymous object: new { id = 1 } binds @id.</summary>
public static class Db
{
    public static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static DateOnly Date(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static int Exec(this SqliteConnection c, string sql, object? args = null, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, sql, args, tx);
        return cmd.ExecuteNonQuery();
    }

    public static T? Scalar<T>(this SqliteConnection c, string sql, object? args = null, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, sql, args, tx);
        var v = cmd.ExecuteScalar();
        if (v is null or DBNull) return default;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, object? args, Func<SqliteDataReader, T> map, SqliteTransaction? tx = null)
    {
        using var cmd = Command(c, sql, args, tx);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static SqliteCommand Command(SqliteConnection c, string sql, object? args, SqliteTransaction? tx)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        if (args is IReadOnlyDictionary<string, object?> dict)
            foreach (var (k, v) in dict) cmd.Parameters.AddWithValue("@" + k, ToDb(v));
        else if (args is not null)
            foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                cmd.Parameters.AddWithValue("@" + p.Name, ToDb(p.GetValue(args)));
        return cmd;
    }

    private static object ToDb(object? v) => v switch
    {
        null => DBNull.Value,
        DateOnly d => Iso(d),
        DateTimeOffset dto => dto.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? 1 : 0,
        decimal m => (double)m,
        _ => v,
    };

    // Typed nullable readers.
    public static string? Str(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetString(i); }
    public static long? Long(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetInt64(i); }
    public static decimal? Dec(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : (decimal)r.GetDouble(i); }
    public static double? Dbl(this SqliteDataReader r, string col) { var i = r.GetOrdinal(col); return r.IsDBNull(i) ? null : r.GetDouble(i); }
}
