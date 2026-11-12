using Meeza.MarketingMcp.Ai;

namespace Meeza.Marketing.Tests;

public class LocalClaudeTests
{
    [Fact]
    public void Local_prompt_reads_the_data_blocks_instead_of_tools()
    {
        var prompt = AnalystService.LocalSystemPrompt;
        Assert.NotEqual(AnalystService.SystemPrompt, prompt);
        Assert.Contains("<data> blocks", prompt);
        Assert.DoesNotContain("Call tools before answering", prompt);
        Assert.DoesNotContain("which you read with the tools", prompt);
        Assert.Contains("Never invent data.", prompt); // the remaining rules stay
    }

    [Fact]
    public void Successful_output_returns_the_answer_and_model()
    {
        var r = LocalClaudeCli.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"**Answer** ok","stop_reason":"end_turn","modelUsage":{"claude-opus-5-5":{}}}""", "", 0);
        Assert.Equal(("**Answer** ok", "claude-opus-5-5", "end_turn"), (r.Text, r.Model, r.StopReason));
    }

    [Fact]
    public void Errors_are_reported_with_a_sign_in_tip()
    {
        var err = Assert.Throws<InvalidOperationException>(() => LocalClaudeCli.Parse("""{"is_error":true,"result":"Not logged in · Please run /login"}""", "", 1));
        Assert.Contains("Not logged in", err.Message);
        Assert.Contains("/login", err.Message);
        var crash = Assert.Throws<InvalidOperationException>(() => LocalClaudeCli.Parse("", "something broke", 2));
        Assert.Contains("something broke", crash.Message);
    }
}
