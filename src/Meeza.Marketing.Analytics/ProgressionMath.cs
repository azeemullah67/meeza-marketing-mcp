using System.Globalization;
using System.Text.RegularExpressions;

namespace Meeza.Marketing.Analytics;

/// <summary>One GA4 row of a level event: (mode, level_name, eventName, success) with distinct users and event count.</summary>
public sealed record LevelEventRow(string Mode, string Level, string EventName, string? Success, long Users, long Events);

/// <summary>Per-level funnel numbers. Players are distinct users; attempts/wins/losses are events.</summary>
public sealed record LevelStat(
    string Level, long PlayersStarted, long Attempts, long PlayersFirstMove, long PlayersWon, long PlayersLost, long PlayersQuit,
    long Wins, long Losses,
    decimal? AttemptsPerPlayer, decimal? WinRatePercent, decimal? ReachPercent, decimal? DropToNextPercent);

/// <summary>One game mode (campaign, clear-board, daily, lesson): how many levels were played and how many attempts.</summary>
public sealed record ModeSummary(string Mode, int LevelsPlayed, long Attempts, long MostPlayersOnOneLevel);

public sealed record LevelProgressionReport(
    string PropertyId, string Range, string Mode, long NewUsers, long TutorialCompleted,
    IReadOnlyList<LevelStat> Levels, IReadOnlyList<ModeSummary> Modes,
    long UnattributedLevelEvents, long UnattributedLevelPlayers, IReadOnlyList<string> SuccessValuesSeen,
    bool Thresholded, bool DataLoss)
{
    /// <summary>Where win/loss came from: "dimension" (per player), "metric" (per event) or null (not tracked).</summary>
    public string? SuccessSource { get; init; }
}

/// <summary>Pure aggregation of GA4 level events into a per-level funnel. No I/O, so it is unit-testable.</summary>
public static partial class ProgressionMath
{
    public static readonly string[] LevelEvents = ["level_start", "level_first_move", "level_end", "level_quit"];
    public static readonly string[] AllEvents = [.. LevelEvents, "first_open", "tutorial_complete"];

    public static LevelProgressionReport Build(string propertyId, string range, IReadOnlyList<LevelEventRow> rows, string mode, bool thresholded, bool dataLoss)
    {
        var newUsers = rows.Where(r => r.EventName == "first_open").Sum(r => r.Users);
        var tutorialDone = rows.Where(r => r.EventName == "tutorial_complete").Sum(r => r.Users);
        var levelRows = rows.Where(r => LevelEvents.Contains(r.EventName) && r.Level != "(not set)").ToList();
        var unattributed = rows.Where(r => LevelEvents.Contains(r.EventName) && r.Level == "(not set)").ToList();
        var unattributedEvents = unattributed.Sum(r => r.Events);
        var unattributedPlayers = unattributed.Where(r => r.EventName == "level_start").Sum(r => r.Users);
        var successValues = rows.Where(r => r.EventName == "level_end" && r.Success is not null)
            .Select(r => r.Success!).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        var modes = levelRows
            .GroupBy(r => r.Mode, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ModeSummary(
                g.Key,
                g.Select(r => r.Level).Distinct().Count(),
                g.Where(r => r.EventName == "level_start").Sum(r => r.Events),
                g.Where(r => r.EventName == "level_start").Select(r => r.Users).DefaultIfEmpty(0).Max()))
            .OrderByDescending(m => m.Attempts)
            .ToList();

        var chosen = levelRows.Where(r => r.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase)).ToList();
        var byLevel = chosen.GroupBy(r => r.Level).OrderBy(g => g.Key, LevelComparer.Instance).ToList();

        var stats = new List<LevelStat>(byLevel.Count);
        long firstLevelPlayers = 0;
        foreach (var g in byLevel)
        {
            long Users(string ev, string? success = null) => g.Where(r => r.EventName == ev && (success is null || r.Success == success)).Sum(r => r.Users);
            long Events(string ev, string? success = null) => g.Where(r => r.EventName == ev && (success is null || r.Success == success)).Sum(r => r.Events);

            var started = Users("level_start");
            var attempts = Events("level_start");
            if (stats.Count == 0) firstLevelPlayers = started;
            var wonEvents = Events("level_end", "1");
            var lostEvents = Events("level_end", "0");
            var decidedEvents = wonEvents + lostEvents;   // level_end rows without a success value cannot be judged
            stats.Add(new LevelStat(
                g.Key, started, attempts, Users("level_first_move"), Users("level_end", "1"), Users("level_end", "0"), Users("level_quit"),
                wonEvents, lostEvents,
                started > 0 ? Math.Round((decimal)attempts / started, 2) : null,
                decidedEvents > 0 ? Math.Round(wonEvents * 100m / decidedEvents, 1) : null,
                firstLevelPlayers > 0 ? Math.Round(started * 100m / firstLevelPlayers, 1) : null,
                null));
        }

        // Drop to next = share of players who started level n but never started level n+1.
        for (var i = 0; i < stats.Count - 1; i++)
        {
            var cur = stats[i];
            var next = stats[i + 1];
            stats[i] = cur with
            {
                DropToNextPercent = cur.PlayersStarted > 0
                    ? Math.Round(Math.Max(0, cur.PlayersStarted - next.PlayersStarted) * 100m / cur.PlayersStarted, 1)
                    : null,
            };
        }

        return new LevelProgressionReport(propertyId, range, mode, newUsers, tutorialDone, stats, modes,
            unattributedEvents, unattributedPlayers, successValues, thresholded, dataLoss);
    }

    /// <summary>Largest drop between consecutive levels among levels with at least <paramref name="minPlayers"/> players.</summary>
    public static LevelStat? BiggestDrop(IReadOnlyList<LevelStat> levels, long minPlayers = 10) =>
        levels.Where(l => l.DropToNextPercent is not null && l.PlayersStarted >= minPlayers)
              .OrderByDescending(l => l.DropToNextPercent).FirstOrDefault();

    /// <summary>Players who started the given level, or 0 when nobody has.</summary>
    public static long PlayersAt(IReadOnlyList<LevelStat> levels, string level) =>
        levels.FirstOrDefault(l => l.Level == level)?.PlayersStarted ?? 0;

    /// <summary>Orders "1","2","10" and "puzzle-1","puzzle-2","puzzle-10" numerically; other names alphabetically after them.</summary>
    private sealed class LevelComparer : IComparer<string>
    {
        public static readonly LevelComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (px, nx) = Split(x ?? "");
            var (py, ny) = Split(y ?? "");
            var c = string.Compare(px, py, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            if (nx is not null && ny is not null) return nx.Value.CompareTo(ny.Value);
            if (nx is not null) return -1;
            if (ny is not null) return 1;
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static (string Prefix, int? Number) Split(string s)
        {
            var m = TrailingNumber().Match(s);
            return m.Success
                ? (s[..m.Index], int.Parse(m.Value, CultureInfo.InvariantCulture))
                : (s, null);
        }
    }

    [GeneratedRegex(@"\d+$")]
    private static partial Regex TrailingNumber();
}
