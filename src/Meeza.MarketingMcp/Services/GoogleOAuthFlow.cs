using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;

namespace Meeza.MarketingMcp.Services;

/// <summary>
/// OAuth 2.0 installed-app flow with a loopback redirect and PKCE, used once to obtain a Google Ads
/// refresh token. The browser talks to Google directly; this process only receives the authorization code.
/// </summary>
public static class GoogleOAuthFlow
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";

    public static async Task<string> GetRefreshTokenAsync(string clientId, string clientSecret, string scope, TextWriter output, CancellationToken ct)
    {
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(24));

        var url = $"{AuthEndpoint}?client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirect)}" +
                  $"&response_type=code&scope={Uri.EscapeDataString(scope)}&access_type=offline&prompt=consent" +
                  $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        listener.Start();

        output.WriteLine("Opening your browser to sign in to Google. If it does not open, visit:");
        output.WriteLine(url);
        TryOpenBrowser(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
        var query = context.Request.QueryString;
        var error = query["error"];
        var code = query["code"];
        var ok = error is null && code is not null && query["state"] == state;

        var page = ok
            ? "<html><body style='font-family:sans-serif'><h2>Meeza Marketing MCP is authorized.</h2><p>You can close this tab.</p></body></html>"
            : "<html><body style='font-family:sans-serif'><h2>Authorization failed.</h2><p>Return to the terminal for details.</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(page);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.OutputStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        context.Response.Close();

        if (error is not null) throw new InvalidOperationException($"Google returned an error: {error}");
        if (!ok) throw new InvalidOperationException("Invalid OAuth response (missing code or state mismatch).");

        using var http = new HttpClient();
        using var resp = await http.PostAsync(CachedGoogleTokenProvider.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code!,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("Token exchange failed: " + Redactor.Redact(body));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Str("refresh_token")
               ?? throw new InvalidOperationException("Google did not return a refresh token. Remove the app's access at https://myaccount.google.com/permissions and try again.");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
        }
        catch
        {
            // The URL is printed; the user can open it manually.
        }
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
