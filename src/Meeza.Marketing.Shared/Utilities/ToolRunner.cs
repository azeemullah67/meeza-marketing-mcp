using Meeza.Marketing.Shared.Models;

namespace Meeza.Marketing.Shared.Utilities;

/// <summary>
/// Executes a tool body and always returns a JSON <see cref="ToolResponse"/>. Exceptions become
/// structured, redacted errors so Claude can explain what went wrong instead of receiving an opaque failure.
/// </summary>
public static class ToolRunner
{
    public static async Task<string> RunAsync(string tool, Func<ToolResponse, Task> body)
    {
        var response = new ToolResponse { Tool = tool };
        try
        {
            await body(response).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response.Ok = false;
            response.Data = null;
            response.Errors.Add(new PlatformError(PlatformOf(ex), Describe(ex)));
        }
        return Json.Serialize(response);
    }

    public static string Describe(Exception ex) => ex switch
    {
        ArgumentException a => Redactor.Redact(a.Message),
        PlatformApiException or PlatformNotConfiguredException or AccountAccessDeniedException => Redactor.Redact(ex.Message),
        HttpRequestException h => Redactor.Redact($"Network error: {h.Message}"),
        _ => Redactor.Redact($"{ex.GetType().Name}: {ex.Message}"),
    };

    private static string PlatformOf(Exception ex) => ex switch
    {
        PlatformApiException p => p.Platform,
        PlatformNotConfiguredException n => n.Platform,
        _ => "server",
    };
}
