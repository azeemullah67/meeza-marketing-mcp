namespace Meeza.Marketing.Shared.Models;

/// <summary>Granularity of a performance report. "AdSet" means a Meta ad set or a Google Ads ad group.</summary>
public enum ReportLevel
{
    Campaign,
    AdSet,
    Ad,
}

public static class ReportLevels
{
    public static ReportLevel Parse(string? value) => (value ?? "campaign").Trim().ToLowerInvariant().Replace("_", "").Replace("-", "") switch
    {
        "campaign" or "campaigns" => ReportLevel.Campaign,
        "adset" or "adsets" or "adgroup" or "adgroups" => ReportLevel.AdSet,
        "ad" or "ads" => ReportLevel.Ad,
        _ => throw new ArgumentException("level must be campaign, adset (ad group) or ad."),
    };

    public static string Name(this ReportLevel level) => level switch
    {
        ReportLevel.AdSet => "adset",
        ReportLevel.Ad => "ad",
        _ => "campaign",
    };
}

/// <summary>One day of account-level delivery for trend charts. Unavailable values are null.</summary>
public sealed record DailyPerformance(
    string Platform,
    string AccountId,
    string Currency,
    DateOnly Date,
    decimal? Spend,
    long? Impressions,
    long? Clicks,
    decimal? Installs);
