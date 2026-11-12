using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Meta.Models;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// A synthetic project for <c>meeza-marketing-mcp dashboard --demo</c>. It is written to the local store the way a real
/// sync writes it (ads, countries, assets, creatives, GA4 users / events / cohorts, sync state), so every page of the
/// app has data to compute from and no platform is ever contacted. Every name, id and number is invented. The numbers
/// are pure functions of the day index, so the same <c>today</c> always gives the same rows.
/// </summary>
public static class DemoProject
{
    public const string Name = "Puzzle Quest (demo)";

    /// <summary>The project's reporting zone: pass <c>TimeZones.Today(DemoProject.TimeZone)</c> as today.</summary>
    public const string TimeZone = "UTC";

    /// <summary>Days of history, ending the day before today.</summary>
    public const int Days = 60;

    // Account ids that pass ProjectStore validation and belong to nobody.
    public const string GoogleAdsAccount = "9990000001", MetaAccount = "act_999000000000001", Ga4Property = "999000001";

    private const string Currency = "USD", Purchase = "in_app_purchase", TargetNote = "Demo target";
    private const int History = 120;      // simulated days before the stored window, so active users do not start from zero
    private const int Update = 27;        // day index of an onboarding update: cohorts from then on finish the tutorial and return more
    private const int FatigueDays = 14;   // the fatiguing creative loses response over the last two weeks
    private const double D1 = 0.35;       // day-1 retention of an average player before the per-source and per-country factors

    /// <summary>First-time player journey: event, label, share of new players reaching it before and after the onboarding update.</summary>
    private static readonly (string Event, string Label, double Before, double After)[] Nux =
    [
        ("tutorial_begin", "Tutorial started", .93, .96), ("tutorial_first_match", "First match made", .81, .89),
        ("tutorial_complete", "Tutorial completed", .63, .77), ("level_1_complete", "Level 1 cleared", .57, .71),
        ("level_5_complete", "Level 5 cleared", .36, .45),
    ];

    /// <summary>
    /// Creates the demo project and fills every fact table the app's pages read: <see cref="Days"/> days ending the day
    /// before <paramref name="today"/>, with each connection recorded as synchronized just now. A demo project left
    /// from an earlier run is deleted first, so there is only ever one and its data always ends yesterday.
    /// Returns the created project.
    /// </summary>
    public static Project Seed(ProjectStore projects, FactStore facts, SyncStore syncs, DateOnly today)
    {
        foreach (var old in projects.List(includeArchived: true).Where(IsDemo)) projects.Delete(old.Id);

        var p = projects.Create(new Project
        {
            Name = Name, Genre = "puzzle", Platforms = "android", TargetCountries = "US,PH,IN,BR,ID", Monetization = "hybrid",
            Currency = Currency, TimeZone = TimeZone, LaunchStage = "soft_launch",
            Objectives = "Reach D1 of 35% and D7 of 11% at a CPI of USD 0.85 or less, then scale country by country.",
            TutorialStartEvent = Nux[0].Event, TutorialEvent = "tutorial_complete", PurchaseEvent = Purchase,
            NuxEvents = Nux.Select(s => new NuxStep(s.Event, s.Label)).ToList(),
            // A long interval keeps the sources "fresh" for as long as a demo session lasts.
            BackfillDays = Days, SyncIntervalMinutes = 1440,
            Connections =
            [
                new(Integrations.GoogleAds, GoogleAdsAccount, "Puzzle Quest demo ads", Currency, TimeZone),
                new(Integrations.Meta, MetaAccount, "Puzzle Quest demo ads", Currency, TimeZone),
                new(Integrations.Ga4, Ga4Property, "Puzzle Quest demo app", Currency, TimeZone),
            ],
        });
        projects.UpsertTarget(p.Id, "cpi", "all", null, 0.85m, TargetNote);
        projects.UpsertTarget(p.Id, "d1", "all", null, 35m, TargetNote);
        projects.UpsertTarget(p.Id, "d7", "all", null, 11m, TargetNote);

        var (start, end) = (today.AddDays(-Days), today.AddDays(-1));
        var model = new Model(start);
        var google = AdScope(p.Id, Integrations.GoogleAds, GoogleAdsAccount);
        var meta = AdScope(p.Id, Integrations.Meta, MetaAccount);
        var ga4 = new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = Ga4Property };
        var records = new Dictionary<string, int>();

        // One transaction for the dated facts: the demo is either complete or not there.
        using (var c = facts.Db.Open())
        using (var tx = c.BeginTransaction())
        {
            void Write<T>(string integration, string table, IReadOnlyDictionary<string, object?> scope, IReadOnlyCollection<T> rows, string dateColumn = "date") =>
                records[integration] = records.GetValueOrDefault(integration) + facts.ReplaceWindow(c, tx, table, scope, dateColumn, start, end, rows).Written;

            foreach (var (platform, scope) in new[] { (Integrations.GoogleAds, google), (Integrations.Meta, meta) })
            {
                var days = model.AdDays.Where(d => d.Ad.Set.Campaign.Platform == platform).ToArray();
                Write(platform, "ad_daily", scope, days.Select(AdRow).ToArray());
                Write(platform, "campaign_country_daily", scope, CountryRows(days));
            }
            Write(Integrations.GoogleAds, "asset_daily", google, model.AdDays.SelectMany(AssetRows).ToArray());
            Write(Integrations.Ga4, "ga4_daily", ga4, model.Ga4Daily());
            Write(Integrations.Ga4, "ga4_dim_daily", ga4, model.Ga4Dims());
            Write(Integrations.Ga4, "ga4_events_daily", ga4, model.Ga4Events());
            Write(Integrations.Ga4, "ga4_cohorts", ga4, model.Ga4Cohorts(), "cohort_date");
            tx.Commit();
        }
        records[Integrations.GoogleAds] += facts.Upsert("creatives", google, GoogleCreatives());
        records[Integrations.Meta] += facts.Upsert("creatives", meta, MetaCreatives());
        records[Integrations.Meta] += facts.Upsert("reach_windows", meta, ReachWindows(model.AdDays, end));

        // What a finished first sync leaves behind: one successful run per connection, with data through yesterday.
        foreach (var (integration, account, ms) in new[] { (Integrations.GoogleAds, GoogleAdsAccount, 6400L), (Integrations.Meta, MetaAccount, 9100L), (Integrations.Ga4, Ga4Property, 12800L) })
            syncs.FinishRun(syncs.StartRun(p.Id, integration, account, "backfill", "demo", start, end), "success", records[integration], end, start, null, [], ms);
        return projects.Require(p.Id);
    }

    /// <summary>A project this class created: the demo name on nothing but the demo accounts.</summary>
    private static bool IsDemo(Project p) =>
        p.Name == Name && p.Connections.Count > 0 && p.Connections.All(c => c.ExternalId is GoogleAdsAccount or MetaAccount or Ga4Property);

    private static Dictionary<string, object?> AdScope(string projectId, string platform, string account) =>
        new() { ["project_id"] = projectId, ["platform"] = platform, ["account_id"] = account };

    // ---------------------------------------------------------------- the invented accounts

    /// <summary>One targeted country of a campaign: its share of spend, and its CPM, CTR and CPI relative to the campaign's.</summary>
    private sealed record Geo(string Country, double Share = 1, double Cpm = 1, double Ctr = 1, double Cpi = 1);

    /// <param name="Quality">Retention of the players it brings, relative to an average player.</param>
    private sealed record Campaign(string Platform, string Id, string Name, string Channel, double Quality, params Geo[] Geos);

    private sealed record AdSet(Campaign Campaign, string Id, string Name);

    /// <summary>One asset of a Google asset-based ad: the share of the ad's impressions it is shown in, and how it does there.</summary>
    private sealed record Asset(string Id, string Field, string Name, string Label, double Share, double Ctr = 1, double Cvr = 1, string? Size = null)
    {
        public string Format => Field switch { "YOUTUBE_VIDEO" => "video", "MARKETING_IMAGE" => "image", _ => "text" };
    }

    /// <summary>An ad at full budget: daily spend, CPM, CTR and click-to-install rate. From / To are day indexes (the stored window is 0..Days-1).</summary>
    private sealed record Ad(AdSet Set, string Id, string Name, double Spend, double Cpm, double Ctr, double Cvr, int From = int.MinValue, int To = int.MaxValue)
    {
        public string? Format { get; init; }   // Meta creative
        public double? Seconds { get; init; }
        public string? Title { get; init; }
        public string? Body { get; init; }
        public bool Fatigues { get; init; }
        public Asset[] Assets { get; init; } = [];   // Google asset-based ad

        public bool Google => Set.Campaign.Platform == Integrations.GoogleAds;
        public bool Video => Format == "video" || Assets.Any(a => a.Format == "video");
        public string Status => To == int.MaxValue ? Google ? "ENABLED" : "ACTIVE" : "PAUSED";
    }

    private static readonly Ad[] Ads = BuildAds();

    private static Ad[] BuildAds()
    {
        const string g = Integrations.GoogleAds, m = Integrations.Meta, app = "MULTI_CHANNEL/APP_CAMPAIGN", installs = "OUTCOME_APP_PROMOTION";
        const string video = "YOUTUBE_VIDEO", image = "MARKETING_IMAGE", headline = "HEADLINE", text = "DESCRIPTION", wide = "1200×628", square = "1200×1200";

        // Google Ads: an efficient Tier 2 campaign, an expensive US one, a broad one whose players leave, and a
        // display test that spends without a single install.
        var tier2 = new Campaign(g, "9901000001", "Puzzle Quest | Android | PH-IN-BR | Installs", app, 1.06,
            new Geo("PH", .34, 1.10, 1.00, 1.08), new Geo("IN", .38, .78, 1.05, .74), new Geo("BR", .28, 1.25, .95, 1.50));
        var us = new Campaign(g, "9901000002", "Puzzle Quest | Android | US | Installs", app, 1.06, new Geo("US"));
        var broad = new Campaign(g, "9901000003", "Puzzle Quest | Android | IN-PK | Broad reach", app, 0.56,
            new Geo("IN", .70, 1.05, 1.00, 1.04), new Geo("PK", .30, .90, 1.00, .92));
        var display = new Campaign(g, "9901000004", "Puzzle Quest | Display | Lapsed players test", "DISPLAY", 1,
            new Geo("US", .40, 2.60, .90), new Geo("PH", .30, .80, 1.05), new Geo("IN", .30, .55, 1.05));
        Ad AppAd(Campaign c, string n, string group, double spend, double cpm, double ctr, double cvr, int from, params Asset[] assets) =>
            new(new AdSet(c, "99020000" + n, group), "99030000" + n, (c.Channel == app ? "App Ad #" : "Responsive Display Ad #") + "99030000" + n, spend, cpm, ctr, cvr, from) { Assets = assets };

        // Meta: the same three markets. Its best video fatigues, one static never worked and was paused.
        var metaTier2 = new Campaign(m, "990500000000001", "Puzzle Quest - Installs - Tier 2", installs, 0.95,
            new Geo("PH", .40, 1.15, 1.00, 1.10), new Geo("IN", .35, .80, 1.05, .78), new Geo("PK", .25, .70, 1.00, .85));
        var metaUs = new Campaign(m, "990500000000002", "Puzzle Quest - Installs - US", installs, 1.04, new Geo("US"));
        var metaBr = new Campaign(m, "990500000000003", "Puzzle Quest - Installs - BR test", installs, 0.98, new Geo("BR"));
        var broadAudience = new AdSet(metaTier2, "990600000000011", "PH-IN-PK | Broad 18-44");
        var puzzleFans = new AdSet(metaTier2, "990600000000012", "PH-IN-PK | Puzzle fans");
        var usBroad = new AdSet(metaUs, "990600000000021", "US | Broad 25-54");
        var brLookalike = new AdSet(metaBr, "990600000000031", "BR | Lookalike 2%");
        Ad MetaAd(AdSet set, string n, string name, string format, double? seconds, string title, string body, double spend, double cpm, double ctr, double cvr,
            int from = int.MinValue, int to = int.MaxValue, bool fatigues = false) =>
            new(set, "9907000000000" + n, name, spend, cpm, ctr, cvr, from, to) { Format = format, Seconds = seconds, Title = title, Body = body, Fatigues = fatigues };

        return
        [
            AppAd(tier2, "11", "Gameplay videos", 48, 2.20, .026, .22, int.MinValue,
                new("9904000101", video, "Level 42: can you beat it? (15s)", "BEST", .46, 1.15, 1.10),
                new("9904000102", video, "Gameplay montage (30s)", "LOW", .22, .75, .80),
                new("9904000103", image, "Board close-up", "GOOD", .17, .95, .95, wide),
                new("9904000104", headline, "Can you clear the board?", "BEST", .58, 1.08, 1.04),
                new("9904000105", headline, "Slide. Match. Solve.", "GOOD", .42, .90, .95),
                new("9904000106", text, "500+ handmade levels. Play free, even offline.", "GOOD", 1)),
            AppAd(tier2, "12", "Satisfying clears", 36, 2.05, .023, .205, int.MinValue,
                new("9904000201", video, "Satisfying clears (20s)", "GOOD", .40, 1.05),
                new("9904000202", video, "One move from failing (12s)", "BEST", .30, 1.20, 1.05),
                new("9904000203", image, "Rewards chest", "LOW", .15, .70, .80, square),
                new("9904000204", headline, "So satisfying to clear", "GOOD", .55, 1.05),
                new("9904000205", headline, "Best puzzle game 2026", "LOW", .45, .90, .85),
                new("9904000206", text, "Relax with a new puzzle every day.", "GOOD", 1)),
            AppAd(us, "21", "US - Challenge hooks", 52, 15.50, .022, .34, int.MinValue,
                new("9904000301", video, "Only 1% clear level 42 (15s)", "BEST", .50, 1.20, 1.10),
                new("9904000302", video, "3 moves left (18s)", "GOOD", .25, .95),
                new("9904000303", image, "Level 42 board", "LEARNING", .10, .80, .90, wide),
                new("9904000304", headline, "Can you beat level 42?", "BEST", .60, 1.10, 1.05),
                new("9904000305", headline, "Only 3 moves left", "GOOD", .40, .90, .95),
                new("9904000306", text, "Think you can solve it? Play Puzzle Quest free.", "GOOD", 1)),
            AppAd(us, "22", "US - Relax and unwind", 40, 16.50, .019, .36, int.MinValue,
                new("9904000401", video, "Coffee break puzzle (15s)", "GOOD", .55),
                new("9904000402", image, "Cozy board on beige", "GOOD", .30, .90, 1, square),
                new("9904000403", headline, "Your 5 minute break", "GOOD", .50),
                new("9904000404", headline, "Relax. Match. Repeat.", "LEARNING", .50, .92, .97),
                new("9904000405", text, "A calm puzzle for busy days.", "GOOD", 1)),
            AppAd(broad, "31", "All placements", 30, 1.50, .027, .10, 18,
                new("9904000501", video, "Gameplay montage - short (10s)", "LOW", .55),
                new("9904000502", image, "Logo on purple", "LOW", .30, .80, .70, square),
                new("9904000503", headline, "Free puzzle game", "GOOD", .60),
                new("9904000504", headline, "Download Puzzle Quest", "LOW", .40, .85, .90),
                new("9904000505", text, "Free to play. No wifi needed.", "GOOD", 1)),
            AppAd(display, "41", "Lapsed 30 days", 13, 1.90, .0055, 0, Days - 21,
                new("9904000601", image, "Come back banner", "LEARNING", .70, 1, 1, wide),
                new("9904000602", image, "Come back square", "LEARNING", .30, .90, 1, square),
                new("9904000603", headline, "New levels are waiting", "LEARNING", 1),
                new("9904000604", text, "40 new boards were added while you were away.", "LEARNING", 1)),

            MetaAd(broadAudience, "11", "Video - Level 42 fail", "video", 22, "Can you beat level 42?",
                "Most players fail this board. Swap, match and clear it in 12 moves.", 24, 1.95, .0175, .21, fatigues: true),
            MetaAd(broadAudience, "12", "Video - Satisfying clears", "video", 15, "So satisfying to clear",
                "Slide, match and watch the board melt away. Free to play.", 14, 1.85, .0145, .22),
            MetaAd(broadAudience, "13", "Static - Rewards chest", "image", null, "Free rewards every day",
                "Download Puzzle Quest and open your first chest free.", 7, 1.60, .010, .17),
            MetaAd(puzzleFans, "14", "Video - 3 moves left", "video", 18, "Only 3 moves left",
                "What would you do? Solve the board before the moves run out.", 12, 2.05, .016, .23),
            MetaAd(puzzleFans, "15", "Playable - First board", "playable", null, "Try the first board",
                "Play it right here, then keep going in the game.", 6, 2.40, .023, .19, from: 24),
            MetaAd(usBroad, "21", "Video - Level 42 fail (US cut)", "video", 20, "Can you beat level 42?",
                "Only 1 in 100 players clear it on the first try.", 26, 11.50, .0125, .40),
            MetaAd(usBroad, "22", "Video - Coffee break puzzle", "video", 15, "Your 5 minute break",
                "A calm puzzle for busy days. No timers, no wifi needed.", 16, 10.80, .0105, .42),
            MetaAd(usBroad, "23", "Static - Download free today", "image", null, "Download free today",
                "The new puzzle game everyone is playing. Install now.", 9, 9.50, .006, .30, to: 34),
            MetaAd(brLookalike, "31", "Video - Level 42 fail (BR cut)", "video", 22, "Level 42: will you clear it?",
                "Swap, match and clear the board in 12 moves.", 11, 2.90, .015, .24, from: 30),
            MetaAd(brLookalike, "32", "Video - Satisfying clears (BR cut)", "video", 15, "So satisfying to clear",
                "Slide, match and watch the board melt away.", 8, 2.70, .013, .24, from: 30),
        ];
    }

    // ---------------------------------------------------------------- ad delivery

    private sealed record GeoDay(string Country, decimal Spend, long Impressions, long Clicks, long Installs);

    /// <summary>One ad on one day. K is the ad's position in <see cref="Ads"/> (its noise seed).</summary>
    private sealed record AdDay(Ad Ad, int K, int Index, DateOnly Date, decimal Spend, long Impressions, long Clicks, long Installs, long Reach, GeoDay[] Geos);

    private static AdDay? Deliver(int k, int i, DateOnly date)
    {
        var ad = Ads[k];
        if (i < ad.From || i > ad.To) return null;
        var week = Week(date);
        // Fatigue: CTR falls 9% a day, and the platform moves part of the budget away as response drops.
        var tired = ad.Fatigues && i >= Days - FatigueDays ? Math.Pow(0.91, i - (Days - FatigueDays) + 1) : 1;
        var budget = ad.Spend * Ramp(i) * week.Spend * Pace(i) * (0.55 + 0.45 * tired) * (1 + 0.07 * N(k, i, 1));
        // More budget buys more expensive impressions, so installs grow with spend^0.8 (diminishing returns).
        var cpm = ad.Cpm * Math.Pow(budget / ad.Spend, 0.2) * (1 + 0.001 * i) * (1 + 0.04 * N(k, i, 2));
        var spend = Math.Round((decimal)budget, 2);
        var impressions = (long)Math.Round(budget / cpm * 1000);
        var clicks = (long)Math.Round(impressions * ad.Ctr * tired * (1 + 0.05 * N(k, i, 3)));
        var installs = Whole(clicks * ad.Cvr * (1 + 0.05 * N(k, i, 4)), k, i, 5);
        var frequency = 1.2 + 0.1 * U(k, 6) + 1.3 * (1 - tired);   // a fatiguing ad is shown to the same people again

        var geos = ad.Set.Campaign.Geos;
        var cents = Split((long)(spend * 100), geos.Select(x => x.Share));
        var imps = Split(impressions, geos.Select(x => x.Share / x.Cpm));
        var clk = Split(clicks, geos.Select(x => x.Share / x.Cpm * x.Ctr));
        var inst = Split(installs, geos.Select(x => x.Share / x.Cpi));
        return new AdDay(ad, k, i, date, spend, impressions, clicks, installs, (long)(impressions / frequency),
            geos.Select((x, n) => new GeoDay(x.Country, cents[n] / 100m, imps[n], clk[n], inst[n])).ToArray());
    }

    private static AdDailyRow AdRow(AdDay d)
    {
        var (ad, set, c) = (d.Ad, d.Ad.Set, d.Ad.Set.Campaign);
        long? Share(double low, double spread, int stream) => ad.Video ? (long)(d.Impressions * (low + spread * U(d.K, stream))) : null;
        // Google reports zero installs as 0; Meta leaves the install action out, which a real sync stores as null.
        return ad.Google
            ? new(d.Date, c.Id, c.Name, "ENABLED", c.Channel, set.Id, set.Name, "ENABLED", ad.Id, ad.Name, ad.Status, Currency,
                d.Spend, d.Impressions, null, d.Clicks, d.Installs, d.Installs, GoogleAdsMapping.InstallSource, 0m,
                Share(.15, .06, 11), Share(.30, .08, 12), Share(.07, .04, 13))
            : new(d.Date, c.Id, c.Name, "ACTIVE", c.Channel, set.Id, set.Name, "ACTIVE", ad.Id, ad.Name, ad.Status, Currency,
                d.Spend, d.Impressions, d.Reach, d.Clicks, null, d.Installs == 0 ? null : d.Installs, d.Installs == 0 ? null : $"meta_attributed:{MetaMapping.InstallActionTypes[0]}",
                d.Installs == 0 ? null : Math.Round(d.Installs * (c.Geos[0].Country == "US" ? 0.34m : 0.04m), 2),
                Share(.52, .16, 11), Share(.26, .08, 12), Share(.04, .03, 13), Share(.07, .04, 14));
    }

    private static CountryDailyRow[] CountryRows(IEnumerable<AdDay> days) =>
        days.SelectMany(d => d.Geos.Select(x => (Day: d, Geo: x)))
            .GroupBy(x => (x.Day.Date, x.Day.Ad.Set.Campaign, x.Geo.Country))
            .Select(x =>
            {
                var (date, c, country) = x.Key;
                var installs = x.Sum(v => v.Geo.Installs);
                var google = c.Platform == Integrations.GoogleAds;
                return new CountryDailyRow(date, c.Id, c.Name, country, Currency, x.Sum(v => v.Geo.Spend), x.Sum(v => v.Geo.Impressions), x.Sum(v => v.Geo.Clicks),
                    google ? installs : null, google || installs > 0 ? installs : null);
            }).ToArray();

    /// <summary>Per-asset results of a Google ad. Assets are shown together, so they overlap and never add up to the ad.</summary>
    private static IEnumerable<AssetDailyRow> AssetRows(AdDay d) => d.Ad.Assets.Select((a, n) =>
    {
        var seed = d.K * 100 + n;
        var share = a.Share * (1 + 0.06 * N(seed, d.Index, 21));
        return new AssetDailyRow(d.Date, d.Ad.Set.Campaign.Id, d.Ad.Set.Id, d.Ad.Id, a.Id, a.Field, Currency, Math.Round(d.Spend * (decimal)share, 2),
            (long)(d.Impressions * share), Whole(d.Clicks * share * a.Ctr, seed, d.Index, 22), Whole(d.Installs * share * a.Ctr * a.Cvr, seed, d.Index, 23));
    });

    // Thumbnail and preview URLs stay empty so the browser never loads anything from outside.
    private static CreativeRow[] GoogleCreatives() => Ads.SelectMany(ad => ad.Assets.Select(a =>
        new CreativeRow($"asset:{a.Id}:{a.Field}", ad.Id, a.Id, a.Name, a.Format, a.Field, null, null, null, null, a.Size, a.Format == "text" ? a.Name : null, a.Label))).ToArray();

    private static CreativeRow[] MetaCreatives() => Ads.Where(ad => !ad.Google).Select(ad =>
    {
        var id = "9908" + ad.Id[4..];
        return new CreativeRow($"creative:{id}", ad.Id, id, ad.Name, ad.Format, null, null, null, null, (decimal?)ad.Seconds, ad.Title, ad.Body, null);
    }).ToArray();

    /// <summary>Meta reach for the trailing windows a sync stores. People seen on several days are counted once, so reach grows slower than the days.</summary>
    private static ReachWindowRow[] ReachWindows(IReadOnlyList<AdDay> days, DateOnly end) =>
        new[] { (Days: 7, Unique: .62), (Days: 14, Unique: .50), (Days: 30, Unique: .38) }.Select(w =>
        {
            var from = end.AddDays(1 - w.Days);
            var rows = days.Where(d => !d.Ad.Google && d.Date >= from).ToArray();
            var (impressions, reach) = (rows.Sum(d => d.Impressions), (long)(rows.Sum(d => d.Reach) * w.Unique));
            return new ReachWindowRow(from, end, reach, impressions, reach == 0 ? null : Math.Round((decimal)impressions / reach, 2));
        }).ToArray();

    // ---------------------------------------------------------------- players (GA4)

    /// <summary>One group of new players that GA4 reports alike: where they are and how it attributes them.</summary>
    /// <param name="Decay">Exponent of the retention curve R(day) = D1 × day^Decay.</param>
    private sealed record Cell(string Country, string Channel, string SourceMedium, string Campaign, string AdGroup, double Quality, double Decay, Func<int, double> NewUsers);

    /// <summary>Value of a GA4 dimension or cohort segment for a group of players. Null = a key this demo does not know.</summary>
    private static string? Segment(Cell c, string key) => key switch
    {
        "all" => "",
        "country" => c.Country,
        "platform" => "Android",
        "channel" => c.Channel,
        "source_medium" => c.SourceMedium,
        "campaign" => c.Campaign,
        "ad_group" => c.AdGroup,
        _ => null,
    };

    private static double CountryRetention(string country) => country switch
    {
        "US" => 1.08, "GB" => 1.05, "PH" => 1.04, "ID" => 1.00, "BR" => .98, "IN" => .97, "MX" => .97, "PK" => .90, _ => .90,
    };

    /// <summary>Revenue per daily active user (ads + purchases).</summary>
    private static double Arpdau(string country) => country switch
    {
        "US" => .30, "GB" => .24, "BR" => .07, "MX" => .06, "PH" => .05, "ID" => .04, "IN" => .03, "PK" => .02, _ => .04,
    };

    /// <summary>
    /// The players behind the ads: every day's new players by source and country, and on which later days each of
    /// those cohorts comes back. Daily users, dimension splits, events and cohorts are all sums of the same groups,
    /// so the GA4 tables agree with each other and with the installs the ad platforms report.
    /// </summary>
    private sealed class Model
    {
        private const string NotSet = "(not set)";
        private readonly DateOnly _start;
        private readonly AdDay?[,] _ads = new AdDay?[Ads.Length, History + Days];
        private readonly List<Cell> _cells = [];
        private readonly long[][] _new;        // [cell][History + day]
        private readonly long[][] _active;     // [cell][day]: new and returning players active that day
        private readonly long[][][] _cohort;   // [cell][cohort day][day n]: players of the cohort active n days later

        public IReadOnlyList<AdDay> AdDays { get; }

        public Model(DateOnly start)
        {
            _start = start;
            for (var k = 0; k < Ads.Length; k++)
                for (var i = -History; i < Days; i++)
                    _ads[k, i + History] = Deliver(k, i, start.AddDays(i));
            AdDays = Enumerable.Range(0, Ads.Length).SelectMany(k => Enumerable.Range(History, Days).Select(i => _ads[k, i])).OfType<AdDay>().ToArray();

            AddCells();
            var n = _cells.Count;
            (_new, _active, _cohort) = (new long[n][], new long[n][], new long[n][][]);
            for (var x = 0; x < n; x++)
            {
                _new[x] = new long[History + Days];
                _active[x] = new long[Days];
                _cohort[x] = Enumerable.Range(0, Days).Select(_ => new long[Ga4Syncer.MaxCohortDay + 1]).ToArray();
                for (var c = -History; c < Days; c++)
                {
                    var users = _new[x][c + History] = Whole(_cells[x].NewUsers(c), 1000 + x, c, 31);
                    if (users == 0) continue;
                    if (c >= 0) _active[x][c] += _cohort[x][c][0] = users;
                    for (var age = Math.Max(1, -c); c + age < Days; age++)
                    {
                        var day = c + age;
                        var back = Whole(users * Retention(_cells[x], c, age) * Week(start.AddDays(day)).Activity * (1 + 0.02 * N(day, 32)), 2000 + x, c, age);
                        _active[x][day] += back;
                        if (c >= 0 && age <= Ga4Syncer.MaxCohortDay) _cohort[x][c][age] = back;
                    }
                }
            }
        }

        private long Installs(int k, int i, int geo) => _ads[k, i + History]?.Geos[geo].Installs ?? 0;

        private void AddCells()
        {
            // Google App campaigns are auto-tagged: GA4 knows the campaign and ad group of about nine in ten installs.
            for (var k = 0; k < Ads.Length; k++)
            {
                var (ad, c) = (Ads[k], Ads[k].Set.Campaign);
                if (!ad.Google || ad.Cvr == 0) continue;
                for (var geo = 0; geo < c.Geos.Length; geo++)
                {
                    var (ki, gi) = (k, geo);
                    _cells.Add(new(c.Geos[geo].Country, "Cross-network", "google / cpc", c.Name, ad.Set.Name, c.Quality, -.59, i => .9 * Installs(ki, i, gi)));
                }
            }
            // Meta installs reach GA4 without their campaign: a third carry Meta's referrer, most arrive as direct.
            foreach (var c in Ads.Where(a => !a.Google).Select(a => a.Set.Campaign).Distinct())
            {
                var ads = Enumerable.Range(0, Ads.Length).Where(k => Ads[k].Set.Campaign == c).ToArray();
                for (var geo = 0; geo < c.Geos.Length; geo++)
                {
                    var gi = geo;
                    double Meta(int i) => ads.Sum(k => Installs(k, i, gi));
                    _cells.Add(new(c.Geos[geo].Country, "Paid Social", "facebook / cpc", NotSet, NotSet, c.Quality, -.59, i => .24 * Meta(i)));
                    _cells.Add(new(c.Geos[geo].Country, "Paid Social", "instagram / cpc", NotSet, NotSet, c.Quality, -.59, i => .11 * Meta(i)));
                    _cells.Add(new(c.Geos[geo].Country, "Direct", "(direct) / (none)", "(direct)", NotSet, c.Quality, -.59, i => .55 * Meta(i)));
                }
            }
            // Players who found the game themselves: more of them every time the ads bring more installs, more at weekends.
            double Paid(int i) => Enumerable.Range(0, Ads.Length).Sum(k => _ads[k, i + History]?.Installs ?? 0);
            double Own(int i, int stream) => Week(_start.AddDays(i)).Organic * (1 + 0.08 * N(i, stream));
            foreach (var (country, share) in new[] { ("IN", .22), ("US", .16), ("PH", .15), ("BR", .12), ("ID", .12), ("PK", .07), ("MX", .07), ("GB", .06), (NotSet, .03) })
            {
                _cells.Add(new(country, "Organic Search", "google-play / organic", "(organic)", NotSet, 1.22, -.54, i => share * (46 * Ramp(i) + .05 * Paid(i)) * Own(i, 33)));
                _cells.Add(new(country, "Direct", "(direct) / (none)", "(direct)", NotSet, 1, -.57, i => share * 15 * Ramp(i) * Own(i, 34)));
            }
            foreach (var (country, share) in new[] { (NotSet, .5), ("IN", .3), ("US", .2) })
                _cells.Add(new(country, "Unassigned", NotSet, NotSet, NotSet, .8, -.59, i => share * 5 * Ramp(i) * Own(i, 35)));
        }

        /// <summary>Share of a cohort active <paramref name="age"/> days after its first day. Cohorts from the onboarding update on return more.</summary>
        private static double Retention(Cell cell, int cohort, double age) =>
            Math.Min(.9, D1 * Math.Pow(age, cell.Decay) * cell.Quality * CountryRetention(cell.Country) * (cohort < Update ? .93 : 1.04));

        private DateOnly Date(int day) => _start.AddDays(day);
        private long NewUsers(int day) => _new.Sum(x => x[day + History]);
        private long ActiveUsers(int day) => _active.Sum(x => x[day]);

        /// <summary>
        /// Players active at least once in the window ending on a day: everyone who started inside it, plus the share of
        /// older cohorts that still plays (their daily retention times how many more of them show up over several days).
        /// </summary>
        private long ActiveIn(int day, int window, double spread)
        {
            var total = 0.0;
            for (var x = 0; x < _cells.Count; x++)
                for (var c = -History; c <= day; c++)
                    total += _new[x][c + History] * (day - c < window ? 1 : Math.Min(1, spread * Retention(_cells[x], c, day - c - window / 2.0)));
            return (long)total;
        }

        /// <summary>Revenue weight of a day: active players times what a player of their country is worth.</summary>
        private double Value(int day, IEnumerable<int> cells) => cells.Sum(x => _active[x][day] * Arpdau(_cells[x].Country));

        private (decimal Ads, decimal Purchases, long Purchasers) Revenue(int day)
        {
            var value = Value(day, Enumerable.Range(0, _cells.Count));
            var ads = Math.Round((decimal)(value * .55 * Week(Date(day)).Activity * (1 + 0.06 * N(day, 41))), 2);
            var purchases = Math.Round((decimal)(value * .45 * (1 + 0.15 * N(day, 42))), 2);
            return (ads, purchases, Math.Max(1, (long)((double)purchases / (4.6 * (1 + 0.1 * N(day, 43))))));
        }

        public Ga4DailyRow[] Ga4Daily() => Enumerable.Range(0, Days).Select(day =>
        {
            var (active, revenue) = (ActiveUsers(day), Revenue(day));
            var weekly = Math.Max(active, ActiveIn(day, 7, 2.3));
            return new Ga4DailyRow(Date(day), active, NewUsers(day), weekly, Math.Max(weekly, ActiveIn(day, 28, 3.8)), Sessions(day, active),
                Math.Round((decimal)(active * 610 * (1 + 0.04 * N(day, 45))), 0), revenue.Ads + revenue.Purchases, revenue.Ads, revenue.Purchases, revenue.Purchasers, Currency);
        }).ToArray();

        private long Sessions(int day, long active) => (long)(active * (2.2 + 0.2 * (Week(Date(day)).Activity - 1) * 10) * (1 + 0.03 * N(day, 44)));

        public Ga4DimRow[] Ga4Dims()
        {
            var rows = new List<Ga4DimRow>();
            foreach (var key in Ga4Syncer.Dimensions.Keys)
                foreach (var g in Enumerable.Range(0, _cells.Count).GroupBy(x => Segment(_cells[x], key)).Where(g => g.Key is not null))
                    for (var day = 0; day < Days; day++)
                    {
                        var (active, users) = (g.Sum(x => _active[x][day]), g.Sum(x => _new[x][day + History]));
                        if (active == 0) continue;
                        var revenue = Revenue(day);
                        var share = Value(day, g) / Value(day, Enumerable.Range(0, _cells.Count));
                        rows.Add(new(Date(day), key, g.Key!, active, users, Math.Round((revenue.Ads + revenue.Purchases) * (decimal)share, 2)));
                    }
            return rows.ToArray();
        }

        public Ga4EventRow[] Ga4Events() => Enumerable.Range(0, Days).SelectMany(day =>
        {
            var (date, active, users) = (Date(day), ActiveUsers(day), NewUsers(day));
            var sessions = Sessions(day, active);
            var purchasers = Revenue(day).Purchasers;
            long Of(double perPlayer, int stream) => (long)(active * perPlayer * (1 + 0.04 * N(day, stream)));
            var rows = new List<Ga4EventRow>
            {
                new(date, "first_open", users, users),
                new(date, "session_start", sessions, active),
                new(date, "user_engagement", (long)(sessions * 2.8), Of(.97, 51)),
                new(date, "screen_view", (long)(sessions * 6.4), Of(.99, 52)),
                new(date, "level_start", Of(7.1, 53), Of(.90, 54)),
                new(date, "level_complete", Of(5.2, 55), Of(.86, 56)),
                new(date, "level_fail", Of(1.9, 57), Of(.58, 58)),
                new(date, "ad_impression", Of(3.6, 59), Of(.71, 60)),
                new(date, "ad_reward", Of(.9, 61), Of(.34, 62)),
                new(date, Purchase, (long)Math.Ceiling(purchasers * 1.22), purchasers),
                new(date, "app_remove", Of(.028, 63), Of(.028, 63)),
            };
            // Each step of the journey loses some of the players who reached the step before it.
            var reached = users;
            for (var s = 0; s < Nux.Length; s++)
            {
                reached = Math.Min(reached, Whole(users * (day < Update ? Nux[s].Before : Nux[s].After) * (1 + 0.02 * N(day, 70 + s)), day, 80 + s));
                rows.Add(new(date, Nux[s].Event, reached, reached));
            }
            return rows;
        }).ToArray();

        /// <summary>Only the days a cohort has reached, and no row when nobody came back: what the GA4 cohort report returns.</summary>
        public Ga4CohortRow[] Ga4Cohorts()
        {
            var rows = new List<Ga4CohortRow>();
            foreach (var key in Ga4Syncer.CohortSegments.Keys)
                foreach (var g in Enumerable.Range(0, _cells.Count).GroupBy(x => Segment(_cells[x], key)).Where(g => g.Key is not null))
                    for (var c = 0; c < Days; c++)
                    {
                        var size = g.Sum(x => _new[x][c + History]);
                        for (var n = 0; size > 0 && n <= Ga4Syncer.MaxCohortDay && c + n < Days; n++)
                            if (g.Sum(x => _cohort[x][c][n]) is > 0 and var active) rows.Add(new(key, g.Key!, Date(c), n, active, size));
                    }
            return rows.ToArray();
        }
    }

    // ---------------------------------------------------------------- shapes over time & noise

    /// <summary>Budgets grew slowly from 80% to today's level.</summary>
    private static double Ramp(int i) => .8 + .2 * Math.Clamp((i + 90) / 150.0, 0, 1);

    /// <summary>Day-to-day pacing of the whole account: some days the platforms deliver more of the budget than others.</summary>
    private static double Pace(int i) => 1 + 0.09 * N(i, 90);

    /// <summary>Weekly seasonality of a casual game: more delivery, more organic installs and more play at weekends.</summary>
    private static (double Spend, double Organic, double Activity) Week(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Saturday => (1.12, 1.22, 1.06),
        DayOfWeek.Sunday => (1.10, 1.25, 1.07),
        DayOfWeek.Friday => (1.04, 1.05, 1.01),
        DayOfWeek.Monday => (.95, .94, .98),
        DayOfWeek.Tuesday => (.93, .92, .97),
        DayOfWeek.Wednesday => (.94, .93, .97),
        _ => (.97, .96, .98),
    };

    /// <summary>A number in [0, 1) that depends only on its arguments (a hash), never on call order, the process or the clock.</summary>
    private static double U(int a, int b, int c = 0, int d = 0)
    {
        unchecked
        {
            var h = 0x9E3779B9u;
            ReadOnlySpan<int> parts = [a, b, c, d];
            foreach (var v in parts)
            {
                h ^= (uint)v + 0x9E3779B9u + (h << 6) + (h >> 2);
                h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            }
            return h / 4294967296.0;
        }
    }

    /// <summary>Noise in [-1, 1].</summary>
    private static double N(int a, int b, int c = 0) => 2 * U(a, b, c, 7) - 1;

    /// <summary>Rounds an expected count to a whole number without bias: 2.3 becomes 2 seven times in ten and 3 otherwise.</summary>
    private static long Whole(double expected, int a, int b, int c = 0) => (long)Math.Floor(Math.Max(0, expected) + U(a, b, c, 9));

    /// <summary>Splits a whole number across weights so the parts add up exactly (largest remainder).</summary>
    private static long[] Split(long total, IEnumerable<double> weights)
    {
        var w = weights.ToArray();
        var sum = w.Sum();
        var exact = w.Select(x => total * x / sum).ToArray();
        var parts = exact.Select(x => (long)Math.Floor(x)).ToArray();
        foreach (var n in Enumerable.Range(0, parts.Length).OrderByDescending(n => exact[n] - parts[n]).ThenBy(n => n).Take((int)(total - parts.Sum()))) parts[n]++;
        return parts;
    }
}
