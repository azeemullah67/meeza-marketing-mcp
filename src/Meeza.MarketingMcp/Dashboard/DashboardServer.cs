using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Meeza.MarketingMcp.Dashboard;

/// <summary>
/// Local live dashboard. Binds to 127.0.0.1 only, rejects foreign Host headers (DNS rebinding),
/// and requires a per-user access key (stored in the user's profile) before serving any data.
/// Everything it serves is read-only.
/// </summary>
public sealed class DashboardServer(DashboardData data, Services.ConnectionSettings connections, IServiceProvider services, ILogger<DashboardServer> logger) : IAsyncDisposable
{
    public const int DefaultPort = 5057;
    private const string CookieName = "meeza_dashboard";
    private const string CsrfHeader = "X-Meeza-Csrf";
    private const long MaxBodyBytes = 128 * 1024;

    /// <summary>Per-process anti-forgery token; the page reads it from GET /api/settings (same-origin only).</summary>
    private static readonly string CsrfToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private WebApplication? _app;

    public static int Port =>
        int.TryParse(Environment.GetEnvironmentVariable("MEEZA_DASHBOARD_PORT"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    /// <summary>False when MEEZA_DASHBOARD is set to off/false/0.</summary>
    public static bool EnabledWithMcp =>
        Environment.GetEnvironmentVariable("MEEZA_DASHBOARD")?.Trim().ToLowerInvariant() is not ("off" or "false" or "0" or "no");

    public static string Url => $"http://127.0.0.1:{Port}/?key={AccessKey.Value}";

    public bool IsRunning => _app is not null;

    /// <summary>True if something (normally another instance of this server) already listens on the dashboard port.</summary>
    public static bool PortInUse()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(IPAddress.Loopback, Port).Wait(TimeSpan.FromMilliseconds(300)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        if (_app is not null) return true;
        if (PortInUse())
        {
            logger.LogInformation("Dashboard port {Port} is already in use (probably another Meeza instance); not starting a second dashboard.", Port);
            return false;
        }

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        // stdout belongs to the MCP protocol: route all web logging to stderr, warnings only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, Port); k.Limits.MaxRequestBodySize = MaxBodyBytes; });
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton(connections);

        var app = builder.Build();
        app.Use(Guard);
        MapEndpoints(app);
        PlatformApi.Map(app, services);

        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
        {
            logger.LogWarning("Dashboard could not start on port {Port}: {Error}. Set MEEZA_DASHBOARD_PORT to use another port.", Port, ex.Message);
            await app.DisposeAsync().ConfigureAwait(false);
            return false;
        }
        _app = app;
        InstanceIdentity.Register(Port);
        logger.LogInformation("Live dashboard: http://127.0.0.1:{Port}/ (open it with the open_dashboard tool or `meeza-marketing-mcp dashboard`).", Port);
        return true;
    }

    /// <summary>True when the listener on the dashboard port proves it is this user's Meeza dashboard (not merely "something listens").</summary>
    public static Task<bool> IsOurInstanceAsync(CancellationToken ct) => InstanceIdentity.IsOurInstanceAsync(Port, ct);

    public static string PortBusyMessage =>
        $"Port {Port} is in use by a program that is not this user's Meeza dashboard, so no dashboard link was issued. " +
        "Close that program or set MEEZA_DASHBOARD_PORT to a free port.";

    public async Task WaitForShutdownAsync(CancellationToken ct)
    {
        if (_app is not null) await _app.WaitForShutdownAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        InstanceIdentity.Unregister();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }

    public static bool TryOpenBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
            else Process.Start("xdg-open", url);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- security

    private static async Task Guard(HttpContext ctx, Func<Task> next)
    {
        // DNS-rebinding protection: only accept our own loopback host names.
        var host = ctx.Request.Host.Host;
        if (host is not ("127.0.0.1" or "localhost"))
        {
            ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
            return;
        }
        InstanceIdentity.EnsureRegistered();
        // Identity challenge for other Meeza processes (see InstanceIdentity). Unauthenticated by design: the answer is an
        // HMAC over the caller's nonce and reveals neither the instance token nor the access key.
        if (HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Path == InstanceIdentity.RoutePath)
        {
            var nonce = ctx.Request.Query["nonce"].ToString();
            var proof = nonce.Length is > 0 and <= 64 && nonce.All(Uri.IsHexDigit) ? InstanceIdentity.Answer(nonce) : null;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            if (proof is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new { port = Port, proof });
            return;
        }
        // Ad data is read-only. The only writes are local configuration (connection settings, projects, targets) and
        // starting syncs / AI requests. They need: POST to /api/settings or /api/v2, a same-origin Origin header, JSON,
        // and the anti-forgery token.
        if (!HttpMethods.IsGet(ctx.Request.Method))
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            var okOrigin = origin == $"http://127.0.0.1:{Port}" || origin == $"http://localhost:{Port}";
            var okToken = ctx.Request.Headers.TryGetValue(CsrfHeader, out var token) &&
                          CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token.ToString()), Encoding.UTF8.GetBytes(CsrfToken));
            if (!HttpMethods.IsPost(ctx.Request.Method) || !(ctx.Request.Path.StartsWithSegments("/api/settings") || ctx.Request.Path.StartsWithSegments("/api/v2")))
            {
                ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }
            if (!okOrigin || !okToken || ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["X-Frame-Options"] = "DENY";
        ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data: https:; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

        // Key in the URL -> set an HttpOnly cookie and redirect to a clean URL.
        if (ctx.Request.Query.TryGetValue("key", out var key))
        {
            if (!AccessKey.Matches(key.ToString()))
            {
                await Deny(ctx);
                return;
            }
            ctx.Response.Cookies.Append(CookieName, AccessKey.Value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromDays(30),
            });
            ctx.Response.Redirect("/");
            return;
        }

        if (!ctx.Request.Cookies.TryGetValue(CookieName, out var cookie) || !AccessKey.Matches(cookie))
        {
            await Deny(ctx);
            return;
        }
        await next();
    }

    private static Task Deny(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync(
            "<!doctype html><meta charset=utf-8><title>Meeza Marketing</title><body style='font-family:system-ui;padding:40px;max-width:560px'>" +
            "<h2>Dashboard link required</h2><p>Open the dashboard from Claude (ask <i>“open the marketing dashboard”</i>) " +
            "or run <code>meeza-marketing-mcp dashboard</code> in a terminal. The link contains your private access key.</p></body>");
    }

    // ---------------------------------------------------------------- endpoints

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(AppHtml.Value, "text/html; charset=utf-8"));
        // The original single-page dashboard was retired; old bookmarks land on the app.
        app.MapGet("/classic", () => Results.Redirect("/"));

        app.MapGet("/api/overview", (HttpRequest req, DashboardData d, CancellationToken ct) =>
            Run(req, (range, refresh) => d.OverviewAsync(range, ReportLevels.Parse(req.Query["level"]), Platforms(req), refresh, ct)));

        app.MapGet("/api/creatives", (HttpRequest req, DashboardData d, CancellationToken ct) =>
            Run(req, (range, refresh) => d.CreativesAsync(range, Platforms(req), refresh, ct)));

        app.MapGet("/api/countries", (HttpRequest req, DashboardData d, CancellationToken ct) =>
            Run(req, (range, refresh) => d.CountriesAsync(range, Platforms(req), refresh, ct)));

        app.MapGet("/api/players", (HttpRequest req, DashboardData d, CancellationToken ct) =>
            Run(req, (range, refresh) => d.PlayersAsync(range, refresh, ct)));

        app.MapGet("/api/play", (HttpRequest req, DashboardData d, CancellationToken ct) =>
            Run(req, (range, refresh) => d.PlayAsync(range, refresh, ct)));

        // ---- connection settings (local only; secrets are write-only from the browser's point of view)
        app.MapGet("/api/settings", (Services.ConnectionSettings c) =>
            Results.Text(Json.Serialize(new { csrf = CsrfToken, settings = c.Describe() }), "application/json; charset=utf-8"));

        app.MapPost("/api/settings/{platform}", async (string platform, HttpRequest req, Services.ConnectionSettings c) =>
            await Write(async () =>
            {
                var body = await JsonSerializer.DeserializeAsync<SaveRequest>(req.Body, JsonOptions).ConfigureAwait(false) ?? new SaveRequest();
                c.Save(platform, body.Values ?? [], body.Remove ?? []);
                return new { ok = true, settings = c.Describe() };
            }));

        app.MapPost("/api/settings/{platform}/test", async (string platform, Services.ConnectionSettings c, CancellationToken ct) =>
            await Write(() => c.TestAsync(platform, ct)));

        app.MapPost("/api/settings/google_ads/signin", async (Services.ConnectionSettings c, CancellationToken ct) =>
            await Write(() => c.SignInGoogleAdsAsync(ct)));

        app.MapPost("/api/settings/mode", async (HttpRequest req, DashboardData d) =>
            await Write(async () =>
            {
                var body = await JsonSerializer.DeserializeAsync<ModeRequest>(req.Body, JsonOptions).ConfigureAwait(false);
                d.Demo = body?.Demo ?? d.Demo;
                d.ClearCache();
                return new { ok = true, demo = d.Demo };
            }));

        // Static, sourced industry reference data (no platform calls).
        app.MapGet("/api/benchmarks", () =>
            Results.Text(Benchmarks.IndustryBenchmarks.Serialize(Benchmarks.IndustryBenchmarks.All), "application/json; charset=utf-8"));
    }

    private sealed class SaveRequest
    {
        public Dictionary<string, string?>? Values { get; set; }
        public List<string>? Remove { get; set; }
    }

    private sealed class ModeRequest
    {
        public bool? Demo { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, MaxDepth = 8 };

    private static async Task<IResult> Write(Func<Task<object>> body)
    {
        try
        {
            return Results.Text(Json.Serialize(await body().ConfigureAwait(false)), "application/json; charset=utf-8");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or JsonException or FormatException or PlatformNotSupportedException or OperationCanceledException)
        {
            var message = ex is OperationCanceledException ? "Timed out waiting for sign-in. Try again." : Redactor.Redact(ex.Message);
            return Results.Text(Json.Serialize(new { ok = false, message }), "application/json; charset=utf-8", statusCode: 400);
        }
    }

    private static string? Platforms(HttpRequest req) => req.Query["platforms"] is { Count: > 0 } p && p.ToString() != "all" ? p.ToString() : null;

    private static async Task<IResult> Run(HttpRequest req, Func<DateRange, bool, Task<ToolResponse>> body)
    {
        try
        {
            var range = DateRange.Resolve(req.Query["preset"], req.Query["start"], req.Query["end"]);
            var refresh = req.Query["refresh"] == "1";
            var response = await body(range, refresh).ConfigureAwait(false);
            return Results.Text(Json.Serialize(response), "application/json; charset=utf-8");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = new ToolResponse { Tool = "dashboard", Ok = false };
            error.Errors.Add(new PlatformError("server", ToolRunner.Describe(ex)));
            return Results.Text(Json.Serialize(error), "application/json; charset=utf-8", statusCode: 400);
        }
    }

    private static readonly Lazy<string> AppHtml = new(() => Resource("Meeza.MarketingMcp.Dashboard.app.html"));

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"Dashboard page resource {name} is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Per-user dashboard access key, persisted in the user's profile so bookmarked links keep working.
/// The file is re-checked on every use, so deleting it (or running <c>dashboard rotate-key</c>) revokes every existing
/// link and browser session immediately, in every running instance.
/// </summary>
public static class AccessKey
{
    public static string FilePath => Path.Combine(PrivateStorage.DefaultDirectory, "dashboard.key");

    private static readonly object Sync = new();
    private static string? _key;
    private static DateTime? _stamp; // last-write time of the file the cached key came from; null = session-only key

    public static string Value
    {
        get { lock (Sync) return Current(); }
    }

    public static bool Matches(string? candidate) =>
        candidate is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(Value));

    /// <summary>Replaces the key. Every old link and cookie fails on its next request, in this and every other running instance.</summary>
    public static string Rotate()
    {
        lock (Sync)
        {
            try { File.Delete(FilePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _key = null;
            _stamp = null;
            return Current();
        }
    }

    private static string Current()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var stamp = File.GetLastWriteTimeUtc(FilePath);
                if (_key is not null && _stamp == stamp) return _key;
                var existing = File.ReadAllText(FilePath).Trim();
                if (existing.Length >= 32)
                {
                    _key = existing;
                    _stamp = stamp;
                    return existing;
                }
            }
            else if (_key is not null && _stamp is null)
            {
                return _key; // profile not writable: session-only key
            }

            // No usable file (first run, or the user deleted it to revoke access): create a fresh key.
            var key = NewKey();
            PrivateStorage.EnsurePrivateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, key);
            PrivateStorage.MakeFilePrivate(FilePath);
            _key = key;
            _stamp = File.GetLastWriteTimeUtc(FilePath);
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _key ??= NewKey(); // session-only key
            _stamp = null;
            return _key;
        }
    }

    private static string NewKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
}

/// <summary>
/// Proves that whatever listens on the dashboard port is this user's Meeza dashboard before a link carrying the access
/// key is handed out. The running instance writes a random token to a user-private file; a client challenges the
/// listener with a nonce and expects HMAC(token, nonce). A foreign process squatting on the port cannot answer.
/// </summary>
public static class InstanceIdentity
{
    public const string RoutePath = "/__meeza/instance";

    /// <summary>One file per port, so a second instance on another port (tests, a demo) never overwrites the live one's record.</summary>
    public static string FilePathFor(int port) => Path.Combine(PrivateStorage.DefaultDirectory, $"dashboard-{port}.instance");

    private static string? _token;
    private static int _port;

    public static void Register(int port)
    {
        _token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _port = port;
        var file = FilePathFor(port);
        try
        {
            PrivateStorage.EnsurePrivateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(new { pid = Environment.ProcessId, port, token = _token, startedAt = DateTimeOffset.UtcNow }));
            PrivateStorage.MakeFilePrivate(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without the file other processes cannot verify us and will refuse to reuse this instance; that is the safe failure.
        }
    }

    /// <summary>Rewrites the record if it disappeared (another process cleaned up, profile tooling, a crash of a sibling instance).</summary>
    public static void EnsureRegistered()
    {
        if (_token is null) return;
        var file = FilePathFor(_port);
        try
        {
            if (File.Exists(file)) return;
            File.WriteAllText(file, JsonSerializer.Serialize(new { pid = Environment.ProcessId, port = _port, token = _token, startedAt = DateTimeOffset.UtcNow }));
            PrivateStorage.MakeFilePrivate(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Unregister()
    {
        var file = FilePathFor(_port);
        try
        {
            if (_token is not null && File.Exists(file) && File.ReadAllText(file).Contains(_token, StringComparison.Ordinal))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        _token = null;
    }

    /// <summary>The server's answer to a challenge, or null when this process is not a registered dashboard.</summary>
    public static string? Answer(string nonce) => _token is null ? null : Proof(_token, nonce);

    public static Task<bool> IsOurInstanceAsync(int port, CancellationToken ct) => IsOurInstanceAsync(port, FilePathFor(port), ct);

    /// <summary>True only when the listener on <paramref name="port"/> proves knowledge of the token in <paramref name="filePath"/>.</summary>
    public static async Task<bool> IsOurInstanceAsync(int port, string filePath, CancellationToken ct)
    {
        string token;
        try
        {
            if (!File.Exists(filePath)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!doc.RootElement.TryGetProperty("port", out var p) || p.GetInt32() != port) return false;
            token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
            if (token.Length < 32) return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return false;
        }

        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync($"http://127.0.0.1:{port}{RoutePath}?nonce={nonce}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var answer = doc.RootElement.TryGetProperty("proof", out var a) ? a.GetString() : null;
            return answer is not null &&
                   CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(answer), Encoding.UTF8.GetBytes(Proof(token, nonce)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return false;
        }
    }

    public static string Proof(string token, string nonce) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(nonce)));
}
