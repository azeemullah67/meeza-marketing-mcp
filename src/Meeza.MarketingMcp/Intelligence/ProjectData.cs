using Meeza.MarketingMcp.Storage;
using Microsoft.Data.Sqlite;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>Request filters shared by every analysis. Null = no filter.</summary>
public sealed record Filters(DateOnly Start, DateOnly End, string? Country = null, string? Platform = null, string? Campaign = null, string? Channel = null)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;
    public Filters Previous() => this with { Start = Start.AddDays(-Days), End = Start.AddDays(-1) };
    public string Range => $"{Db.Iso(Start)}..{Db.Iso(End)}";
}

public sealed record AdFact(
    DateOnly Date, string Platform, string AccountId, string CampaignId, string? CampaignName, string? CampaignStatus, string? Channel,
    string AdsetId, string? AdsetName, string? AdsetStatus, string AdId, string? AdName, string? AdStatus, string Currency,
    decimal? Spend, long? Impressions, long? Reach, long? Clicks, decimal? Conversions, decimal? Installs, decimal? ConversionValue,
    long? VideoViews, long? VideoP25, long? VideoP100, long? Thruplays);

public sealed record CountryFact(
    DateOnly Date, string Platform, string AccountId, string CampaignId, string? CampaignName, string Country, string Currency,
    decimal? Spend, long? Impressions, long? Clicks, decimal? Conversions, decimal? Installs);

public sealed record AssetFact(
    DateOnly Date, string Platform, string AccountId, string? CampaignId, string AdsetId, string AdId, string AssetId, string FieldType, string Currency,
    decimal? Spend, long? Impressions, long? Clicks, decimal? Installs);

public sealed record Ga4Day(
    DateOnly Date, string PropertyId, long? ActiveUsers, long? NewUsers, long? Active7d, long? Active28d, long? Sessions,
    decimal? EngagementSeconds, decimal? TotalRevenue, decimal? AdRevenue, decimal? PurchaseRevenue, long? Purchasers, string? Currency);

public sealed record Ga4Dim(DateOnly Date, string Dimension, string Value, long? ActiveUsers, long? NewUsers, decimal? TotalRevenue);

public sealed record Ga4Event(DateOnly Date, string EventName, long? EventCount, long? Users);

public sealed record CohortCell(string SegmentType, string SegmentValue, DateOnly CohortDate, int DayN, long ActiveUsers, long CohortUsers);

public sealed record CreativeMeta(
    string Platform, string AccountId, string CreativeKey, string? AdId, string? AssetId, string? Name, string? Format, string? FieldType,
    string? PreviewUrl, string? ThumbnailUrl, string? VideoId, decimal? DurationSeconds, string? Title, string? Body, string? PerformanceLabel);

public sealed record ReachWindow(string Platform, string AccountId, DateOnly WindowStart, DateOnly WindowEnd, long? Reach, long? Impressions, decimal? Frequency);

/// <summary>Read side of the store. Every query is scoped to one project, which is what keeps projects isolated.</summary>
public sealed class ProjectData(MarketingDb db)
{
    public IReadOnlyList<AdFact> Ads(string projectId, Filters f, bool applyPlatformAndCampaign = true)
    {
        using var c = db.Open();
        var (where, args) = Where(projectId, f, applyPlatformAndCampaign);
        return c.Query($"SELECT * FROM ad_daily WHERE {where}", args, r => new AdFact(
            Db.Date(r.Str("date")!), r.Str("platform")!, r.Str("account_id")!, r.Str("campaign_id")!, r.Str("campaign_name"), r.Str("campaign_status"),
            r.Str("channel"), r.Str("adset_id")!, r.Str("adset_name"), r.Str("adset_status"), r.Str("ad_id")!, r.Str("ad_name"), r.Str("ad_status"), r.Str("currency")!,
            r.Dec("spend"), r.Long("impressions"), r.Long("reach"), r.Long("clicks"), r.Dec("conversions"), r.Dec("installs"), r.Dec("conversion_value"),
            r.Long("video_views"), r.Long("video_p25"), r.Long("video_p100"), r.Long("thruplays")));
    }

    public IReadOnlyList<CountryFact> Countries(string projectId, Filters f)
    {
        using var c = db.Open();
        var (where, args) = Where(projectId, f, true);
        if (f.Country is not null) { where += " AND country=@country"; args["country"] = f.Country.ToUpperInvariant(); }
        return c.Query($"SELECT * FROM campaign_country_daily WHERE {where}", args, r => new CountryFact(
            Db.Date(r.Str("date")!), r.Str("platform")!, r.Str("account_id")!, r.Str("campaign_id")!, r.Str("campaign_name"), r.Str("country")!,
            r.Str("currency")!, r.Dec("spend"), r.Long("impressions"), r.Long("clicks"), r.Dec("conversions"), r.Dec("installs")));
    }

    public IReadOnlyList<AssetFact> Assets(string projectId, Filters f)
    {
        using var c = db.Open();
        var (where, args) = Where(projectId, f, true);
        return c.Query($"SELECT * FROM asset_daily WHERE {where}", args, r => new AssetFact(
            Db.Date(r.Str("date")!), r.Str("platform")!, r.Str("account_id")!, r.Str("campaign_id"), r.Str("adset_id")!, r.Str("ad_id")!,
            r.Str("asset_id")!, r.Str("field_type")!, r.Str("currency")!, r.Dec("spend"), r.Long("impressions"), r.Long("clicks"), r.Dec("installs")));
    }

    public IReadOnlyList<CreativeMeta> Creatives(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM creatives WHERE project_id=@projectId", new { projectId }, r => new CreativeMeta(
            r.Str("platform")!, r.Str("account_id")!, r.Str("creative_key")!, r.Str("ad_id"), r.Str("asset_id"), r.Str("name"), r.Str("format"),
            r.Str("field_type"), r.Str("preview_url"), r.Str("thumbnail_url"), r.Str("video_id"), r.Dec("duration_seconds"), r.Str("title"),
            r.Str("body"), r.Str("performance_label")));
    }

    public IReadOnlyList<ReachWindow> Reach(string projectId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM reach_windows WHERE project_id=@projectId", new { projectId }, r => new ReachWindow(
            r.Str("platform")!, r.Str("account_id")!, Db.Date(r.Str("window_start")!), Db.Date(r.Str("window_end")!),
            r.Long("reach"), r.Long("impressions"), r.Dec("frequency")));
    }

    public IReadOnlyList<Ga4Day> Ga4Daily(string projectId, DateOnly start, DateOnly end)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM ga4_daily WHERE project_id=@projectId AND date BETWEEN @start AND @end", new { projectId, start, end }, r => new Ga4Day(
            Db.Date(r.Str("date")!), r.Str("property_id")!, r.Long("active_users"), r.Long("new_users"), r.Long("active_7d"), r.Long("active_28d"),
            r.Long("sessions"), r.Dec("engagement_seconds"), r.Dec("total_revenue"), r.Dec("ad_revenue"), r.Dec("purchase_revenue"),
            r.Long("purchasers"), r.Str("currency")));
    }

    public IReadOnlyList<Ga4Dim> Ga4Dims(string projectId, DateOnly start, DateOnly end, string? dimension = null)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM ga4_dim_daily WHERE project_id=@projectId AND date BETWEEN @start AND @end" + (dimension is null ? "" : " AND dimension=@dimension"),
            new { projectId, start, end, dimension }, r => new Ga4Dim(Db.Date(r.Str("date")!), r.Str("dimension")!, r.Str("value")!,
                r.Long("active_users"), r.Long("new_users"), r.Dec("total_revenue")));
    }

    public IReadOnlyList<Ga4Event> Ga4Events(string projectId, DateOnly start, DateOnly end)
    {
        using var c = db.Open();
        return c.Query("SELECT date, event_name, SUM(event_count) AS event_count, SUM(users) AS users FROM ga4_events_daily WHERE project_id=@projectId AND date BETWEEN @start AND @end GROUP BY date, event_name",
            new { projectId, start, end }, r => new Ga4Event(Db.Date(r.Str("date")!), r.Str("event_name")!, r.Long("event_count"), r.Long("users")));
    }

    /// <summary>Cohort cells summed across the project's GA4 properties.</summary>
    public IReadOnlyList<CohortCell> Cohorts(string projectId, string segmentType, DateOnly start, DateOnly end)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT segment_type, segment_value, cohort_date, day_n, SUM(active_users) AS active_users, SUM(cohort_users) AS cohort_users
            FROM ga4_cohorts WHERE project_id=@projectId AND segment_type=@segmentType AND cohort_date BETWEEN @start AND @end
            GROUP BY segment_type, segment_value, cohort_date, day_n
            """, new { projectId, segmentType, start, end }, r => new CohortCell(r.Str("segment_type")!, r.Str("segment_value")!,
                Db.Date(r.Str("cohort_date")!), (int)r.Long("day_n")!.Value, r.Long("active_users") ?? 0, r.Long("cohort_users") ?? 0));
    }

    /// <summary>Distinct filter values present in the project's data (for filter dropdowns).</summary>
    public object FilterOptions(string projectId)
    {
        using var c = db.Open();
        List<string> L(string sql) => c.Query(sql, new { projectId }, r => r.GetString(0));
        return new
        {
            countries = L("SELECT DISTINCT country FROM campaign_country_daily WHERE project_id=@projectId UNION SELECT DISTINCT value FROM ga4_dim_daily WHERE project_id=@projectId AND dimension='country' AND value NOT IN ('(not set)','') ORDER BY 1"),
            platforms = L("SELECT DISTINCT platform FROM ad_daily WHERE project_id=@projectId ORDER BY 1"),
            campaigns = c.Query("SELECT campaign_id, MAX(campaign_name), platform FROM ad_daily WHERE project_id=@projectId GROUP BY campaign_id, platform ORDER BY 2",
                new { projectId }, r => new { id = r.GetString(0), name = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1), platform = r.GetString(2) }),
            channels = L("SELECT DISTINCT value FROM ga4_dim_daily WHERE project_id=@projectId AND dimension='channel' ORDER BY 1"),
            dataRange = c.Query("""
                SELECT MIN(d), MAX(d) FROM (SELECT MIN(date) d FROM ad_daily WHERE project_id=@projectId UNION ALL SELECT MAX(date) FROM ad_daily WHERE project_id=@projectId
                UNION ALL SELECT MIN(date) FROM ga4_daily WHERE project_id=@projectId UNION ALL SELECT MAX(date) FROM ga4_daily WHERE project_id=@projectId)
                """, new { projectId }, r => new { min = r.IsDBNull(0) ? null : r.GetString(0), max = r.IsDBNull(1) ? null : r.GetString(1) }).FirstOrDefault(),
        };
    }

    private static (string Where, Dictionary<string, object?> Args) Where(string projectId, Filters f, bool platformAndCampaign)
    {
        var where = "project_id=@projectId AND date BETWEEN @start AND @end";
        var args = new Dictionary<string, object?> { ["projectId"] = projectId, ["start"] = Db.Iso(f.Start), ["end"] = Db.Iso(f.End) };
        if (platformAndCampaign && f.Platform is not null) { where += " AND platform=@platform"; args["platform"] = f.Platform; }
        if (platformAndCampaign && f.Campaign is not null) { where += " AND campaign_id=@campaign"; args["campaign"] = f.Campaign; }
        return (where, args);
    }
}
