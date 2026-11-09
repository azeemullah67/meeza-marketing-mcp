using Meeza.Marketing.Analytics;

namespace Meeza.MarketingMcp.Intelligence;

/// <summary>Maturity of one cohort × day cell.</summary>
public enum CellStatus
{
    /// <summary>cohort date + N is on or before the last complete day and outside the GA4 processing lag: final.</summary>
    Mature,
    /// <summary>Reached, but within GA4's ~48h processing window: the number can still rise.</summary>
    Provisional,
    /// <summary>The cohort has not reached day N yet. Never shown as 0%.</summary>
    Immature,
}

public sealed record HeatCell(int Day, decimal? Percent, long? ActiveUsers, CellStatus Status);
public sealed record HeatRow(DateOnly Cohort, long Users, IReadOnlyList<HeatCell> Cells);
public sealed record CurvePoint(int Day, decimal? Percent, long CohortUsers, int Cohorts, bool IncludesProvisional);
public sealed record SegmentCurve(string Segment, long Users, IReadOnlyList<CurvePoint> Curve);

/// <summary>
/// Exact-day (Day N) cohort retention from GA4 cohort cells: active on day N ÷ cohort size. Cohorts that have not reached
/// day N are excluded (not zero); cells inside the processing lag are flagged provisional.
/// </summary>
public static class RetentionAnalytics
{
    public const int MaxDay = 30;
    /// <summary>Days at the end of the data GA4 may still be processing.</summary>
    public const int ProvisionalLagDays = 2;

    public static CellStatus Status(DateOnly cohort, int day, DateOnly today)
    {
        var reached = cohort.AddDays(day);
        if (reached >= today) return CellStatus.Immature; // today is not complete
        if (reached > today.AddDays(-1 - ProvisionalLagDays)) return CellStatus.Provisional;
        return CellStatus.Mature;
    }

    public static IReadOnlyList<HeatRow> Heatmap(IEnumerable<CohortCell> cells, DateOnly today, int maxDay = MaxDay)
    {
        return cells.GroupBy(c => c.CohortDate).OrderByDescending(g => g.Key).Select(g =>
        {
            var size = g.Max(c => c.CohortUsers);
            var byDay = g.GroupBy(c => c.DayN).ToDictionary(x => x.Key, x => x.Sum(c => c.ActiveUsers));
            var row = Enumerable.Range(0, maxDay + 1).Select(d =>
            {
                var st = Status(g.Key, d, today);
                if (st == CellStatus.Immature || size == 0) return new HeatCell(d, null, null, CellStatus.Immature);
                var active = byDay.GetValueOrDefault(d); // a reached day with no row means nobody returned: a real 0
                return new HeatCell(d, Math.Round(active * 100m / size, 2), active, st);
            }).ToArray();
            return new HeatRow(g.Key, size, row);
        }).ToArray();
    }

    /// <summary>
    /// Cohort-size-weighted curve. Day N uses only cohorts that reached it; provisional cells are included only when
    /// <paramref name="includeProvisional"/> is true (and the point is flagged).
    /// </summary>
    public static IReadOnlyList<CurvePoint> Curve(IEnumerable<CohortCell> cells, DateOnly today, bool includeProvisional = false, int maxDay = MaxDay)
    {
        var rows = Heatmap(cells, today, maxDay);
        return Enumerable.Range(0, maxDay + 1).Select(d =>
        {
            var usable = rows.Select(r => (r.Users, Cell: r.Cells[d]))
                .Where(x => x.Users > 0 && (x.Cell.Status == CellStatus.Mature || includeProvisional && x.Cell.Status == CellStatus.Provisional))
                .ToArray();
            var users = usable.Sum(x => x.Users);
            if (users == 0) return new CurvePoint(d, null, 0, 0, false);
            var active = usable.Sum(x => x.Cell.ActiveUsers ?? 0);
            return new CurvePoint(d, Math.Round(active * 100m / users, 2), users, usable.Length, usable.Any(x => x.Cell.Status == CellStatus.Provisional));
        }).ToArray();
    }

    public static decimal? On(IReadOnlyList<CurvePoint> curve, int day) => curve.FirstOrDefault(p => p.Day == day)?.Percent;

    /// <summary>Curves for the largest segments (e.g. top countries), for cohort comparison.</summary>
    public static IReadOnlyList<SegmentCurve> BySegment(IEnumerable<CohortCell> cells, DateOnly today, int top = 8) =>
        cells.GroupBy(c => c.SegmentValue)
            .Select(g => (Segment: g.Key, Users: g.GroupBy(c => c.CohortDate).Sum(x => x.Max(c => c.CohortUsers)), Cells: g.ToArray()))
            .Where(x => x.Users > 0)
            .OrderByDescending(x => x.Users).Take(top)
            .Select(x => new SegmentCurve(x.Segment, x.Users, Curve(x.Cells, today)))
            .ToArray();

    /// <summary>D1 / D7 per cohort date (mature cells only), for the churn trend.</summary>
    public static IReadOnlyList<object> ChurnTrend(IReadOnlyList<HeatRow> rows) =>
        rows.OrderBy(r => r.Cohort).Select(r => (object)new
        {
            cohort = r.Cohort, users = r.Users,
            d1 = r.Cells[1].Status == CellStatus.Immature ? null : r.Cells[1].Percent,
            d1Status = r.Cells[1].Status.ToString().ToLowerInvariant(),
            d7 = r.Cells.Count > 7 && r.Cells[7].Status != CellStatus.Immature ? r.Cells[7].Percent : null,
            d7Status = r.Cells.Count > 7 ? r.Cells[7].Status.ToString().ToLowerInvariant() : "immature",
        }).ToArray();

    /// <summary>Converts the mature curve to the existing power-law fit (for LTV estimates).</summary>
    public static RetentionFit? Fit(IReadOnlyList<CurvePoint> curve, long minUsers = 30) =>
        RetentionMath.Fit(curve.Select(p => new RetentionDay(p.Day, p.Percent, p.CohortUsers, p.Cohorts)), minUsers);

    /// <summary>Observed expected active days through day N (sum of mature retention fractions, day 0 = 1). Null when a day is missing.</summary>
    public static decimal? ObservedActiveDays(IReadOnlyList<CurvePoint> curve, int throughDay)
    {
        decimal sum = 0;
        for (var d = 0; d <= throughDay; d++)
        {
            var p = curve.FirstOrDefault(x => x.Day == d)?.Percent;
            if (p is null) return null;
            sum += p.Value / 100m;
        }
        return Math.Round(sum, 3);
    }
}
