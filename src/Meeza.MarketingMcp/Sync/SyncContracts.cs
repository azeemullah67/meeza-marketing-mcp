using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Sync;

/// <summary>Account facts a syncer needs before planning windows.</summary>
public sealed record AccountInfo(string ExternalId, string? DisplayName, string? Currency, string? TimeZone);

/// <summary>What one window sync produced.</summary>
public sealed class WindowResult
{
    public int Records { get; set; }
    /// <summary>Latest date that has real activity (spend/impressions or active users). Null when the window had none.</summary>
    public DateOnly? LatestActivityDate { get; set; }
    public List<string> Warnings { get; } = [];

    public void Add(WriteResult w, string what)
    {
        Records += w.Written;
        if (w.Warning is not null) Warnings.Add($"{what}: {w.Warning}");
    }

    public void Seen(DateOnly d)
    {
        if (LatestActivityDate is null || d > LatestActivityDate) LatestActivityDate = d;
    }
}

/// <summary>
/// One integration's synchronizer. Implementations fetch a complete date window and write it through
/// <see cref="FactStore"/>; they must throw (and write nothing) when a required part of the window could not be fetched.
/// </summary>
public interface IIntegrationSyncer
{
    string Integration { get; }
    bool IsConfigured { get; }
    string ConfigurationHint { get; }

    /// <summary>Days re-fetched on every incremental run, because platforms revise recent days (delayed conversions, GA4 processing, cohort maturation).</summary>
    int LookbackDays { get; }

    /// <summary>Largest window per request batch (keeps responses within API limits).</summary>
    int ChunkDays { get; }

    Task<AccountInfo> DescribeAsync(string externalId, CancellationToken ct);

    Task<WindowResult> SyncWindowAsync(string projectId, AccountInfo account, DateOnly from, DateOnly to, Project project, CancellationToken ct);
}

public static class TimeZones
{
    /// <summary>Today's calendar date in an IANA time zone (falls back to the computer's zone).</summary>
    public static DateOnly Today(string? ianaZone, DateTimeOffset? now = null)
    {
        var utc = now ?? DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(ianaZone) && TimeZoneInfo.TryFindSystemTimeZoneById(ianaZone, out var tz))
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);
        return DateOnly.FromDateTime(utc.ToLocalTime().DateTime);
    }
}
