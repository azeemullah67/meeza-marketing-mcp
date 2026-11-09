using System.Globalization;
using Meeza.MarketingMcp.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meeza.MarketingMcp.Sync;

/// <summary>
/// Background synchronization. Every few minutes it checks each project and syncs those whose interval has elapsed.
/// Runs inside the MCP server and the terminal dashboard; the database lease keeps two processes from syncing the
/// same account at once. Disable with MEEZA_SYNC=off.
/// </summary>
public sealed class SyncScheduler(ProjectStore projects, SyncStore syncs, SyncEngine engine, ILogger<SyncScheduler> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    public static bool Enabled =>
        Environment.GetEnvironmentVariable("MEEZA_SYNC")?.Trim().ToLowerInvariant() is not ("off" or "false" or "0" or "no");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;
        try
        {
            var recovered = syncs.RecoverAbandonedRuns(TimeSpan.FromMinutes(50));
            if (recovered > 0) logger.LogInformation("Marked {Count} interrupted sync run(s) as failed; they will be retried.", recovered);
        }
        catch (Exception ex) { logger.LogWarning("Sync recovery check failed: {Error}", ex.Message); }

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false); // let the host finish starting
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var p in projects.List().Where(p => Due(p, syncs.States(p.Id), DateTimeOffset.UtcNow)))
                {
                    if (engine.IsRunning(p.Id)) continue;
                    _ = engine.StartProjectSync(p.Id, "schedule");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Scheduled sync check failed: {Error}", ex.Message);
            }
            await Task.Delay(Tick, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A project is due when it has a synced connection that was never attempted or whose last attempt is older than the
    /// project's interval. Repeated failures back off exponentially (capped at 6 hours) to respect rate limits.
    /// </summary>
    public static bool Due(Project p, IReadOnlyList<SyncState> states, DateTimeOffset now)
    {
        foreach (var conn in p.Connections.Where(c => Integrations.Synced.Contains(c.Integration)))
        {
            var s = states.FirstOrDefault(x => x.Integration == conn.Integration && x.ExternalId == conn.ExternalId);
            if (s?.LastAttemptAt is null) return true;
            if (s.LastStatus == "running") continue;
            var last = DateTimeOffset.Parse(s.LastAttemptAt, CultureInfo.InvariantCulture);
            var interval = TimeSpan.FromMinutes(p.SyncIntervalMinutes);
            if (s.ConsecutiveFailures > 0)
                interval = TimeSpan.FromMinutes(Math.Min(360, 15 * Math.Pow(2, Math.Min(s.ConsecutiveFailures - 1, 5))));
            if (now - last >= interval) return true;
        }
        return false;
    }
}
