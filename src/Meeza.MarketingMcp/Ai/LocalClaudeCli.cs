using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Meeza.MarketingMcp.Ai;

public sealed record LocalClaudeResult(string Text, string? Model, string? StopReason);

/// <summary>Finds the Claude Code app. Demo mode and tests register <see cref="None"/> so nothing reaches the person's Claude account.</summary>
public sealed record LocalClaudeLocator(Func<string?> Find)
{
    public static readonly LocalClaudeLocator Default = new(LocalClaudeCli.Find);
    public static readonly LocalClaudeLocator None = new(() => null);
}

/// <summary>
/// Runs the Claude Code app installed on this computer in headless mode (<c>claude -p</c>), signed in with the person's
/// own Claude account, so the AI analyst works without an Anthropic API key. Each call is isolated: no tools, no MCP servers,
/// no user or project settings, no saved session, and an empty working folder. The prompt goes through stdin.
/// </summary>
public static class LocalClaudeCli
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>Full path of the claude executable, or null when Claude Code is not installed.</summary>
    public static string? Find()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 0) dirs.Add(Path.Combine(home, ".local", "bin")); // default location of the native installer
        return FindIn(dirs);
    }

    /// <summary>
    /// The executable name looked for: on Windows only the native <c>claude.exe</c>. A <c>claude.cmd</c> (npm shim) is
    /// never used, because Windows runs it through cmd.exe, which re-parses the arguments: it expands <c>%VAR%</c> and
    /// cuts a multi-line system prompt at its first line break (verified on .NET 9 / Windows 11).
    /// </summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "claude.exe" : "claude";

    /// <summary>The first <see cref="ExecutableName"/> in <paramref name="dirs"/>. Public for tests.</summary>
    public static string? FindIn(IEnumerable<string> dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                var path = Path.Combine(dir, ExecutableName);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { } // malformed PATH entry
        }
        return null;
    }

    public static async Task<LocalClaudeResult> RunAsync(string exe, string systemPrompt, string prompt, string? model, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "meeza-marketing-claude");
        Directory.CreateDirectory(work);

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = work, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[]
        {
            "-p", "--output-format", "json", "--no-session-persistence", "--disable-slash-commands",
            "--strict-mcp-config", "--setting-sources", "", "--tools", "", "--system-prompt", systemPrompt,
        }) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(model); }
        // Use the signed-in Claude account, never an API key from the environment, and don't inherit a parent Claude Code session.
        foreach (var k in new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT" })
            psi.Environment.Remove(k);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the Claude app.");
        try
        {
            var stdout = proc.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = proc.StandardError.ReadToEndAsync(timeout.Token);
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), timeout.Token).ConfigureAwait(false);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return Parse(await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), proc.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new InvalidOperationException("The Claude app did not answer within 10 minutes. Try a narrower question.");
        }
    }

    /// <summary>Reads the <c>--output-format json</c> result. Public for tests.</summary>
    public static LocalClaudeResult Parse(string stdout, string stderr, int exitCode)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(stdout.Trim() is { Length: > 0 } s ? s : "null"); } catch (JsonException) { }
        using (doc)
        {
            var root = doc?.RootElement;
            if (root is not { ValueKind: JsonValueKind.Object } r)
            {
                var detail = (stderr.Trim() is { Length: > 0 } e ? e : stdout.Trim()) is { Length: > 0 } d ? d : $"exit code {exitCode}";
                throw new InvalidOperationException("The Claude app failed: " + Clip(detail) + LoginTip(detail));
            }
            string? Str(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var text = Str("result") ?? "";
            var isError = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            if (isError || exitCode != 0)
                throw new InvalidOperationException("The Claude app returned an error: " + Clip(text.Length > 0 ? text : Str("subtype") ?? $"exit code {exitCode}") + LoginTip(text));
            var model = r.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object
                ? mu.EnumerateObject().Select(p => p.Name).FirstOrDefault() : null;
            return new LocalClaudeResult(text, model, Str("stop_reason"));
        }
    }

    private static string LoginTip(string detail) =>
        detail.Contains("log", StringComparison.OrdinalIgnoreCase) && (detail.Contains("in", StringComparison.OrdinalIgnoreCase) || detail.Contains("auth", StringComparison.OrdinalIgnoreCase))
            ? " Open a terminal, run claude, and sign in with /login." : "";

    private static string Clip(string s) => s.Length > 400 ? s[..400] + "…" : s;
}
