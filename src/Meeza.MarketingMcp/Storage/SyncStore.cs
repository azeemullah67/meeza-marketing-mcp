using System.Globalization;

namespace Meeza.MarketingMcp.Storage;

public sealed record SyncState(
    string ProjectId, string Integration, string ExternalId,
    string? LastAttemptAt, string? LastSuccessAt, string? LastStatus, string? LastError,
    string? LatestDataDate, string? BackfilledFrom, long? RecordsLast, long? DurationMsLast, int ConsecutiveFailures);

public sealed record SyncRun(
    long Id, string ProjectId, string Integration, string ExternalId, string Kind, string Trigger,
    string StartedAt, string? FinishedAt, string Status, string? WindowStart, string? WindowEnd,
    long? Records, string? LatestDataDate, string? Error, string? Warnings, long? DurationMs);

/// <summary>Sync history and per-account sync state.</summary>
public sealed class SyncStore(MarketingDb db)
{
    public long StartRun(string projectId, string integration, string externalId, string kind, string trigger, DateOnly? from, DateOnly? to)
    {
        using var c = db.Open();
        var now = Db.Now();
        c.Exec("""
            INSERT INTO sync_runs(project_id, integration, external_id, kind, trigger, started_at, status, window_start, window_end)
            VALUES(@projectId, @integration, @externalId, @kind, @trigger, @now, 'running', @from, @to)
            """, new { projectId, integration, externalId, kind, trigger, now, from, to });
        var id = c.Scalar<long>("SELECT last_insert_rowid()");
        c.Exec("""
            INSERT INTO sync_state(project_id, integration, external_id, last_attempt_at, last_status)
            VALUES(@projectId, @integration, @externalId, @now, 'running')
            ON CONFLICT(project_id, integration, external_id) DO UPDATE SET last_attempt_at=@now, last_status='running'
            """, new { projectId, integration, externalId, now });
        return id;
    }

    /// <summary>
    /// Finishes a run. status: success | partial | failed. latest_data_date and backfilled_from only ever move with
    /// successful data, so a failed run never makes stale data look current.
    /// </summary>
    public void FinishRun(long runId, string status, long records, DateOnly? latestDataDate, DateOnly? backfilledFrom, string? error, IReadOnlyList<string> warnings, long durationMs)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var now = Db.Now();
        var run = c.Query("SELECT project_id, integration, external_id FROM sync_runs WHERE id=@runId", new { runId },
            r => (P: r.GetString(0), I: r.GetString(1), E: r.GetString(2)), tx).Single();
        c.Exec("""
            UPDATE sync_runs SET finished_at=@now, status=@status, records=@records, latest_data_date=@latest, error=@error, warnings=@warnings, duration_ms=@durationMs
            WHERE id=@runId
            """, new { now, status, records, latest = latestDataDate, error, warnings = warnings.Count == 0 ? null : string.Join("\n", warnings), durationMs, runId }, tx);
        var ok = status is "success" or "partial";
        c.Exec($"""
            UPDATE sync_state SET
                last_status=@status, last_error=@error, records_last=@records, duration_ms_last=@durationMs,
                last_success_at = CASE WHEN @ok=1 THEN @now ELSE last_success_at END,
                latest_data_date = CASE WHEN @ok=1 AND @latest IS NOT NULL AND (latest_data_date IS NULL OR @latest > latest_data_date) THEN @latest ELSE latest_data_date END,
                backfilled_from = CASE WHEN @ok=1 AND @backfilled IS NOT NULL AND (backfilled_from IS NULL OR @backfilled < backfilled_from) THEN @backfilled ELSE backfilled_from END,
                consecutive_failures = CASE WHEN @status='failed' THEN consecutive_failures + 1 ELSE 0 END
            WHERE project_id=@p AND integration=@i AND external_id=@e
            """, new { status, error, records, durationMs, ok = ok ? 1 : 0, now, latest = latestDataDate, backfilled = backfilledFrom, p = run.P, i = run.I, e = run.E }, tx);
        tx.Commit();
    }

    /// <summary>Marks runs left 'running' by a crashed process as failed so they never block or mislead.</summary>
    public int RecoverAbandonedRuns(TimeSpan olderThan)
    {
        using var c = db.Open();
        var cutoff = DateTimeOffset.UtcNow.Subtract(olderThan).ToString("O", CultureInfo.InvariantCulture);
        var n = c.Exec("""
            UPDATE sync_runs SET status='failed', finished_at=@now, error='Interrupted (the app stopped during this sync). It will be retried.'
            WHERE status='running' AND started_at < @cutoff
            """, new { cutoff, now = Db.Now() });
        c.Exec("UPDATE sync_state SET last_status='failed', last_error='Interrupted; will retry.' WHERE last_status='running' AND last_attempt_at < @cutoff", new { cutoff });
        return n;
    }

    public IReadOnlyList<SyncState> States(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM sync_state WHERE project_id=@projectId", new { projectId }, r => new SyncState(
            r.Str("project_id")!, r.Str("integration")!, r.Str("external_id")!, r.Str("last_attempt_at"), r.Str("last_success_at"),
            r.Str("last_status"), r.Str("last_error"), r.Str("latest_data_date"), r.Str("backfilled_from"),
            r.Long("records_last"), r.Long("duration_ms_last"), (int)(r.Long("consecutive_failures") ?? 0)));
    }

    public SyncState? State(string projectId, string integration, string externalId) =>
        States(projectId).FirstOrDefault(s => s.Integration == integration && s.ExternalId == externalId);

    public IReadOnlyList<SyncRun> Runs(string projectId, int limit = 50)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM sync_runs WHERE project_id=@projectId ORDER BY id DESC LIMIT @limit", new { projectId, limit }, r => new SyncRun(
            r.Long("id")!.Value, r.Str("project_id")!, r.Str("integration")!, r.Str("external_id")!, r.Str("kind")!, r.Str("trigger")!,
            r.Str("started_at")!, r.Str("finished_at"), r.Str("status")!, r.Str("window_start"), r.Str("window_end"),
            r.Long("records"), r.Str("latest_data_date"), r.Str("error"), r.Str("warnings"), r.Long("duration_ms")));
    }

    // ---------------------------------------------------------------- leases

    /// <summary>Takes a lease if free or expired. Works across processes sharing the database file.</summary>
    public bool TryAcquire(string key, string owner, TimeSpan ttl)
    {
        using var c = db.Open();
        var now = DateTimeOffset.UtcNow;
        var n = c.Exec("""
            INSERT INTO sync_leases(lease_key, owner, expires_at) VALUES(@key, @owner, @exp)
            ON CONFLICT(lease_key) DO UPDATE SET owner=@owner, expires_at=@exp WHERE sync_leases.expires_at < @now OR sync_leases.owner = @owner
            """, new { key, owner, exp = now.Add(ttl).ToString("O", CultureInfo.InvariantCulture), now = now.ToString("O", CultureInfo.InvariantCulture) });
        return n > 0;
    }

    public string? LeaseOwner(string key)
    {
        using var c = db.Open();
        return c.Scalar<string>("SELECT owner FROM sync_leases WHERE lease_key=@key", new { key });
    }

    /// <summary>Takes over a lease from a specific (dead) holder; fails if someone else got it first.</summary>
    public bool TakeOver(string key, string deadOwner, string owner, TimeSpan ttl)
    {
        using var c = db.Open();
        return c.Exec("UPDATE sync_leases SET owner=@owner, expires_at=@exp WHERE lease_key=@key AND owner=@deadOwner",
            new { key, deadOwner, owner, exp = DateTimeOffset.UtcNow.Add(ttl).ToString("O", CultureInfo.InvariantCulture) }) > 0;
    }

    /// <summary>Marks one account's runs left 'running' by a process that no longer exists as interrupted.</summary>
    public int MarkInterrupted(string projectId, string integration, string externalId)
    {
        using var c = db.Open();
        var n = c.Exec("""
            UPDATE sync_runs SET status='failed', finished_at=@now, error='Interrupted (the app stopped during this sync). Resumed by a later sync.'
            WHERE status='running' AND project_id=@p AND integration=@i AND external_id=@e
            """, new { now = Db.Now(), p = projectId, i = integration, e = externalId });
        c.Exec("UPDATE sync_state SET last_status='failed', last_error='Interrupted; resumed by a later sync.' WHERE last_status='running' AND project_id=@p AND integration=@i AND external_id=@e",
            new { p = projectId, i = integration, e = externalId });
        return n;
    }

    /// <summary>
    /// Records progress after each stored window so an interrupted backfill resumes where it stopped.
    /// Only moves the data markers forward; status and last-success time are set by FinishRun.
    /// </summary>
    public void Checkpoint(string projectId, string integration, string externalId, DateOnly? latestDataDate, DateOnly? backfilledFrom)
    {
        using var c = db.Open();
        c.Exec("""
            UPDATE sync_state SET
                latest_data_date = CASE WHEN @latest IS NOT NULL AND (latest_data_date IS NULL OR @latest > latest_data_date) THEN @latest ELSE latest_data_date END,
                backfilled_from = CASE WHEN @backfilled IS NOT NULL AND (backfilled_from IS NULL OR @backfilled < backfilled_from) THEN @backfilled ELSE backfilled_from END
            WHERE project_id=@p AND integration=@i AND external_id=@e
            """, new { latest = latestDataDate is { } l ? Db.Iso(l) : null, backfilled = backfilledFrom is { } b ? Db.Iso(b) : null, p = projectId, i = integration, e = externalId });
    }

    public void Release(string key, string owner)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM sync_leases WHERE lease_key=@key AND owner=@owner", new { key, owner });
    }
}
