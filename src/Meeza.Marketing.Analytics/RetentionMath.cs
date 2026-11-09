namespace Meeza.Marketing.Analytics;

/// <summary>Users of one install-date cohort active on day N after their first session.</summary>
public sealed record CohortPoint(DateOnly Cohort, int Day, long ActiveUsers, long CohortUsers);

/// <summary>
/// Blended retention for one day offset across every cohort old enough to have reached it.
/// RetentionPercent is null when no cohort has matured to that day yet.
/// </summary>
public sealed record RetentionDay(int Day, decimal? RetentionPercent, long CohortUsers, int Cohorts);

/// <summary>Retention curve fitted as R(d) = A × d^B (d ≥ 1), the usual shape of mobile-game retention.</summary>
public sealed record RetentionFit(double A, double B, int Points)
{
    /// <summary>Predicted retention fraction (0..1) on day d.</summary>
    public double At(int day) => day <= 0 ? 1 : Math.Clamp(A * Math.Pow(day, B), 0, 1);
}

/// <summary>Pure retention and lifetime math. No I/O, so it is fully unit-testable.</summary>
public static class RetentionMath
{
    /// <summary>
    /// Blends cohorts into one curve weighted by cohort size. A cohort only counts toward day N once
    /// cohort date + N is on or before <paramref name="lastCompleteDay"/>, so young cohorts never drag days down.
    /// </summary>
    public static IReadOnlyList<RetentionDay> Blend(IEnumerable<CohortPoint> points, DateOnly lastCompleteDay, int maxDay)
    {
        var byCohort = points.GroupBy(p => p.Cohort)
            .Select(g => (Cohort: g.Key, Size: g.Max(p => p.CohortUsers), Days: g.ToDictionary(p => p.Day, p => p.ActiveUsers)))
            .Where(c => c.Size > 0)
            .ToArray();

        var result = new List<RetentionDay>();
        for (var d = 0; d <= maxDay; d++)
        {
            var matured = byCohort.Where(c => c.Cohort.AddDays(d) <= lastCompleteDay).ToArray();
            var users = matured.Sum(c => c.Size);
            if (users == 0) { result.Add(new RetentionDay(d, null, 0, 0)); continue; }
            var active = matured.Sum(c => c.Days.TryGetValue(d, out var a) ? a : 0);
            result.Add(new RetentionDay(d, Math.Round(active * 100m / users, 2), users, matured.Length));
        }
        return result;
    }

    /// <summary>Least-squares fit of ln R = ln A + B ln d over days ≥ 1 with retention above zero. Null with fewer than 2 points.</summary>
    public static RetentionFit? Fit(IEnumerable<RetentionDay> days, long minCohortUsers = 50)
    {
        var pts = days.Where(d => d.Day >= 1 && d.RetentionPercent > 0 && d.CohortUsers >= minCohortUsers)
            .Select(d => (x: Math.Log(d.Day), y: Math.Log((double)d.RetentionPercent!.Value / 100.0)))
            .ToArray();
        if (pts.Length < 2) return null;
        var mx = pts.Average(p => p.x); var my = pts.Average(p => p.y);
        var sxx = pts.Sum(p => (p.x - mx) * (p.x - mx));
        if (sxx <= 0) return null;
        var b = pts.Sum(p => (p.x - mx) * (p.y - my)) / sxx;
        b = Math.Min(b, 0); // retention never rises over time
        var a = Math.Exp(my - b * mx);
        return new RetentionFit(a, b, pts.Length);
    }

    /// <summary>Expected active days per installer over the first <paramref name="horizon"/> days (day 0 counts as 1).</summary>
    public static double ExpectedActiveDays(RetentionFit fit, int horizon)
    {
        var sum = 1.0;
        for (var d = 1; d <= horizon; d++) sum += fit.At(d);
        return sum;
    }

    /// <summary>Retention percent on a given day, or null when that day has not matured.</summary>
    public static decimal? On(IReadOnlyList<RetentionDay> curve, int day) =>
        curve.FirstOrDefault(d => d.Day == day)?.RetentionPercent;

    public static long UsersOn(IReadOnlyList<RetentionDay> curve, int day) =>
        curve.FirstOrDefault(d => d.Day == day)?.CohortUsers ?? 0;
}
