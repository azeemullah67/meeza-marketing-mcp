namespace Meeza.Marketing.Shared.Models;

/// <summary>Standard caveats attached to tool responses so Claude interprets the data correctly.</summary>
public static class ReportingNotes
{
    public const string Currency =
        "Amounts are in each account's original currency and are never converted. Do not add amounts across different currencies.";

    public const string NullMeansUnavailable =
        "null means the metric is unavailable or not reported, not zero. Ratios (CTR, CPC, CPM, CPI) are null when the denominator is null or zero.";

    public const string TimeZone =
        "Dates are reported in each account's own time zone (see timeZone), so day boundaries can differ between platforms.";

    public const string PlatformAttribution =
        "Ad-platform installs are attributed by each platform's own model (click/view windows, modelled conversions). " +
        "Meta and Google Ads can both claim the same install, so their totals may overlap and must not be treated as de-duplicated.";

    public const string StoreVsAttributed =
        "Play Console installs are store-reported (all sources, organic + paid) and are a different measurement from ad-platform attributed installs. Never add them together.";

    public const string AssetOverlap =
        "Asset results overlap: one impression credits several assets at once (a headline and a video), and Google breaks down only part " +
        "of an ad's results by asset. Never add asset spend or installs together or compare them with ad totals. Assets are judged by " +
        "Google's own rating (Low / Good / Best, compared with other assets of the same kind); 'Learning' means not rated yet.";

    public const string Ratios =
        "CTR is a percentage. CPM is cost per 1,000 impressions. CPI = spend / attributed installs, computed only when installs > 0.";
}
