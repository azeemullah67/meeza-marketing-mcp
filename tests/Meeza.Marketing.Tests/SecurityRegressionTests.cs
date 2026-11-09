using System.Net;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Analytics;
using Meeza.Marketing.Meta;
using Meeza.Marketing.Shared.Utilities;
using Meeza.MarketingMcp.Dashboard;
using Meeza.MarketingMcp.Sync;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meeza.Marketing.Tests;

/// <summary>Records every request and answers from a delegate. No network is touched.</summary>
internal sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public HttpClient Client => new(this);
}

// ------------------------------------------------------------------ F1: Meta child objects must belong to the resolved account

public class MetaChildOwnershipTests
{
    private static MetaAdsClient Client(FakeHttp http) =>
        new(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic", AdAccountIds = ["act_11111"] }, NullLogger<MetaAdsClient>.Instance);

    private static FakeHttp Graph(string ownerOfChild) => new(r =>
    {
        var path = r.RequestUri!.AbsolutePath;
        if (path.EndsWith("/88888") || path.EndsWith("/77777"))
            return FakeHttp.Json(ownerOfChild is null ? """{"id":"88888"}""" : $$"""{"id":"88888","account_id":"{{ownerOfChild}}"}""");
        if (path.EndsWith("/adsets") || path.EndsWith("/ads"))
            return FakeHttp.Json("""{"data":[{"id":"1","name":"foreign row","campaign_id":"88888"}]}""");
        return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
    });

    [Fact]
    public async Task Ad_sets_of_a_campaign_from_another_account_are_refused()
    {
        var http = Graph("22222");
        var ex = await Assert.ThrowsAsync<AccountAccessDeniedException>(() => Client(http).GetAdSetsAsync("act_11111", "88888", 100, default));
        Assert.Contains("different ad account", ex.Message);
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/88888/adsets"));
    }

    [Fact]
    public async Task Ads_of_an_ad_set_from_another_account_are_refused()
    {
        var http = Graph("22222");
        await Assert.ThrowsAsync<AccountAccessDeniedException>(() => Client(http).GetAdsAsync("act_11111", "77777", 100, default));
        Assert.DoesNotContain(http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/77777/ads"));
    }

    [Fact]
    public async Task Child_that_does_not_report_an_owner_fails_closed()
    {
        var http = Graph(null!);
        var ex = await Assert.ThrowsAsync<AccountAccessDeniedException>(() => Client(http).GetAdSetsAsync("act_11111", "88888", 100, default));
        Assert.Contains("cannot be checked", ex.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Child_of_the_allowed_account_is_fetched()
    {
        var http = Graph("11111");
        var rows = await Client(http).GetAdSetsAsync("act_11111", "88888", 100, default);
        Assert.Single(rows);
        Assert.Contains(http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/88888/adsets"));
        Assert.All(http.Requests, r => Assert.Equal("graph.facebook.com", r.RequestUri!.Host));
    }
}

// ------------------------------------------------------------------ F3: pagination must never carry the token off-host

public class MetaPaginationTests
{
    [Theory]
    [InlineData("https://graph.facebook.com.attacker.invalid/collect")]
    [InlineData("https://graph.facebook.com@attacker.invalid/collect")]
    [InlineData("http://graph.facebook.com/v25.0/act_1/campaigns?after=x")]
    [InlineData("https://graph.facebook.com:8443/v25.0/act_1/campaigns?after=x")]
    [InlineData("https://attacker.invalid/?u=https://graph.facebook.com")]
    [InlineData("/v25.0/act_1/campaigns?after=x")]
    public async Task Deceptive_next_links_abort_the_request_and_are_never_fetched(string next)
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"data":[{"id":"1"}],"paging":{"next":""" + JsonSerializer.Serialize(next) + "}}"));
        var client = new MetaAdsClient(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic" }, NullLogger<MetaAdsClient>.Instance);

        await Assert.ThrowsAsync<PlatformApiException>(() => client.GetCampaignsAsync("act_1", null, 1000, default));

        Assert.Single(http.Requests); // the first page only
        Assert.All(http.Requests, r => Assert.Equal("graph.facebook.com", r.RequestUri!.Host));
        Assert.False(MetaAdsClient.IsGraphUrl(next));
    }

    [Fact]
    public async Task Genuine_next_link_is_followed_with_the_token()
    {
        var calls = 0;
        var http = new FakeHttp(_ => ++calls == 1
            ? FakeHttp.Json("""{"data":[{"id":"1"}],"paging":{"next":"https://graph.facebook.com/v25.0/act_1/campaigns?after=abc"}}""")
            : FakeHttp.Json("""{"data":[{"id":"2"}]}"""));
        var client = new MetaAdsClient(http.Client, new MetaAdsOptions { AccessToken = "EAAtest-synthetic" }, NullLogger<MetaAdsClient>.Instance);

        var rows = await client.GetCampaignsAsync("act_1", null, 1000, default);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, http.Requests.Count);
        Assert.All(http.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization!.Scheme));
        Assert.True(MetaAdsClient.IsGraphUrl("https://graph.facebook.com/v25.0/x"));
        Assert.True(MetaAdsClient.IsGraphUrl("https://GRAPH.FACEBOOK.COM:443/v25.0/x"));
    }
}

// ------------------------------------------------------------------ F2: GA4 allow-list applies to sync too

public class Ga4AllowListTests
{
    private static (AnalyticsService Service, FakeHttp Http) Service(params string[] allow)
    {
        var http = new FakeHttp(_ => FakeHttp.Json("""{"rows":[]}"""));
        var options = new AnalyticsOptions { ServiceAccountJson = """{"client_email":"x@y.iam.gserviceaccount.com","private_key":"unused"}""", PropertyIds = allow };
        return (new AnalyticsService(new AnalyticsClient(http.Client, options)), http);
    }

    [Fact]
    public async Task Report_for_an_excluded_property_is_refused_before_any_request()
    {
        var (service, http) = Service("111111");
        await Assert.ThrowsAsync<AccountAccessDeniedException>(() => service.RunReportAsync("222222", new { limit = 1 }, default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Sync_describe_refuses_an_excluded_property()
    {
        var (service, http) = Service("111111");
        using var db = new TestDb();
        var syncer = new Ga4Syncer(service, db.Facts);

        await Assert.ThrowsAsync<AccountAccessDeniedException>(() => syncer.DescribeAsync("222222", default));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Authorize_normalizes_and_accepts_allowed_ids()
    {
        var (service, _) = Service("111111");
        Assert.Equal("111111", service.Authorize("properties/111111"));
        Assert.Throws<AccountAccessDeniedException>(() => service.Authorize("333333"));
    }

    [Fact]
    public void Empty_allow_list_means_every_visible_property()
    {
        var (service, _) = Service();
        Assert.Equal("424242", service.Authorize("424242"));
    }
}

// ------------------------------------------------------------------ F4: an occupied port is not a dashboard until it proves it

public class DashboardIdentityTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string InstanceFile(int port, string token)
    {
        var path = Path.Combine(Path.GetTempPath(), $"meeza-instance-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { pid = 1, port, token }));
        return path;
    }

    private static HttpListener Listen(int port, Func<Uri, string?> answer)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); } catch { return; }
                var body = Encoding.UTF8.GetBytes(answer(ctx.Request.Url!) ?? "{}");
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });
        return listener;
    }

    [Fact]
    public async Task A_listener_that_cannot_answer_the_challenge_is_not_trusted()
    {
        var port = FreePort();
        var file = InstanceFile(port, new string('a', 64));
        using var squatter = Listen(port, _ => """{"port":1,"proof":"not-the-right-answer","key":"anything"}""");
        try
        {
            Assert.False(await InstanceIdentity.IsOurInstanceAsync(port, file, default));
        }
        finally { squatter.Stop(); File.Delete(file); }
    }

    [Fact]
    public async Task No_instance_file_means_no_trust_even_if_something_listens()
    {
        var port = FreePort();
        using var squatter = Listen(port, _ => "{}");
        try
        {
            Assert.False(await InstanceIdentity.IsOurInstanceAsync(port, Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"), default));
        }
        finally { squatter.Stop(); }
    }

    [Fact]
    public async Task A_listener_that_knows_the_token_is_trusted()
    {
        var port = FreePort();
        var token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var file = InstanceFile(port, token);
        using var genuine = Listen(port, url =>
        {
            var nonce = System.Web.HttpUtility.ParseQueryString(url.Query)["nonce"]!;
            return JsonSerializer.Serialize(new { port, proof = InstanceIdentity.Proof(token, nonce) });
        });
        try
        {
            Assert.True(await InstanceIdentity.IsOurInstanceAsync(port, file, default));
        }
        finally { genuine.Stop(); File.Delete(file); }
    }

    [Fact]
    public async Task Instance_file_for_a_different_port_is_ignored()
    {
        var port = FreePort();
        var file = InstanceFile(port + 1, new string('b', 64));
        try
        {
            Assert.False(await InstanceIdentity.IsOurInstanceAsync(port, file, default));
        }
        finally { File.Delete(file); }
    }
}
