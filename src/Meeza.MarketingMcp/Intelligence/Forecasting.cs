namespace Meeza.MarketingMcp.Intelligence;

/// <summary>
/// Spend → installs response model and CPI forecast from recent daily history.
/// installs = e^a × spend^b (log-log least squares). b &lt; 1 captures diminishing returns: each extra unit of budget buys
/// fewer installs. With too little data the model falls back to a constant CPI and says so.
/// </summary>
public sealed class Forecasting
{
    public const int MinPointsForCurve = 7;

    public double A { get; private init; }
    public double B { get; private init; }
    public double? R2 { get; private init; }
    public int Points { get; private init; }
    public string Type { get; private init; } = "none";
    public IReadOnlyList<(DateOnly Date, decimal Cpi)> CpiHistory { get; private init; } = [];

    public static Forecasting Build(IReadOnlyList<(DateOnly Date, decimal Spend, decimal? Installs)> daily)
    {
        var pts = daily.Where(d => d.Spend > 0 && d.Installs > 0).ToArray();
        var cpis = pts.Select(d => (d.Date, Cpi: d.Spend / d.Installs!.Value)).OrderBy(x => x.Date).ToArray();
        if (pts.Length >= MinPointsForCurve)
        {
            var xs = pts.Select(p => Math.Log((double)p.Spend)).ToArray();
            var ys = pts.Select(p => Math.Log((double)p.Installs!.Value)).ToArray();
            var mx = xs.Average(); var my = ys.Average();
            var sxx = xs.Sum(x => (x - mx) * (x - mx));
            if (sxx > 1e-6)
            {
                var b = xs.Zip(ys).Sum(t => (t.First - mx) * (t.Second - my)) / sxx;
                var clamped = Math.Clamp(b, 0.3, 1.0);
                var a = my - clamped * mx;
                var ssTot = ys.Sum(y => (y - my) * (y - my));
                var ssRes = xs.Zip(ys).Sum(t => Math.Pow(t.Second - (a + clamped * t.First), 2));
                return new Forecasting { A = a, B = clamped, R2 = ssTot > 0 ? 1 - ssRes / ssTot : null, Points = pts.Length, Type = "power_curve", CpiHistory = cpis };
            }
        }
        if (pts.Length >= 1)
        {
            // Constant CPI: installs = spend / CPI  →  a = -ln(CPI), b = 1.
            var cpi = (double)(pts.Sum(p => p.Spend) / pts.Sum(p => p.Installs!.Value));
            return new Forecasting { A = -Math.Log(cpi), B = 1, Points = pts.Length, Type = "constant_cpi", CpiHistory = cpis };
        }
        return new Forecasting();
    }

    public decimal? InstallsAt(decimal dailySpend) =>
        Type == "none" || dailySpend <= 0 ? null : (decimal)(Math.Exp(A) * Math.Pow((double)dailySpend, B));

    /// <summary>
    /// CPI for the next <paramref name="horizon"/> days: exponentially weighted level plus a damped linear trend, with an
    /// 80% band from the spread of recent daily CPIs.
    /// </summary>
    public IReadOnlyList<object> CpiForecast(DateOnly today, int horizon)
    {
        if (CpiHistory.Count < 3) return [];
        var values = CpiHistory.Select(x => (double)x.Cpi).ToArray();
        double level = values[0];
        foreach (var v in values.Skip(1)) level = 0.3 * v + 0.7 * level;
        var recent = values.TakeLast(14).ToArray();
        var slope = Stats.Slope(recent) ?? 0;
        var resid = recent.Select((v, i) => v - recent.Average()).ToArray();
        var sd = Math.Sqrt(resid.Sum(r => r * r) / Math.Max(1, resid.Length - 1));
        return Enumerable.Range(0, horizon).Select(h =>
        {
            var damp = slope * (1 - Math.Pow(0.9, h + 1)) / 0.1; // damped trend: total drift converges
            var mid = Math.Max(0.0001, level + damp);
            var band = 1.28 * sd * Math.Sqrt(1 + h / 7.0);
            return (object)new
            {
                date = today.AddDays(h), cpi = Math.Round((decimal)mid, 4),
                low = Math.Round((decimal)Math.Max(0, mid - band), 4), high = Math.Round((decimal)(mid + band), 4),
            };
        }).ToArray();
    }

    public object Describe() => new
    {
        type = Type, points = Points, elasticity = Type == "none" ? (double?)null : Math.Round(B, 3), r2 = R2 is null ? (double?)null : Math.Round(R2.Value, 3),
        explanation = Type switch
        {
            "power_curve" => $"installs ≈ {Math.Exp(A):0.###} × daily spend^{B:0.##} from {Points} days. Elasticity {B:0.##}: +10% budget → about +{(Math.Pow(1.1, B) - 1) * 100:0.#}% installs.",
            "constant_cpi" => $"Only {Points} day(s) with installs: assumes CPI stays flat as budget changes. Real CPI usually rises with budget, so treat larger scenarios as optimistic.",
            _ => "No days with both spend and installs: cannot forecast.",
        },
    };
}
