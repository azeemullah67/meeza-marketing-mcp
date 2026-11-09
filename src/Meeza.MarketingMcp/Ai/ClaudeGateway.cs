using Anthropic;
using Anthropic.Models.Beta.Messages;
using Meeza.Marketing.Shared.Security;
using BetaParams = Anthropic.Models.Beta.Messages.MessageCreateParams;

namespace Meeza.MarketingMcp.Ai;

/// <summary>
/// Creates Claude API clients from the user's settings (ANTHROPIC_API_KEY from the environment or the encrypted store).
/// Every request uses claude-opus-5-5 by default (override with MEEZA_AI_MODEL) with server-side refusal fallbacks enabled.
/// </summary>
public sealed class ClaudeGateway(ISettingsResolver settings)
{
    public const string DefaultModel = "claude-opus-5-5";
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(settings.Get(SettingKeys.AnthropicApiKey));

    public string Model => settings.Get(SettingKeys.AiModel) is { Length: > 0 } m ? m : DefaultModel;

    public const string Hint =
        "Add an Anthropic API key in Integrations → Claude (or set ANTHROPIC_API_KEY). It is stored encrypted on this computer and only used for the AI analyst and research.";

    public AnthropicClient Client()
    {
        var key = settings.Get(SettingKeys.AnthropicApiKey);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(Hint);
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
