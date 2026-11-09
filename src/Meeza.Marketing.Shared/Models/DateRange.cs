using System.Globalization;

namespace Meeza.Marketing.Shared.Models;

/// <summary>
/// An inclusive calendar date range. Dates are interpreted in each platform's own
/// reporting time zone (Meta: ad account time zone, Google Ads: customer time zone,
/// Play vitals: America/Los_Angeles). Platform responses report which time zone applies.
/// </summary>
public sealed record DateRange(DateOnly Start, DateOnly End)
{
    public const string Presets =
        "today, yesterday, last_7_days, last_14_days, last_30_days, last_90_days, last_N_days, this_month, last_month";

    public int Days => End.DayNumber - Start.DayNumber + 1;

    /// <summary>The range of the same length that ends the day before this one starts (for period-over-period comparison).</summary>
    public DateRange Previous() => new(Start.AddDays(-Days), Start.AddDays(-1));

    public string StartIso => Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string EndIso => End.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves a date range from a preset or explicit start/end dates (yyyy-MM-dd).
    /// Explicit dates take precedence. Defaults to last_30_days. "last_N_days" excludes today
    /// because today's data is incomplete on every platform.
    /// </summary>
    public static DateRange Resolve(string? preset, string? startDate, string? endDate, DateOnly? today = null)
    {
        // The computer's local date: ad accounts usually report in the owner's time zone, and UTC can be a day behind.
        var now = today ?? DateOnly.FromDateTime(DateTime.Now);

        if (!string.IsNullOrWhiteSpace(startDate) || !string.IsNullOrWhiteSpace(endDate))
        {
            if (string.IsNullOrWhiteSpace(startDate) || string.IsNullOrWhiteSpace(endDate))
                throw new ArgumentException("Both startDate and endDate must be provided (yyyy-MM-dd).");
            var s = ParseDate(startDate, nameof(startDate));
            var e = ParseDate(endDate, nameof(endDate));
            if (e < s) throw new ArgumentException("endDate must be on or after startDate.");
            if (e.DayNumber - s.DayNumber > 3 * 366)
                throw new ArgumentException("Date ranges longer than 3 years are not supported.");
            return new DateRange(s, e);
        }

        var p = (preset ?? "last_30_days").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return p switch
        {
            "" => Resolve(null, null, null, now),
            "today" => new DateRange(now, now),
            "yesterday" => new DateRange(now.AddDays(-1), now.AddDays(-1)),
            "this_month" => new DateRange(new DateOnly(now.Year, now.Month, 1), now),
            "last_month" => LastMonth(now),
            _ when TryParseLastNDays(p, out var n) => new DateRange(now.AddDays(-n), now.AddDays(-1)),
            _ => throw new ArgumentException($"Unknown date preset '{preset}'. Use one of: {Presets}, or startDate/endDate.")
        };
    }

    private static DateRange LastMonth(DateOnly now)
    {
        var firstThis = new DateOnly(now.Year, now.Month, 1);
        return new DateRange(firstThis.AddMonths(-1), firstThis.AddDays(-1));
    }

    private static bool TryParseLastNDays(string p, out int n)
    {
        n = 0;
        if (!p.StartsWith("last_", StringComparison.Ordinal) || !p.EndsWith("_days", StringComparison.Ordinal)) return false;
        var mid = p["last_".Length..^"_days".Length];
        return int.TryParse(mid, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n is >= 1 and <= 1095;
    }

    private static DateOnly ParseDate(string value, string name) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw new ArgumentException($"{name} must be in yyyy-MM-dd format.");

    public override string ToString() => $"{StartIso}..{EndIso}";
}
