using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.Marketing.Meta.Models;

/// <summary>Converts raw Graph API payloads into the shared, normalized models.</summary>
public static class MetaMapping
{
    /// <summary>
    /// App install action types in order of preference. "mobile_app_install" is the install event
    /// attributed by Meta (SDK / MMP); "omni_app_install" combines app and web install events.
    /// </summary>
    public static readonly string[] InstallActionTypes = ["mobile_app_install", "app_install", "omni_app_install"];

    // Currencies Meta reports budgets for without a 1/100 offset.
    private static readonly HashSet<string> ZeroOffsetCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLP", "COP", "CRC", "HUF", "ISK", "IDR", "JPY", "KRW", "PYG", "TWD", "VND",
    };

    public static AdAccount ToAccount(JsonElement a) => new(
        MetaAdsOptions.Platform,
        a.Str("id") ?? "act_" + a.Str("account_id"),
        a.Str("name"),
        a.Str("currency"),
        a.Str("timezone_name"),
        AccountStatus(a.Long("account_status")));

    public static string? AccountStatus(long? code) => code switch
    {
        null => null,
        1 => "ACTIVE",
        2 => "DISABLED",
        3 => "UNSETTLED",
        7 => "PENDING_RISK_REVIEW",
        8 => "PENDING_SETTLEMENT",
        9 => "IN_GRACE_PERIOD",
        100 => "PENDING_CLOSURE",
        101 => "CLOSED",
        201 => "ANY_ACTIVE",
        202 => "ANY_CLOSED",
        _ => code.ToString(),
    };

    /// <summary>Converts a Meta budget (minor units, as a string) to a decimal amount in the account currency.</summary>
    public static decimal? Budget(JsonElement e, string field, string? currency)
    {
        var minor = e.Dec(field);
        if (minor is null or 0) return null;
        return currency is not null && ZeroOffsetCurrencies.Contains(currency) ? minor : minor / 100m;
    }

    /// <summary>Flattens an "actions" style array into action_type → value.</summary>
    public static Dictionary<string, decimal> Actions(JsonElement row, string field = "actions")
    {
        var map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in row.Arr(field))
        {
            var type = a.Str("action_type");
            var value = a.Dec("value");
            if (type is not null && value is not null) map[type] = value.Value;
        }
        return map;
    }

    /// <summary>Returns the attributed install count and the action type it came from, or (null, null).</summary>
    public static (decimal? Installs, string? Source) Installs(IReadOnlyDictionary<string, decimal> actions)
    {
        foreach (var t in InstallActionTypes)
            if (actions.TryGetValue(t, out var v)) return (v, $"meta_attributed:{t}");
        return (null, null);
    }

    public static CampaignPerformance ToPerformance(JsonElement row, DateRange range, string? timeZone, string? fallbackAccountId = null, ReportLevel level = ReportLevel.Campaign)
    {
        var actions = Actions(row);
        var (installs, source) = Installs(actions);
        var accountId = row.Str("account_id") is { } id ? "act_" + id : fallbackAccountId ?? "unknown";
        var notes = installs is null
            ? new List<string> { "No install actions reported: either zero installs, or install tracking (Meta SDK / MMP) is not configured for this campaign." }
            : null;

        return new CampaignPerformance
        {
            Platform = MetaAdsOptions.Platform,
            AccountId = accountId,
            AccountName = row.Str("account_name"),
            CampaignId = row.Str("campaign_id"),
            CampaignName = row.Str("campaign_name"),
            Objective = row.Str("objective"),
            Level = level.Name(),
            AdSetId = row.Str("adset_id"),
            AdSetName = row.Str("adset_name"),
            AdId = row.Str("ad_id"),
            AdName = row.Str("ad_name"),
            Country = row.Str("country"),
            Spend = row.Dec("spend"),
            Currency = row.Str("account_currency") ?? "UNKNOWN",
            Impressions = row.Long("impressions"),
            Reach = row.Long("reach"),
            Clicks = row.Long("clicks"),
            Installs = installs,
            InstallSource = source,
            StartDate = ParseDate(row.Str("date_start")) ?? range.Start,
            EndDate = ParseDate(row.Str("date_stop")) ?? range.End,
            TimeZone = timeZone,
            Notes = notes,
        }.WithDerivedMetrics();
    }

    public static DailyPerformance ToDaily(JsonElement row, string accountId, string? currency) => new(
        MetaAdsOptions.Platform,
        accountId,
        row.Str("account_currency") ?? currency ?? "UNKNOWN",
        ParseDate(row.Str("date_start")) ?? DateOnly.MinValue,
        row.Dec("spend"),
        row.Long("impressions"),
        row.Long("clicks"),
        Installs(Actions(row)).Installs);

    private static DateOnly? ParseDate(string? s) => DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
