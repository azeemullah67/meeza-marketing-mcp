using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.GoogleAds.Models;

/// <summary>
/// GAQL queries. Only validated values (ISO dates, numeric IDs, fixed enum names) are ever
/// interpolated, so user input cannot alter query structure.
/// </summary>
public static class GoogleAdsQueries
{
    public const string Customer =
        "SELECT customer.id, customer.descriptive_name, customer.currency_code, customer.time_zone, customer.manager, customer.status FROM customer LIMIT 1";

    public const string CustomerClients =
        "SELECT customer_client.id, customer_client.descriptive_name, customer_client.currency_code, customer_client.time_zone, " +
        "customer_client.manager, customer_client.status, customer_client.level FROM customer_client WHERE customer_client.level <= 1";

    public static string Campaigns(bool includeRemoved) =>
        "SELECT campaign.id, campaign.name, campaign.status, campaign.serving_status, campaign.advertising_channel_type, " +
        "campaign.advertising_channel_sub_type, campaign.bidding_strategy_type, campaign_budget.amount_micros, " +
        "campaign.app_campaign_setting.app_id, campaign.app_campaign_setting.app_store " +
        "FROM campaign" + (includeRemoved ? "" : " WHERE campaign.status != 'REMOVED'") + " ORDER BY campaign.name";

    public static string CampaignPerformance(DateRange r, bool daily) =>
        "SELECT campaign.id, campaign.name, campaign.status, campaign.advertising_channel_type, campaign.advertising_channel_sub_type, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions, metrics.all_conversions, metrics.conversions_value" +
        (daily ? ", segments.date" : "") +
        $" FROM campaign WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public static string AdGroupPerformance(DateRange r) =>
        "SELECT campaign.id, campaign.name, ad_group.id, ad_group.name, ad_group.status, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions, metrics.all_conversions " +
        $"FROM ad_group WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public static string AdPerformance(DateRange r) =>
        "SELECT campaign.id, campaign.name, campaign.status, campaign.advertising_channel_type, campaign.advertising_channel_sub_type, " +
        "ad_group.id, ad_group.name, ad_group_ad.ad.id, ad_group_ad.ad.name, ad_group_ad.ad.type, ad_group_ad.status, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions, metrics.all_conversions " +
        $"FROM ad_group_ad WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public static string AdGroupInstalls(DateRange r) =>
        "SELECT ad_group.id, segments.conversion_action_category, metrics.conversions " +
        $"FROM ad_group WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND segments.conversion_action_category = 'DOWNLOAD'";

    public static string AdInstalls(DateRange r) =>
        "SELECT ad_group.id, ad_group_ad.ad.id, segments.conversion_action_category, metrics.conversions " +
        $"FROM ad_group_ad WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND segments.conversion_action_category = 'DOWNLOAD'";

    /// <summary>
    /// Per-asset results inside each ad (headlines, descriptions, images, videos of App and other asset-based ads),
    /// with Google's own performance label.
    /// </summary>
    public static string AssetPerformance(DateRange r) =>
        "SELECT campaign.id, campaign.name, ad_group.id, ad_group.name, ad_group_ad.ad.id, " +
        "ad_group_ad_asset_view.field_type, ad_group_ad_asset_view.performance_label, ad_group_ad_asset_view.enabled, " +
        "asset.id, asset.name, asset.type, asset.text_asset.text, asset.youtube_video_asset.youtube_video_id, " +
        "asset.youtube_video_asset.youtube_video_title, asset.image_asset.full_size.url, " +
        "asset.image_asset.full_size.width_pixels, asset.image_asset.full_size.height_pixels, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions " +
        $"FROM ad_group_ad_asset_view WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    /// <summary>
    /// Per-asset conversions split by conversion action. ad_group_ad_asset_view does not accept
    /// segments.conversion_action_category, so installs are matched against the DOWNLOAD action IDs instead.
    /// </summary>
    public static string AssetConversionsByAction(DateRange r) =>
        "SELECT ad_group.id, ad_group_ad.ad.id, asset.id, ad_group_ad_asset_view.field_type, segments.conversion_action, metrics.conversions " +
        $"FROM ad_group_ad_asset_view WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public static string DailyAccount(DateRange r) =>
        "SELECT segments.date, metrics.cost_micros, metrics.impressions, metrics.clicks " +
        $"FROM customer WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public static string DailyInstalls(DateRange r) =>
        "SELECT segments.date, segments.conversion_action_category, metrics.conversions " +
        $"FROM customer WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND segments.conversion_action_category = 'DOWNLOAD'";

    public static string CampaignInstalls(DateRange r) =>
        "SELECT campaign.id, segments.conversion_action_category, metrics.conversions, metrics.all_conversions " +
        $"FROM campaign WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND segments.conversion_action_category = 'DOWNLOAD'";

    public static string ConversionsByAction(DateRange r) =>
        "SELECT campaign.id, campaign.name, segments.conversion_action_name, segments.conversion_action_category, " +
        "metrics.conversions, metrics.all_conversions, metrics.conversions_value, metrics.all_conversions_value " +
        $"FROM campaign WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    public const string InstallConversionActions =
        "SELECT conversion_action.id, conversion_action.name, conversion_action.category, conversion_action.status, " +
        "conversion_action.type, conversion_action.primary_for_goal FROM conversion_action " +
        "WHERE conversion_action.category = 'DOWNLOAD' AND conversion_action.status = 'ENABLED'";

    public static string CountryPerformance(DateRange r) =>
        "SELECT campaign.id, campaign.name, campaign.status, geographic_view.country_criterion_id, geographic_view.location_type, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions, metrics.all_conversions " +
        $"FROM geographic_view WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND geographic_view.location_type = 'LOCATION_OF_PRESENCE'";

    public static string CountryInstalls(DateRange r) =>
        "SELECT campaign.id, geographic_view.country_criterion_id, geographic_view.location_type, segments.conversion_action_category, metrics.conversions " +
        $"FROM geographic_view WHERE segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}' AND geographic_view.location_type = 'LOCATION_OF_PRESENCE' AND segments.conversion_action_category = 'DOWNLOAD'";

    // ---------------------------------------------------------------- synchronization (daily-segmented)

    private static string During(DateRange r) => $"segments.date BETWEEN '{r.StartIso}' AND '{r.EndIso}'";

    /// <summary>Ad-level daily metrics with campaign / ad group / ad identity and status.</summary>
    public static string AdDaily(DateRange r) =>
        "SELECT segments.date, campaign.id, campaign.name, campaign.status, campaign.advertising_channel_type, campaign.advertising_channel_sub_type, " +
        "ad_group.id, ad_group.name, ad_group.status, ad_group_ad.ad.id, ad_group_ad.ad.name, ad_group_ad.ad.type, ad_group_ad.status, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions, metrics.conversions_value " +
        $"FROM ad_group_ad WHERE {During(r)}";

    /// <summary>Ad-level daily installs (conversions of DOWNLOAD-category actions).</summary>
    public static string AdInstallsDaily(DateRange r) =>
        "SELECT segments.date, ad_group.id, ad_group_ad.ad.id, segments.conversion_action_category, metrics.conversions " +
        $"FROM ad_group_ad WHERE {During(r)} AND segments.conversion_action_category = 'DOWNLOAD'";

    /// <summary>Ad-level daily video views. Separate query so an API version without the field never blocks the core sync.</summary>
    public static string AdVideoDaily(DateRange r) =>
        "SELECT segments.date, ad_group.id, ad_group_ad.ad.id, metrics.video_trueview_views, metrics.video_quartile_p25_rate, metrics.video_quartile_p100_rate, metrics.impressions " +
        $"FROM ad_group_ad WHERE {During(r)} AND metrics.impressions > 0";

    public static string CountryDaily(DateRange r) =>
        "SELECT segments.date, campaign.id, campaign.name, geographic_view.country_criterion_id, geographic_view.location_type, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks, metrics.conversions " +
        $"FROM geographic_view WHERE {During(r)} AND geographic_view.location_type = 'LOCATION_OF_PRESENCE'";

    public static string CountryInstallsDaily(DateRange r) =>
        "SELECT segments.date, campaign.id, geographic_view.country_criterion_id, geographic_view.location_type, segments.conversion_action_category, metrics.conversions " +
        $"FROM geographic_view WHERE {During(r)} AND geographic_view.location_type = 'LOCATION_OF_PRESENCE' AND segments.conversion_action_category = 'DOWNLOAD'";

    public static string AssetDaily(DateRange r) =>
        "SELECT segments.date, campaign.id, ad_group.id, ad_group_ad.ad.id, asset.id, ad_group_ad_asset_view.field_type, " +
        "metrics.cost_micros, metrics.impressions, metrics.clicks " +
        $"FROM ad_group_ad_asset_view WHERE {During(r)}";

    public static string AssetConversionsDaily(DateRange r) =>
        "SELECT segments.date, ad_group.id, ad_group_ad.ad.id, asset.id, ad_group_ad_asset_view.field_type, segments.conversion_action, metrics.conversions " +
        $"FROM ad_group_ad_asset_view WHERE {During(r)}";

    /// <summary>Asset metadata (text, video, image) and Google's performance label for the creative library.</summary>
    public static string AssetMetadata(DateRange r) =>
        "SELECT ad_group.id, ad_group_ad.ad.id, asset.id, asset.name, asset.type, asset.text_asset.text, " +
        "asset.youtube_video_asset.youtube_video_id, asset.youtube_video_asset.youtube_video_title, " +
        "asset.image_asset.full_size.url, asset.image_asset.full_size.width_pixels, asset.image_asset.full_size.height_pixels, " +
        "ad_group_ad_asset_view.field_type, ad_group_ad_asset_view.performance_label, metrics.impressions " +
        $"FROM ad_group_ad_asset_view WHERE {During(r)}";

    public static string GeoTargets(IEnumerable<long> ids) =>
        "SELECT geo_target_constant.id, geo_target_constant.country_code, geo_target_constant.name FROM geo_target_constant " +
        $"WHERE geo_target_constant.resource_name IN ({string.Join(", ", ids.Select(i => $"'geoTargetConstants/{i}'"))})";
}

