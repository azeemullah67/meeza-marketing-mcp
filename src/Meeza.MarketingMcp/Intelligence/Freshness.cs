using System.Globalization;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;

namespace Meeza.MarketingMcp.Intelligence;

public sealed record IntegrationStatus(
    string Integration, string ExternalId, string? DisplayName, string? Currency, string? TimeZone,
    string Freshness, string FreshnessReason,
    string? LastAttemptAt, string? LastSuccessAt, string? LastStatus, string? LastError,
    string? LatestDataDate, string? ExpectedLatestDate, string? BackfilledFrom, long? RecordsLast, long? DurationMsLast, int ConsecutiveFailures,
    bool Syncing);

/// <summary>
/// Classifies how current each connection's stored data is, so the UI can never present stale data as current:
/// never (no successful sync), syncing, fresh, delayed (synced, but the platform's newest day is behind what it
/// should be), stale (no successful sync for a long time), error (last attempt failed; stored data is older).
/// </summary>
public static class Freshness
{
    /// <summary>Newest day each source should normally have: ad platforms report today; GA4 typically lags 1 day.</summary>
    public static DateOnly ExpectedLatest(string integration, DateOnly today) =>
        integration == Integrations.Ga4 ? today.AddDays(-1) : today.AddDays(-1);

    public static IntegrationStatus Evaluate(ProjectConnection conn, SyncState? s, int intervalMinutes, bool syncing, DateTimeOffset now)
    {
        var today = TimeZones.Today(conn.TimeZone, now);
        var expected = ExpectedLatest(conn.Integration, today);
        string freshness, reason;
        var lastSuccess = s?.LastSuccessAt is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(s.LastSuccessAt, CultureInfo.InvariantCulture);
        var staleAfter = TimeSpan.FromMinutes(Math.Max(intervalMinutes * 3, 360));

        if (syncing && lastSuccess is null) { freshness = "syncing"; reason = "First synchronization in progress."; }
        else if (lastSuccess is null) { freshness = "never"; reason = s?.LastError is null ? "Not synchronized yet." : "Never synchronized successfully. " + s.LastError; }
        else if (now - lastSuccess > staleAfter) { freshness = "stale"; reason = $"Last successful sync {Ago(now - lastSuccess.Value)} ago. Showing stored data, which may be out of date."; }
        else if (s!.LastStatus == "failed") { freshness = "error"; reason = $"The latest sync failed; data is from the successful sync {Ago(now - lastSuccess.Value)} ago."; }
        else if (s.LatestDataDate is null) { freshness = "delayed"; reason = "Synced, but the platform reported no activity in the synchronized window."; }
        else if (Db.Date(s.LatestDataDate) < expected) { freshness = "delayed"; reason = $"Newest day with activity is {s.LatestDataDate}; expected {Db.Iso(expected)}. Campaigns may be paused, or the platform is still processing."; }
        else { freshness = syncing ? "syncing" : "fresh"; reason = $"Data through {s.LatestDataDate}."; }

        return new IntegrationStatus(conn.Integration, conn.ExternalId, conn.DisplayName, conn.Currency, conn.TimeZone, freshness, reason,
            s?.LastAttemptAt, s?.LastSuccessAt, s?.LastStatus, s?.LastError, s?.LatestDataDate, Db.Iso(expected), s?.BackfilledFrom,
            s?.RecordsLast, s?.DurationMsLast, s?.ConsecutiveFailures ?? 0, syncing);
    }

    private static string Ago(TimeSpan t) =>
        t.TotalDays >= 2 ? $"{(int)t.TotalDays} days" : t.TotalHours >= 2 ? $"{(int)t.TotalHours} hours" : $"{Math.Max(1, (int)t.TotalMinutes)} minutes";
}
