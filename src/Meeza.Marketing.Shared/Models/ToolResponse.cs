namespace Meeza.Marketing.Shared.Models;

/// <summary>
/// Standard envelope returned by every MCP tool so Claude always sees the same shape:
/// what was asked, which data came back, and which caveats apply.
/// </summary>
public sealed class ToolResponse
{
    public required string Tool { get; init; }
    public bool Ok { get; set; } = true;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? DateRange { get; set; }
    public object? Data { get; set; }

    /// <summary>Per-platform failures that did not abort the whole request.</summary>
    public List<PlatformError> Errors { get; init; } = [];

    /// <summary>Caveats about attribution, currencies, time zones and missing data.</summary>
    public List<string> Notes { get; init; } = [];
}

public sealed record PlatformError(string Platform, string Message);
