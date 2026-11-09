using System.Globalization;
using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Intelligence;
using Meeza.MarketingMcp.Storage;

namespace Meeza.MarketingMcp.Ai;

public sealed record ToolCallEvidence(string Tool, IReadOnlyDictionary<string, JsonElement> Input, int ResultChars, bool Error);

public sealed record AnalystAnswer(string ConversationId, string Answer, IReadOnlyList<ToolCallEvidence> Evidence, string? StopReason, string Model);

/// <summary>
/// Conversational marketing analyst. Claude answers only through tools that read the project's synchronized data
/// (the same computations the dashboard shows), and must cite those numbers. Conversation turns are stored locally.
/// </summary>
public sealed class AnalystService(ClaudeGateway claude, AnalyticsEngine engine, ProjectStore projects, MarketingDb db)
{
    private const int MaxToolRounds = 10;
    private const int MaxToolResultChars = 60_000;

    public const string SystemPrompt = """
        You are the marketing analyst for one mobile game project. You answer questions about its user acquisition, retention,
        creatives, funnel, monetization and budget using ONLY the project's synchronized data, which you read with the tools.

        Rules:
        - Call tools before answering anything quantitative. Never state a number that did not come from a tool result in this conversation.
        - Never invent data. If a metric is null, "unavailable", missing, or based on too few users, say so plainly and say what would be needed to measure it.
        - Distinguish clearly between OBSERVED data (cite the metric, value, period and source), HYPOTHESES (label them as such) and RECOMMENDATIONS.
        - Treat the tools' notes, definitions, freshness and maturity fields as part of the evidence: stale data, provisional cohorts,
          platform-attributed installs (which overlap and must never be summed) and currency differences change what you can conclude.
        - Small samples: below ~100 users per retention point or ~20 installs per campaign, say the result is directional.
        - You have read-only access. You cannot change budgets or campaigns; recommend changes for the user to make.

        Answer format (Markdown, concise):
        1. **Answer** - one or two sentences.
        2. **Evidence** - bullet list of the specific numbers (metric, value, comparison, period, source).
        3. **Likely causes** - labelled hypotheses with your confidence (high/medium/low) and why.
        4. **What to do** - prioritized actions, with expected impact when the data supports an estimate.
        5. **How to validate** - what to watch, and when.
        Skip sections that do not apply to a simple factual question.
        """;

    public bool IsConfigured => claude.IsConfigured;

    public async Task<AnalystAnswer> AskAsync(string projectId, string? conversationId, string question, CancellationToken ct)
    {
        var p = projects.Require(projectId);
        question = question.Trim();
        if (question.Length is 0 or > 4000) throw new ArgumentException("Ask a question of up to 4,000 characters.");
        conversationId ??= Guid.NewGuid().ToString("N");

        // Prior turns are replayed as plain text (append-only history).
        var history = History(projectId, conversationId).TakeLast(20).ToList();
        var messages = new List<BetaMessageParam>();
        foreach (var h in history)
            messages.Add(h.Role == "user" ? new BetaUserMessageParam { Content = h.Content } : new BetaAssistantMessageParam { Content = h.Content });
        var today = AnalyticsEngine.Today(p);
        messages.Add(new BetaUserMessageParam
        {
            Content = $"Project: {p.Name} (id {p.Id}; genre {p.Genre ?? "not set"}; platforms {p.Platforms ?? "not set"}; target countries {p.TargetCountries ?? "not set"}; " +
                      $"stage {p.LaunchStage ?? "not set"}; objectives: {p.Objectives ?? "not set"}). Today in the project's time zone: {Db.Iso(today)}. " +
                      $"Unless the question says otherwise, analyse the last 14 complete days ({Db.Iso(today.AddDays(-14))} to {Db.Iso(today.AddDays(-1))}) against the 14 days before.\n\nQuestion: {question}",
        });
        Save(projectId, conversationId, "user", question, null);

        using var client = claude.Client();
        var evidence = new List<ToolCallEvidence>();
        string? stop = null;
        var answer = new List<string>();
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var response = await client.Beta.Messages.Create(claude.Params(16_000, messages, SystemPrompt, Tools), ct).ConfigureAwait(false);
            stop = response.StopReason?.ToString();
            var assistant = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            answer.Clear();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var text))
                {
                    assistant.Add(new BetaTextBlockParam { Text = text.Text });
                    answer.Add(text.Text);
                }
                else if (block.TryPickThinking(out var thinking))
                    assistant.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                else if (block.TryPickRedactedThinking(out var redacted))
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                else if (block.TryPickToolUse(out var use))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                    var (content, isError) = RunTool(p, today, use.Name, use.Input);
                    evidence.Add(new ToolCallEvidence(use.Name, use.Input, content.Length, isError));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = use.ID, Content = content, IsError = isError });
                }
            }
            if (response.StopReason == BetaStopReason.Refusal)
            {
                answer.Clear();
                answer.Add("The model declined to answer this question. Try rephrasing it as a question about the project's marketing data.");
                break;
            }
            if (results.Count == 0) break; // end_turn / max_tokens: final answer is in `answer`
            messages.Add(new BetaAssistantMessageParam { Content = assistant });
            messages.Add(new BetaUserMessageParam { Content = results });
        }

        var final = string.Join("\n\n", answer).Trim();
        if (final.Length == 0) final = "No answer was produced (the analysis hit its tool-call limit). Try a narrower question.";
        Save(projectId, conversationId, "assistant", final, JsonSerializer.Serialize(evidence));
        return new AnalystAnswer(conversationId, final, evidence, stop, claude.Model);
    }

    // ---------------------------------------------------------------- tools

    private static BetaTool Tool(string name, string description, Dictionary<string, object> props) => new()
    {
        Name = name,
        Description = description,
        InputSchema = new()
        {
            Properties = props.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value)),
        },
    };

    private static readonly Dictionary<string, object> RangeProps = new()
    {
        ["start"] = new { type = "string", description = "Start date yyyy-MM-dd. Default: 14 days ago." },
        ["end"] = new { type = "string", description = "End date yyyy-MM-dd, inclusive. Default: yesterday." },
        ["country"] = new { type = "string", description = "Optional ISO country code filter, e.g. PH." },
        ["platform"] = new { type = "string", description = "Optional ad platform filter: google_ads or meta." },
        ["campaign"] = new { type = "string", description = "Optional campaign id filter." },
        ["channel"] = new { type = "string", description = "Optional GA4 first-user channel group filter, e.g. 'Paid Search'." },
    };

    private static Dictionary<string, object> With(params (string Key, object Schema)[] extra)
    {
        var d = new Dictionary<string, object>(RangeProps);
        foreach (var (k, v) in extra) d[k] = v;
        return d;
    }

    private static readonly List<BetaToolUnion> Tools =
    [
        Tool("get_overview", "Executive KPIs for a period vs the previous period of equal length: spend, installs, CPI, CPM, CPC, CTR, IPM, reach, frequency, DAU/WAU/MAU, D1-D30 retention, revenue, ARPDAU, ARPU, LTV, ROAS, organic vs paid. Each KPI has target, source, freshness and definition.", RangeProps),
        Tool("get_findings", "The automatic analyst's rule-based findings for a period: problems with severity, evidence, hypothesis, confidence, action and validation.", RangeProps),
        Tool("get_campaigns", "Campaign / ad set / ad table with period-over-period changes, spend share, GA4 D1/D7 retention of users by campaign, and flags (high_cpi, wasted_spend, low_ctr, rising_cpm, declining_cvr, poor_quality, good_retention, scale_candidate, investigate_before_scaling). Also country rows and organic demand by country.",
            With(("level", new { type = "string", @enum = new[] { "campaign", "adset", "ad" } }))),
        Tool("get_retention", "Exact-day cohort retention (D0-D30): heatmap, blended mature curve, previous-period curve, curves per segment, churn trend and new vs returning users.",
            With(("segment_type", new { type = "string", @enum = new[] { "all", "country", "platform", "channel", "campaign", "ad_group" } }),
                 ("segment_value", new { type = "string", description = "Optional value of the segment, e.g. PH." }))),
        Tool("get_creatives", "Creative performance: each creative's spend, CTR, CPI, IPM, composite score, classification (best, underperforming, fatigued, testing, steady), fatigue evidence, format/hook/theme comparisons and suggestions.", RangeProps),
        Tool("get_funnel", "Acquisition funnel impressions → clicks → installs → first open → tutorial → D1 return → purchasers/ad viewers, with step conversion vs previous period and diagnosis.", RangeProps),
        Tool("get_attribution", "Reconciliation of Google Ads installs, Meta installs, GA4 first opens and GA4 new users by source per day, with definitions of each source.", RangeProps),
        Tool("get_forecast", "CPI forecast, spend → installs response model, budget scenarios, LTV estimate and budget reallocation proposals, from the last 28 days.",
            new() { ["daily_budget"] = new { type = "number", description = "Optional daily budget to center the scenarios on." } }),
        Tool("get_sync_status", "Freshness of each connected data source: last attempt, last success, latest data date, errors.", new()),
    ];

    private (string Content, bool Error) RunTool(Project p, DateOnly today, string name, IReadOnlyDictionary<string, JsonElement> input)
    {
        try
        {
            string? S(string k) => input.TryGetValue(k, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            DateOnly D(string k, DateOnly fallback) => S(k) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : fallback;
            var f = new Filters(D("start", today.AddDays(-14)), D("end", today.AddDays(-1)), S("country")?.ToUpperInvariant(), S("platform"), S("campaign"), S("channel"));
            if (f.End < f.Start || f.Days > 400) throw new ArgumentException("Invalid date range (max 400 days).");
            object result = name switch
            {
                "get_overview" => engine.Overview(p.Id, f),
                "get_findings" => engine.Findings(p.Id, f),
                "get_campaigns" => engine.Campaigns(p.Id, f, S("level") ?? "campaign"),
                "get_retention" => engine.Retention(p.Id, f, S("segment_type") ?? "all", S("segment_value"), "exact"),
                "get_creatives" => engine.Creatives(p.Id, f),
                "get_funnel" => engine.Funnel(p.Id, f),
                "get_attribution" => engine.Attribution(p.Id, f),
                "get_forecast" => engine.Forecast(p.Id, f, input.TryGetValue("daily_budget", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetDecimal() : null),
                "get_sync_status" => engine.Status(p),
                _ => throw new ArgumentException($"Unknown tool {name}."),
            };
            var json = Json.Serialize(result);
            return json.Length > MaxToolResultChars
                ? (json[..MaxToolResultChars] + "\n…[truncated: narrow the date range or filters for complete data]", false)
                : (json, false);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return ($"Error: {ex.Message}", true);
        }
    }

    // ---------------------------------------------------------------- history

    public sealed record StoredMessage(long Id, string ConversationId, string Role, string Content, string? EvidenceJson, string CreatedAt);

    public IReadOnlyList<StoredMessage> History(string projectId, string conversationId)
    {
        using var c = db.Open();
        return c.Query("SELECT * FROM analyst_messages WHERE project_id=@projectId AND conversation_id=@conversationId ORDER BY id",
            new { projectId, conversationId }, Map);
    }

    public IReadOnlyList<object> Conversations(string projectId)
    {
        using var c = db.Open();
        return c.Query("""
            SELECT conversation_id, MIN(created_at) AS started, MAX(created_at) AS last,
                   (SELECT content FROM analyst_messages m2 WHERE m2.conversation_id = m.conversation_id AND m2.role='user' ORDER BY id LIMIT 1) AS title
            FROM analyst_messages m WHERE project_id=@projectId GROUP BY conversation_id ORDER BY last DESC LIMIT 30
            """, new { projectId }, r => (object)new { conversationId = r.Str("conversation_id"), started = r.Str("started"), last = r.Str("last"), title = r.Str("title") });
    }

    private void Save(string projectId, string conversationId, string role, string content, string? evidence)
    {
        using var c = db.Open();
        c.Exec("INSERT INTO analyst_messages(project_id, conversation_id, role, content, evidence_json, created_at) VALUES(@projectId, @conversationId, @role, @content, @evidence, @now)",
            new { projectId, conversationId, role, content, evidence, now = Db.Now() });
    }

    private static StoredMessage Map(Microsoft.Data.Sqlite.SqliteDataReader r) =>
        new(r.Long("id")!.Value, r.Str("conversation_id")!, r.Str("role")!, r.Str("content")!, r.Str("evidence_json"), r.Str("created_at")!);
}
