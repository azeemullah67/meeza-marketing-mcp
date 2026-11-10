using System.Text.Json;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;
using Meeza.MarketingMcp.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

public class NuxJourneyTests
{
    private static readonly List<NuxStep> Journey =
    [
        new("tutorial_begin", "Tutorial started"),
        new("tutorial_step_3"),
        new("tutorial_complete", "Tutorial done"),
    ];

    [Fact]
    public void Journey_is_saved_in_order_and_kept_when_an_update_omits_it()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "Gapsy", NuxEvents = Journey });
        Assert.Equal(Journey, t.Projects.Require(p.Id).NuxEvents);

        // Older clients (and the MCP side) send no journey: it must survive the update.
        t.Projects.Update(p.Id, t.Projects.Require(p.Id) with { Genre = "puzzle", NuxEvents = null });
        Assert.Equal(Journey, t.Projects.Require(p.Id).NuxEvents);

        // An explicit empty list clears it.
        t.Projects.Update(p.Id, t.Projects.Require(p.Id) with { NuxEvents = [] });
        Assert.Empty(t.Projects.Require(p.Id).NuxEvents!);
    }

    [Fact]
    public void Journey_rejects_bad_names_duplicates_and_too_many_steps()
    {
        using var t = new TestDb();
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", NuxEvents = [new("tutorial begin")] }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", NuxEvents = [new("tutorial_begin"), new("Tutorial_Begin")] }));
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", NuxEvents = Enumerable.Range(1, 21).Select(i => new NuxStep($"step_{i}")).ToList() }));
        // Blank rows from the form are ignored, labels are trimmed and blank labels dropped.
        var p = t.Projects.Create(new Project { Name = "X", NuxEvents = [new(""), new("level_1_complete", "  "), new("level_2_complete", " Level 2 ")] });
        Assert.Equal([new NuxStep("level_1_complete"), new NuxStep("level_2_complete", "Level 2")], p.NuxEvents);
    }

    [Fact]
    public void Funnel_uses_the_journey_and_finds_the_step_where_new_players_quit()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "Gapsy", TimeZone = "UTC", NuxEvents = Journey, Connections = [TestDb.Ga4Conn] });
        var today = TimeZones.Today("UTC");
        var f = new Filters(today.AddDays(-7), today.AddDays(-1));
        var scope = new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" };
        var d = today.AddDays(-2);
        t.Facts.ReplaceWindow("ga4_daily", scope, "date", f.Start, f.End,
            [new Ga4DailyRow(d, 1000, 1000, null, null, null, null, null, null, null, null, "USD")]);
        t.Facts.ReplaceWindow("ga4_events_daily", scope, "date", f.Start, f.End, new[]
        {
            new Ga4EventRow(d, "first_open", 1000, 1000),
            new Ga4EventRow(d, "tutorial_begin", 900, 900),
            new Ga4EventRow(d, "tutorial_step_3", 400, 400), // the big drop
            new Ga4EventRow(d, "tutorial_complete", 350, 350),
        });

        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var funnel = JsonSerializer.SerializeToElement(e.Funnel(p.Id, f), Meeza.Marketing.Shared.Utilities.Json.Output);
        Assert.True(funnel.GetProperty("nuxConfigured").GetBoolean());
        var steps = funnel.GetProperty("steps").EnumerateArray().ToArray();
        var journey = steps.Where(s => s.GetProperty("nux").GetBoolean()).ToArray();
        Assert.Equal(["Tutorial started", "tutorial_step_3", "Tutorial done"], journey.Select(s => s.GetProperty("label").GetString()));
        Assert.DoesNotContain(steps, s => s.GetProperty("key").GetString() is "tutorial_begin" or "tutorial_complete"); // replaced by the journey
        Assert.Equal(40m, journey[1].GetProperty("shareOfFirstOpen").GetDecimal());
        Assert.Equal(44.44m, journey[1].GetProperty("conversionFromPrevious").GetDecimal()); // 400 / 900
        var drop = funnel.GetProperty("biggestDrop");
        Assert.Equal("Tutorial started", drop.GetProperty("from").GetString());
        Assert.Equal("tutorial_step_3", drop.GetProperty("to").GetString());
    }

    [Theory]
    // Gapsy, Oct-Nov 2026: most players are lost before the tutorial even starts.
    [InlineData(242, 185, 146, 117, "never reach tutorial_begin")]
    // Players start, then stop inside the generic tutorial_step: ask for step numbers.
    [InlineData(240, 230, 120, 110, "tutorial_step has no step number")]
    public void Tutorial_advice_points_at_the_step_where_most_players_are_lost(long opens, long begin, long step, long complete, string expected)
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "Gapsy", TimeZone = "UTC", Connections = [TestDb.Ga4Conn] });
        var today = TimeZones.Today("UTC");
        var f = new Filters(today.AddDays(-7), today.AddDays(-1));
        var d = today.AddDays(-2);
        var scope = new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" };
        t.Facts.ReplaceWindow("ga4_events_daily", scope, "date", f.Start, f.End, new[]
        {
            new Ga4EventRow(d, "first_open", opens, opens), new Ga4EventRow(d, "tutorial_begin", begin, begin),
            new Ga4EventRow(d, "tutorial_step", step, step), new Ga4EventRow(d, "tutorial_complete", complete, complete),
        });
        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var finding = e.Findings(p.Id, f).Single(x => x.Id == "funnel-tutorial");
        Assert.Contains(expected, finding.Action);
        Assert.DoesNotContain("Shorten", finding.Action);
        Assert.Equal(4, finding.Evidence.Count); // three step losses + completion
    }

    [Fact]
    public void A_custom_tutorial_start_event_is_saved_and_used_by_the_funnel_and_the_check()
    {
        using var t = new TestDb();
        Assert.Throws<ArgumentException>(() => t.Projects.Create(new Project { Name = "X", TutorialStartEvent = "tutorial start" }));
        var p = t.Projects.Create(new Project { Name = "Gapsy", TimeZone = "UTC", TutorialStartEvent = " ftue_start ", TutorialEvent = "ftue_done", Connections = [TestDb.Ga4Conn] });
        Assert.Equal("ftue_start", t.Projects.Require(p.Id).TutorialStartEvent);
        // An update that leaves it empty goes back to the default.
        Assert.Equal("tutorial_begin", t.Projects.Update(p.Id, t.Projects.Require(p.Id) with { TutorialStartEvent = null }).TutorialStart);
        t.Projects.Update(p.Id, t.Projects.Require(p.Id) with { TutorialStartEvent = "ftue_start" });

        var today = TimeZones.Today("UTC");
        var f = new Filters(today.AddDays(-7), today.AddDays(-1));
        var d = today.AddDays(-2);
        var scope = new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" };
        t.Facts.ReplaceWindow("ga4_events_daily", scope, "date", f.Start, f.End, new[]
        {
            new Ga4EventRow(d, "first_open", 200, 200), new Ga4EventRow(d, "ftue_start", 120, 120), new Ga4EventRow(d, "ftue_done", 80, 80),
            new Ga4EventRow(d, "tutorial_begin", 5, 5), // a stray standard event must not be used
        });
        t.Facts.ReplaceWindow("ga4_daily", scope, "date", f.Start, f.End, [new Ga4DailyRow(d, 200, 200, null, null, null, null, null, null, null, null, "USD")]);
        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var started = JsonSerializer.SerializeToElement(e.Funnel(p.Id, f), Meeza.Marketing.Shared.Utilities.Json.Output)
            .GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("key").GetString() == "tutorial_begin");
        Assert.Equal((120, "GA4 ftue_start"), (started.GetProperty("value").GetInt64(), started.GetProperty("source").GetString()));

        var finding = e.Findings(p.Id, f).Single(x => x.Id == "funnel-tutorial");
        Assert.Contains("never reach ftue_start", finding.Action); // 80 of 200 lost before ftue_start
    }

    [Fact]
    public void Missing_journey_event_is_unavailable_with_a_fix()
    {
        using var t = new TestDb();
        var p = t.Projects.Create(new Project { Name = "Gapsy", TimeZone = "UTC", NuxEvents = [new("tutorial_begn")], Connections = [TestDb.Ga4Conn] });
        var today = TimeZones.Today("UTC");
        var f = new Filters(today.AddDays(-7), today.AddDays(-1));
        var scope = new Dictionary<string, object?> { ["project_id"] = p.Id, ["property_id"] = "123456789" };
        t.Facts.ReplaceWindow("ga4_daily", scope, "date", f.Start, f.End, [new Ga4DailyRow(today.AddDays(-2), 10, 10, null, null, null, null, null, null, null, null, "USD")]);
        var e = new AnalyticsEngine(t.Projects, t.Syncs, t.Data, new SyncEngine(t.Projects, t.Syncs, [], NullLogger<SyncEngine>.Instance));
        var step = JsonSerializer.SerializeToElement(e.Funnel(p.Id, f), Meeza.Marketing.Shared.Utilities.Json.Output)
            .GetProperty("steps").EnumerateArray().Single(s => s.GetProperty("nux").GetBoolean());
        Assert.Equal(JsonValueKind.Null, step.GetProperty("value").ValueKind);
        Assert.Contains("spelling", step.GetProperty("note").GetString());
    }
}
