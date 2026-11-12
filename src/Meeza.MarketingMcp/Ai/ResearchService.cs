using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Storage;
using Microsoft.Extensions.Logging;

namespace Meeza.MarketingMcp.Ai;

public sealed record Citation(int Index, string Url, string? Title, string? CitedText);

public sealed record ResearchReport(long Id, string ProjectId, string Topic, string Question, string Status, string? Content, IReadOnlyList<Citation> Citations, string? Error, string CreatedAt, string? FinishedAt);

/// <summary>
/// Market and competitor research with Claude's server-side web search and web fetch. Every claim taken from the web keeps
/// its citation (URL, title, quoted text); the prompt requires verified facts and estimates to be in separate sections.
/// Reports run in the background and are stored per project.
/// </summary>
public sealed class ResearchService(ClaudeGateway claude, ProjectStore projects, MarketingDb db, ILogger<ResearchService> logger)
{
    private const int MaxContinuations = 6;
    /// <summary>Global cap on simultaneous research jobs: each one is a long web-search conversation that costs real money.</summary>
    public const int MaxConcurrent = 2;
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _cancels = new();

    public static readonly IReadOnlyDictionary<string, string> Topics = new Dictionary<string, string>
    {
        ["competitors"] = "Identify the closest competitor games (same genre and core mechanic) on Google Play and the App Store. For each: developer, store ratings and rating count, install band, monetization, last update, and what players praise or complain about in reviews.",
        ["creatives"] = "Research the advertising creatives competitors and the genre are running now (Meta Ad Library, Google Ads Transparency Center, industry creative reports): formats, hooks, first-3-second patterns, gameplay vs. fake/promotional ads, and what is trending.",
        ["market"] = "Research the market for this genre: recent growth or decline, top-grossing and top-downloaded titles, notable launches, and trends in monetization and retention reported by industry sources (with dates).",
        ["countries"] = "Find which countries are the best user-acquisition opportunities for this genre: published CPI/CPM ranges by country and genre, market size, payer conversion, language/localization needs. Compare with the project's target countries.",
        ["aso"] = "Analyse App Store Optimization for this game and its competitors: title and short-description keywords, screenshot/video patterns, ratings, and keyword opportunities the game is not using.",
        ["ua_opportunities"] = "Find user-acquisition opportunities: under-used ad networks and placements for this genre, creator/UGC and playable-ad practice, cross-promotion, seasonal moments, and community channels, with evidence of what works.",
        ["custom"] = "",
    };

    public const string SystemPrompt = """
        You are a mobile game market researcher. Use web search and web fetch to research the question, then write a report.

        Rules:
        - Every factual claim from the web must be backed by a citation from your searches. Prefer primary sources: store listings,
          ad libraries, company reports, and named industry reports (with their publication date).
        - Never invent numbers, ratings, install counts or competitor names. If a figure cannot be found, say "not found".
        - Separate clearly:
          ## Verified findings  (each item cited; include the date of the source)
          ## Estimates and inferences  (your reasoning from the findings; label each as an estimate and say what it is based on)
          ## Recommendations  (specific, testable next steps for this game)
          ## Open questions  (what could not be verified)
        - Note when a source is old (more than 18 months) or when sources disagree.
        - Be concise. Use tables where they help comparison.
        """;

    public bool IsConfigured => claude.HasApiKey;

    public ResearchReport Start(string projectId, string topic, string? question)
    {
        var p = projects.Require(projectId);
        if (!claude.HasApiKey) throw new InvalidOperationException(ClaudeGateway.ApiKeyHint);
        if (!Topics.TryGetValue(topic, out var brief)) throw new ArgumentException($"Unknown topic '{topic}'. Use: {string.Join(", ", Topics.Keys)}.");
        question = (question ?? "").Trim();
        if (topic == "custom" && question.Length == 0) throw new ArgumentException("Enter a research question.");
        if (question.Length > 2000) throw new ArgumentException("Keep the question under 2,000 characters.");
        var full = string.IsNullOrEmpty(question) ? brief : string.IsNullOrEmpty(brief) ? question : $"{brief}\n\nSpecific question: {question}";

        // Spend guards: the UI disables its button, but the API is the boundary that counts.
        if (_running.Count >= MaxConcurrent)
            throw new InvalidOperationException($"{MaxConcurrent} research jobs are already running. Wait for one to finish or cancel it first.");
        if (List(p.Id).Any(r => r.Status == "running"))
            throw new InvalidOperationException("A research job is already running for this project. Wait for it to finish or cancel it first.");

        long id;
        using (var c = db.Open())
        {
            c.Exec("INSERT INTO research_reports(project_id, topic, question, status, created_at) VALUES(@projectId, @topic, @q, 'running', @now)",
                new { projectId, topic, q = full, now = Db.Now() });
            id = c.Scalar<long>("SELECT last_insert_rowid()");
        }
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20)); // hard ceiling per job
        _cancels[id] = cts;
        _running[id] = Task.Run(async () =>
        {
            try { await RunAsync(id, p, full, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                Finish(id, "cancelled", null, [], cts.IsCancellationRequested ? "Cancelled." : "Timed out.");
            }
            catch (Exception ex)
            {
                // Provider exceptions can echo request details; never log them raw.
                var safe = Redactor.Redact(ex.Message);
                logger.LogWarning("Research {Id} failed: {Error}", id, safe);
                Finish(id, "failed", null, [], safe);
            }
            finally
            {
                _running.TryRemove(id, out _);
                _cancels.TryRemove(id, out _);
                cts.Dispose();
            }
        });
        return Get(projectId, id)!;
    }

    /// <summary>Stops a running job of this project. Returns false when it is not running (already finished, or unknown).</summary>
    public bool Cancel(string projectId, long id)
    {
        if (Get(projectId, id) is null) return false;
        if (!_cancels.TryGetValue(id, out var cts)) return false;
        try { cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    private async Task RunAsync(long id, Project p, string question, CancellationToken ct)
    {
        var context = $"""
            Game: {p.Name}
            Genre: {p.Genre ?? "not specified"}
            Platforms: {p.Platforms ?? "not specified"}
            Google Play: {p.AndroidStoreUrl ?? "not specified"}
            App Store: {p.IosStoreUrl ?? "not specified"}
            Target countries: {p.TargetCountries ?? "not specified"}
            Monetization: {p.Monetization ?? "not specified"}
            Launch stage: {p.LaunchStage ?? "not specified"}
            Marketing objectives: {p.Objectives ?? "not specified"}
            Today: {DateTime.UtcNow:yyyy-MM-dd}

            Research task: {question}
            """;
        var messages = new List<BetaMessageParam> { new BetaUserMessageParam { Content = context } };
        var tools = new List<BetaToolUnion>
        {
            new(new BetaWebSearchTool20260209 { MaxUses = 12 }),
            new(new BetaWebFetchTool20260209 { MaxUses = 8 }),
        };
        using var client = claude.Client();
        var text = new StringBuilder();
        var citations = new List<Citation>();
        for (var i = 0; i <= MaxContinuations; i++)
        {
            var response = await client.Beta.Messages.Create(claude.Params(16_000, messages, SystemPrompt, tools), ct).ConfigureAwait(false);
            foreach (var block in response.Content)
            {
                if (!block.TryPickText(out var t)) continue;
                text.Append(t.Text);
                var marks = new List<int>();
                foreach (var cit in t.Citations ?? [])
                {
                    if (cit.Value is not BetaCitationsWebSearchResultLocation web) continue;
                    var existing = citations.FirstOrDefault(c => c.Url == web.Url);
                    if (existing is null) { existing = new Citation(citations.Count + 1, web.Url, web.Title, web.CitedText); citations.Add(existing); }
                    if (!marks.Contains(existing.Index)) marks.Add(existing.Index);
                }
                if (marks.Count > 0) text.Append(string.Concat(marks.Select(m => $" [{m}]")));
            }
            if (response.StopReason == BetaStopReason.Refusal)
            {
                Finish(id, "failed", null, [], "The model declined this research request.");
                return;
            }
            // pause_turn: the server-side tool loop hit its iteration limit; send the turn back to let it continue.
            if (response.StopReason != BetaStopReason.PauseTurn) break;
            messages.Add(new BetaAssistantMessageParam { Content = response.Content.Select(b => new BetaContentBlockParam(b.Json)).ToList() });
        }
        Finish(id, "done", text.ToString().Trim(), citations, null);
    }

    private void Finish(long id, string status, string? content, IReadOnlyList<Citation> citations, string? error)
    {
        using var c = db.Open();
        c.Exec("UPDATE research_reports SET status=@status, content=@content, citations_json=@cites, error=@error, finished_at=@now WHERE id=@id",
            new { id, status, content, cites = JsonSerializer.Serialize(citations), error, now = Db.Now() });
    }

    public ResearchReport? Get(string projectId, long id) => List(projectId, id).FirstOrDefault();

    public IReadOnlyList<ResearchReport> List(string projectId, long? id = null)
    {
        using var c = db.Open();
        var sql = "SELECT * FROM research_reports WHERE project_id=@projectId" + (id is null ? "" : " AND id=@id") + " ORDER BY id DESC LIMIT 50";
        return c.Query(sql, new { projectId, id }, r => new ResearchReport(r.Long("id")!.Value, r.Str("project_id")!, r.Str("topic")!, r.Str("question")!,
            r.Str("status") == "running" && !_running.ContainsKey(r.Long("id")!.Value) ? "interrupted" : r.Str("status")!,
            r.Str("content"), r.Str("citations_json") is { } j ? JsonSerializer.Deserialize<List<Citation>>(j) ?? [] : [],
            r.Str("error"), r.Str("created_at")!, r.Str("finished_at")));
    }
}
