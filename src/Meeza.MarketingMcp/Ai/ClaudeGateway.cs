using Anthropic;
using Anthropic.Models.Beta.Messages;
using Meeza.Marketing.Shared.Security;
using BetaParams = Anthropic.Models.Beta.Messages.MessageCreateParams;

namespace Meeza.MarketingMcp.Ai;

/// <summary>
/// Creates Claude API clients from the user's settings (ANTHROPIC_API_KEY from the environment or the encrypted store),
/// and decides whether the AI analyst runs on the API or on the Claude Code app installed on this computer (<see cref="LocalClaudeCli"/>).
/// Every request uses claude-opus-5-5 by default (override with MEEZA_AI_MODEL) with server-side refusal fallbacks enabled.
/// </summary>
public sealed class ClaudeGateway(ISettingsResolver settings, LocalClaudeLocator? local = null)
{
    private readonly LocalClaudeLocator _local = local ?? LocalClaudeLocator.Default;

    /// <summary>Path of the Claude Code app on this computer, or null when it is not installed (always null in demo mode).</summary>
    public string? LocalAppPath => _local.Find();

    public const string DefaultModel = "claude-opus-5-5";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    public const string EngineApi = "api";
    public const string EngineLocal = "claude_code";

    public bool HasApiKey => !string.IsNullOrWhiteSpace(settings.Get(SettingKeys.AnthropicApiKey));

    /// <summary>The engine set in Data sources → Claude; null = automatic.</summary>
    public string? EngineSetting => settings.Get(SettingKeys.AiEngine) is { Length: > 0 } e ? e.Trim().ToLowerInvariant() : null;

    /// <summary>
    /// Which engine the AI analyst uses: the one set, else the API when a key is saved, else the Claude app on this computer
    /// when it is installed. Null when neither is available.
    /// </summary>
    public string? Engine => EngineSetting switch
    {
        EngineApi => HasApiKey ? EngineApi : null,
        EngineLocal => LocalAppPath is not null ? EngineLocal : null,
        _ => HasApiKey ? EngineApi : LocalAppPath is not null ? EngineLocal : null,
    };

    public bool UsesLocalApp => Engine == EngineLocal;

    /// <summary>The AI analyst can run (API key, or the local Claude app). Market research needs <see cref="HasApiKey"/>.</summary>
    public bool IsConfigured => Engine is not null;

    /// <summary>The model the API engine uses. The local app uses its own default unless a model is set.</summary>
    public string Model => ModelSetting ?? DefaultModel;

    public string? ModelSetting => settings.Get(SettingKeys.AiModel) is { Length: > 0 } m ? m : null;

    public const string Hint =
        "Set up Claude in Data sources → Claude: install the Claude Code app on this computer and sign in (no API key needed), " +
        "or add an Anthropic API key (or set ANTHROPIC_API_KEY). The key is stored encrypted on this computer.";

    public const string ApiKeyHint =
        "Market research needs an Anthropic API key, because its web search and citations run through the Claude API. Add one in Data sources → Claude (or set ANTHROPIC_API_KEY).";

    /// <summary>One headless request to the Claude app on this computer (no tools, no MCP servers, the person's own Claude login).</summary>
    public Task<LocalClaudeResult> RunLocalAsync(string systemPrompt, string prompt, CancellationToken ct) =>
        LocalClaudeCli.RunAsync(LocalAppPath ?? throw new InvalidOperationException(Hint), systemPrompt, prompt, ModelSetting, ct);

    public AnthropicClient Client()
    {
        var key = settings.Get(SettingKeys.AnthropicApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(ApiKeyHint);
        return new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <summary>Base request: model, effort, refusal fallback ("default" mode: a policy decline is re-served by a suitable model chosen by refusal category, inside the same call).</summary>
    public BetaParams Params(int maxTokens, IReadOnlyList<BetaMessageParam> messages, string system, IReadOnlyList<BetaToolUnion>? tools = null, Effort effort = Effort.High) => new()
    {
        Model = Model,
        MaxTokens = maxTokens,
        System = system,
        Messages = messages.ToList(),
        Tools = tools?.ToList(),
        OutputConfig = new BetaOutputConfig { Effort = effort },
        Betas = [FallbackBeta],
        Fallbacks = new Default(),
    };
}
