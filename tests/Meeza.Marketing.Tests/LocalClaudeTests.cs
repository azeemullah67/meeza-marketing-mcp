using System.Text.Json;
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

    /// <summary>The fake-claude helper built next to this test project (tests/Meeza.Marketing.FakeClaude).</summary>
    private static string FakeClaude()
    {
        var tfm = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var config = tfm.Parent!.Name;
        var testsDir = tfm.Parent.Parent!.Parent!.Parent!.FullName;
        var path = Path.Combine(testsDir, "Meeza.Marketing.FakeClaude", "bin", config, tfm.Name, OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude");
        Assert.True(File.Exists(path), $"fake-claude was not built at {path}");
        return path;
    }

    [Fact]
    public async Task System_prompt_arrives_as_one_intact_argument_and_the_question_on_stdin()
    {
        // Characters a shell or cmd.exe would interpret: quotes, &, |, <, >, ^, %VAR%, newlines, and non-ASCII text.
        const string system = "Rules:\n- Say \"hi\" & echo PWNED > pwned.txt | more\n- 100% of %PATH% ^caret <tag>\n\nLast line: ünïcødé → ✓";
        const string question = "Why did CPI rise? & %PATH% \"quoted\"\nSecond line ✓";
        var r = await LocalClaudeCli.RunAsync(FakeClaude(), system, question, "some-model", default);

        Assert.Equal("fake-model", r.Model);
        var echo = JsonDocument.Parse(r.Text).RootElement;
        var argv = echo.GetProperty("argv").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal(system, argv[argv.IndexOf("--system-prompt") + 1]);
        Assert.Equal(question, echo.GetProperty("stdin").GetString());
        Assert.DoesNotContain(argv, a => a.Contains("Why did CPI rise", StringComparison.Ordinal)); // the question never goes on the command line
        // Isolation flags, including the two empty values, arrive as separate arguments.
        Assert.Equal("", argv[argv.IndexOf("--tools") + 1]);
        Assert.Equal("", argv[argv.IndexOf("--setting-sources") + 1]);
        Assert.Contains("--strict-mcp-config", argv);
        Assert.Contains("--no-session-persistence", argv);
        Assert.Equal("some-model", argv[argv.IndexOf("--model") + 1]);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "meeza-marketing-claude", "pwned.txt")));
    }

    [Fact]
    public void Only_the_native_executable_is_used_never_a_cmd_shim()
    {
        var dir = Directory.CreateTempSubdirectory("meeza-claude-find-");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                File.WriteAllText(Path.Combine(dir.FullName, "claude.cmd"), "@echo off\r\n");
                Assert.Null(LocalClaudeCli.FindIn([dir.FullName])); // npm shim alone: not used
            }
            Assert.Null(LocalClaudeCli.FindIn([Path.Combine(dir.FullName, "missing"), "bad\0path"]));
            var exe = Path.Combine(dir.FullName, LocalClaudeCli.ExecutableName);
            File.WriteAllText(exe, "");
            Assert.Equal(exe, LocalClaudeCli.FindIn(["bad\0path", dir.FullName]));
        }
        finally { dir.Delete(true); }
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
