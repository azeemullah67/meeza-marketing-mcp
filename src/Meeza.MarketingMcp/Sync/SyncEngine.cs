using System.Collections.Concurrent;
using System.Diagnostics;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;
using Microsoft.Extensions.Logging;

namespace Meeza.MarketingMcp.Sync;

public sealed record SyncWindow(DateOnly From, DateOnly To, bool Backfill);

public sealed record AccountSyncOutcome(
    string Integration, string ExternalId, string Status, int Records, DateOnly? LatestDataDate,
    string? Error, IReadOnlyList<string> Warnings, long DurationMs, int Windows);

/// <summary>
/// Plans and runs synchronization: incremental windows first (newest data lands fastest, re-reading recent days that
/// platforms still revise), then the historical backfill in chunks, resuming where an earlier backfill stopped.
/// Each window is committed on its own; a failure stops that account only and never deletes stored data.
/// </summary>
public sealed class SyncEngine(ProjectStore projects, SyncStore syncs, IEnumerable<IIntegrationSyncer> syncers, ILogger<SyncEngine> logger)
{
    private static readonly string Owner = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<AccountSyncOutcome>>> _running = new();
    private readonly Dictionary<string, IIntegrationSyncer> _syncers = syncers.ToDictionary(s => s.Integration);

    public IReadOnlyCollection<string> Running => _running.Keys.ToArray();
    public bool IsRunning(string projectId) => _running.ContainsKey(projectId);
    public IIntegrationSyncer? Syncer(string integration) => _syncers.GetValueOrDefault(integration);

    /// <summary>Starts a project sync in the background (or returns the one already running).</summary>
    public Task<IReadOnlyList<AccountSyncOutcome>> StartProjectSync(string projectId, string trigger, string? integration = null) =>
        _running.GetOrAdd(projectId, id => Task.Run(async () =>
        {
            try { return await SyncProjectAsync(id, trigger, integration, CancellationToken.None).ConfigureAwait(false); }
            finally { _running.TryRemove(id, out _); }
        }));

    public async Task<IReadOnlyList<AccountSyncOutcome>> SyncProjectAsync(string projectId, string trigger, string? integration, CancellationToken ct)
    {
        var project = projects.Require(projectId);
        var outcomes = new List<AccountSyncOutcome>();
        foreach (var conn in project.Connections.Where(c => Integrations.Synced.Contains(c.Integration) && (integration is null || c.Integration == integration)))
        {
            if (!_syncers.TryGetValue(conn.Integration, out var syncer)) continue;
            outcomes.Add(await SyncAccountAsync(project, syncer, conn.ExternalId, trigger, DateTimeOffset.UtcNow, ct).ConfigureAwait(false));
        }
        return outcomes;
    }

    /// <summary>Windows to fetch, newest first. Pure function so planning is unit-tested.</summary>
    public static IReadOnlyList<SyncWindow> Plan(DateOnly today, int backfillDays, int lookbackDays, int chunkDays, string? latestDataDate, string? backfilledFrom)
    {
        var targetStart = today.AddDays(-(backfillDays - 1));
        DateOnly? latest = latestDataDate is null ? null : Db.Date(latestDataDate);
        DateOnly? filledFrom = backfilledFrom is null ? null : Db.Date(backfilledFrom);

        // Incremental: from (latest known data or today, whichever is earlier) minus the lookback, through today.
        var anchor = latest is { } l && l < today ? l : today;
        var incFrom = anchor.AddDays(-lookbackDays);
        if (filledFrom is null) incFrom = Max(targetStart, today.AddDays(-lookbackDays)); // first run: fresh data first, history after
        incFrom = Max(incFrom, targetStart);

        var windows = new List<SyncWindow>();
        windows.AddRange(Chunks(incFrom, today, chunkDays, backfill: false));
        var backfillEnd = (filledFrom is { } f && f < incFrom ? f : incFrom).AddDays(-1);
        if (filledFrom is null || filledFrom > targetStart)
            windows.AddRange(Chunks(targetStart, backfillEnd, chunkDays, backfill: true));
        return windows;
    }

    private static IEnumerable<SyncWindow> Chunks(DateOnly from, DateOnly to, int size, bool backfill)
    {
        for (var end = to; end >= from; end = end.AddDays(-size))
        {
            var start = Max(from, end.AddDays(-(size - 1)));
            yield return new SyncWindow(start, end, backfill);
        }
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(45);

    /// <summary>
    /// Takes the account lease. A lease held by a process that no longer runs (the app was closed mid-sync) is taken
    /// over immediately instead of blocking every sync until it expires, and that process's run is marked interrupted.
    /// </summary>
    private bool Acquire(string lease, string projectId, string integration, string externalId)
    {
        if (syncs.TryAcquire(lease, Owner, LeaseTtl)) return true;
        var holder = syncs.LeaseOwner(lease);
        if (holder is null) return syncs.TryAcquire(lease, Owner, LeaseTtl);
        if (OwnerAlive(holder) || !syncs.TakeOver(lease, holder, Owner, LeaseTtl)) return false;
        syncs.MarkInterrupted(projectId, integration, externalId);
        logger.LogInformation("Took over the {Integration} {Account} sync lease from a process that is no longer running.", integration, externalId);
        return true;
    }

    /// <summary>Lease owners are "{processId}-{guid}". Unknown formats and this process's own leases count as alive.</summary>
    internal static bool OwnerAlive(string owner)
    {
        var dash = owner.IndexOf('-');
        if (dash <= 0 || !int.TryParse(owner[..dash], out var pid)) return true;
        if (pid == Environment.ProcessId) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return !p.HasExited && string.Equals(p.ProcessName, me.ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }          // no such process
        catch (InvalidOperationException) { return false; }  // exited while we looked
    }

    private async Task<AccountSyncOutcome> SyncAccountAsync(Project project, IIntegrationSyncer syncer, string externalId, string trigger, DateTimeOffset now, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var lease = $"{project.Id}|{syncer.Integration}|{externalId}";
        if (!Acquire(lease, project.Id, syncer.Integration, externalId))
            return new AccountSyncOutcome(syncer.Integration, externalId, "skipped", 0, null, "Another sync of this account is already running.", [], 0, 0);

        long? runId = null;
        try
        {
            if (!syncer.IsConfigured)
            {
                runId = syncs.StartRun(project.Id, syncer.Integration, externalId, "incremental", trigger, null, null);
                var msg = $"{syncer.Integration} credentials are not configured. {syncer.ConfigurationHint}";
                syncs.FinishRun(runId.Value, "failed", 0, null, null, msg, [], sw.ElapsedMilliseconds);
                return new AccountSyncOutcome(syncer.Integration, externalId, "failed", 0, null, msg, [], sw.ElapsedMilliseconds, 0);
            }

            AccountInfo info;
            var state = syncs.State(project.Id, syncer.Integration, externalId);
            try
            {
                info = await syncer.DescribeAsync(externalId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                runId = syncs.StartRun(project.Id, syncer.Integration, externalId, "incremental", trigger, null, null);
                var msg = ToolRunner.Describe(ex);
                syncs.FinishRun(runId.Value, "failed", 0, null, null, msg, [], sw.ElapsedMilliseconds);
                return new AccountSyncOutcome(syncer.Integration, externalId, "failed", 0, null, msg, [], sw.ElapsedMilliseconds, 0);
            }

            var today = TimeZones.Today(info.TimeZone ?? project.TimeZone, now);
            var plan = Plan(today, project.BackfillDays, syncer.LookbackDays, syncer.ChunkDays, state?.LatestDataDate, state?.BackfilledFrom);
            var kind = plan.Any(w => w.Backfill) ? "backfill" : "incremental";
            runId = syncs.StartRun(project.Id, syncer.Integration, externalId, kind, trigger, plan.Min(w => w.From), plan.Max(w => w.To));

            var records = 0;
            DateOnly? latest = null, filledFrom = null;
            var warnings = new List<string>();
            string? error = null;
            var done = 0;
            foreach (var w in plan)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var r = await syncer.SyncWindowAsync(project.Id, info, w.From, w.To, project, ct).ConfigureAwait(false);
                    records += r.Records;
                    if (r.LatestActivityDate is { } d && (latest is null || d > latest)) latest = d;
                    foreach (var warn in r.Warnings.Where(x => !warnings.Contains(x))) warnings.Add(warn);
                    filledFrom = filledFrom is null || w.From < filledFrom ? w.From : filledFrom;
                    done++;
                    // Save progress and keep the lease alive, so a long backfill survives restarts and never expires mid-run.
                    syncs.Checkpoint(project.Id, syncer.Integration, externalId, latest, filledFrom);
                    syncs.TryAcquire(lease, Owner, LeaseTtl);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = $"{Db.Iso(w.From)}..{Db.Iso(w.To)}: {ToolRunner.Describe(ex)}";
                    logger.LogWarning("Sync {Integration} {Account} window {From}..{To} failed: {Error}", syncer.Integration, externalId, w.From, w.To, error);
                    break; // later windows would very likely fail the same way; stored data stays untouched
                }
            }

            // A completed backfill covers through the project start even when the earliest days had no activity.
            var status = error is null ? "success" : done > 0 ? "partial" : "failed";
            syncs.FinishRun(runId.Value, status, records, latest, filledFrom, error, warnings, sw.ElapsedMilliseconds);
            if (info.Currency is not null || info.TimeZone is not null || info.DisplayName is not null)
                projects.SetConnections(project.Id, projects.Require(project.Id).Connections
                    .Select(c => c.Integration == syncer.Integration && c.ExternalId == externalId
                        ? c with { DisplayName = c.DisplayName ?? info.DisplayName, Currency = info.Currency ?? c.Currency, TimeZone = info.TimeZone ?? c.TimeZone }
                        : c).ToArray());
            return new AccountSyncOutcome(syncer.Integration, externalId, status, records, latest, error, warnings, sw.ElapsedMilliseconds, done);
        }
        catch (OperationCanceledException)
        {
            if (runId is { } id) syncs.FinishRun(id, "failed", 0, null, null, "Cancelled.", [], sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            syncs.Release(lease, Owner);
        }
    }
}
