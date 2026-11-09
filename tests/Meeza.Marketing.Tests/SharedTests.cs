using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.Shared.Models;
using Meeza.Marketing.Shared.Security;
using Meeza.Marketing.Shared.Utilities;
using Microsoft.Extensions.Configuration;

namespace Meeza.Marketing.Tests;

public class DateRangeTests
{
    private static readonly DateOnly Today = new(2026, 11, 4);

    [Theory]
    [InlineData("last_7_days", "2026-10-28", "2026-11-03")]
    [InlineData("last_30_days", "2026-10-05", "2026-11-03")]
    [InlineData("yesterday", "2026-11-03", "2026-11-03")]
    [InlineData("today", "2026-11-04", "2026-11-04")]
    [InlineData("this_month", "2026-11-01", "2026-11-04")]
    [InlineData("last_month", "2026-10-01", "2026-10-31")]
    [InlineData("last-14-days", "2026-10-21", "2026-11-03")]
    [InlineData(null, "2026-10-05", "2026-11-03")]
    public void Presets_resolve_to_expected_dates(string? preset, string start, string end)
    {
        var r = DateRange.Resolve(preset, null, null, Today);
        Assert.Equal(start, r.StartIso);
        Assert.Equal(end, r.EndIso);
    }

    [Fact]
    public void Explicit_dates_override_preset()
    {
        var r = DateRange.Resolve("last_7_days", "2026-01-01", "2026-01-31", Today);
        Assert.Equal(31, r.Days);
    }

    [Theory]
    [InlineData("2026-02-01", "2026-01-01")]
    [InlineData("2026-13-01", "2026-12-01")]
    [InlineData("2026-01-01", null)]
    public void Invalid_explicit_dates_throw(string? start, string? end) =>
        Assert.Throws<ArgumentException>(() => DateRange.Resolve(null, start, end, Today));

    [Fact]
    public void Unknown_preset_throws() => Assert.Throws<ArgumentException>(() => DateRange.Resolve("forever", null, null, Today));
}

public class MetricMathTests
{
    [Fact]
    public void Ratios_are_null_when_denominator_missing_or_zero()
    {
        Assert.Null(MetricMath.CostPer(100m, (decimal?)0));
        Assert.Null(MetricMath.CostPer(100m, (decimal?)null));
        Assert.Null(MetricMath.Ctr(10, 0));
        Assert.Null(MetricMath.Cpm(null, 1000));
    }

    [Fact]
    public void Ratios_are_computed_consistently()
    {
        Assert.Equal(2.5m, MetricMath.Ctr(25, 1000));
        Assert.Equal(5m, MetricMath.Cpm(5m, 1000));
        Assert.Equal(0.5m, MetricMath.CostPer(50m, (decimal?)100));
    }

    [Fact]
    public void Sum_keeps_null_only_when_both_null()
    {
        Assert.Null(MetricMath.Sum((decimal?)null, null));
        Assert.Equal(3m, MetricMath.Sum(3m, null));
    }
}

public class AggregationTests
{
    private static CampaignPerformance Row(string platform, string currency, decimal spend, decimal? installs, string? country = null, string id = "1") =>
        new CampaignPerformance
        {
            Platform = platform, AccountId = "a", CampaignId = id, Currency = currency, Spend = spend,
            Impressions = 1000, Clicks = 10, Installs = installs, Country = country,
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 1, 31),
        }.WithDerivedMetrics();

    [Fact]
    public void Totals_never_mix_currencies()
    {
        var totals = Aggregation.ByPlatform([Row("meta", "USD", 10, 5), Row("meta", "PKR", 1000, 5, id: "2")]);
        Assert.Equal(2, totals.Count);
        Assert.Contains(totals, t => t is { Currency: "USD", Spend: 10m, Cpi: 2m });
        Assert.Contains(totals, t => t is { Currency: "PKR", Spend: 1000m, Cpi: 200m });
    }

    [Fact]
    public void Installs_stay_null_when_no_row_reports_them()
    {
        var totals = Aggregation.ByPlatform([Row("google_ads", "USD", 10, null), Row("google_ads", "USD", 5, null, id: "2")]);
        Assert.Null(totals.Single().Installs);
        Assert.Null(totals.Single().Cpi);
        Assert.Equal(15m, totals.Single().Spend);
    }

    [Fact]
    public void Country_names_and_codes_are_accepted()
    {
        var set = Aggregation.ParseCountries("Pakistan, PH, United States, uk");
        Assert.NotNull(set);
        Assert.Equal(["GB", "PH", "PK", "US"], set!.OrderBy(x => x).ToArray());
        Assert.Throws<ArgumentException>(() => Aggregation.ParseCountries("Atlantis"));
    }

    [Fact]
    public void Cpi_report_flags_spend_without_installs()
    {
        var report = JsonSerializer.SerializeToElement(Aggregation.CpiReport(
            [Row("meta", "USD", 50, 10), Row("meta", "USD", 30, null, id: "2"), Row("meta", "USD", 20, 0, id: "3")]), Json.Output);
        Assert.Equal(1, report.GetProperty("campaignsWithInstalls").GetArrayLength());
        Assert.Equal(2, report.GetProperty("spendWithoutInstalls").GetArrayLength());
        Assert.Equal("2", report.GetProperty("spendWithoutInstalls")[0].GetProperty("campaignId").GetString());
    }

    [Fact]
    public void Merging_countries_drops_reach_and_recomputes_ratios()
    {
        var merged = Aggregation.MergeCountries([Row("meta", "USD", 10, 2, "PK") with { Reach = 500 }, Row("meta", "USD", 30, 2, "PH")]).Single();
        Assert.Null(merged.Country);
        Assert.Null(merged.Reach);
        Assert.Equal(40m, merged.Spend);
        Assert.Equal(10m, merged.Cpi);
    }
}

public class RedactorTests
{
    [Theory]
    [InlineData("https://graph.facebook.com/v25.0/me?access_token=EAABsecret123&x=1", "access_token=[REDACTED]")]
    [InlineData("Authorization: Bearer ya29.a0AfH6SMBabc", "Bearer [REDACTED]")]
    [InlineData("{\"refresh_token\": \"1//0gabcdefghijklmnopqrstuvwxyz\"}", "\"refresh_token\": \"[REDACTED]\"")]
    public void Secrets_are_removed(string input, string expected)
    {
        var output = Redactor.Redact(input);
        Assert.Contains(expected, output);
        Assert.DoesNotContain("secret123", output);
        Assert.DoesNotContain("abcdefghijklmnop", output);
    }

    [Fact]
    public void Error_codes_are_not_redacted() => Assert.Contains("error_code=17", Redactor.Redact("error_code=17"));

    [Fact]
    public void Private_keys_are_removed() =>
        Assert.Equal("[REDACTED PRIVATE KEY]", Redactor.Redact("-----BEGIN PRIVATE KEY-----\nMIIabc\n-----END PRIVATE KEY-----"));

    [Fact]
    public void Mask_shows_only_edges() => Assert.Equal("EAAB…WXYZ (20 chars)", Redactor.Mask("EAABcdefghijklmnWXYZ"));
}

public class SettingsTests
{
    [Fact]
    public void Lists_split_on_common_separators() =>
        Assert.Equal(["a", "b", "c"], SettingsResolver.SplitList(" a, b;c  a "));

    [Fact]
    public void Secrets_are_never_read_from_appsettings()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Settings:META_ACCESS_TOKEN"] = "from-appsettings",
                ["Settings:PLAY_PACKAGE_NAMES"] = "com.example.game",
            }).Build();
        var resolver = new SettingsResolver(config, new LocalSecretStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json")));
        Assert.Null(resolver.Get(SettingKeys.MetaAccessToken));
        Assert.Equal("com.example.game", resolver.Get(SettingKeys.PlayPackageNames));
    }

    [Fact]
    public void Secret_store_round_trips_encrypted_values()
    {
        if (!LocalSecretStore.IsSupported) return;
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new LocalSecretStore(path);
            store.Set("META_ACCESS_TOKEN", "EAAsupersecretvalue");
            Assert.Equal("EAAsupersecretvalue", store.Get("META_ACCESS_TOKEN"));
            Assert.DoesNotContain("supersecret", File.ReadAllText(path));
            Assert.True(store.Remove("META_ACCESS_TOKEN"));
            Assert.Null(store.Get("META_ACCESS_TOKEN"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class ServiceAccountTests
{
    [Fact]
    public void Jwt_assertion_is_signed_with_the_service_account_key()
    {
        using var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new
        {
            type = "service_account",
            client_email = "reporter@project.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
        });
        var provider = new GoogleServiceAccountTokenProvider(new HttpClient(), json, ["scope-a", "scope-b"], "google_play");
        var jwt = provider.CreateAssertion(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)).Split('.');

        Assert.Equal(3, jwt.Length);
        var claims = JsonDocument.Parse(FromB64Url(jwt[1])).RootElement;
        Assert.Equal("reporter@project.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("scope-a scope-b", claims.GetProperty("scope").GetString());
        Assert.Equal(1_800_003_600, claims.GetProperty("exp").GetInt64());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), FromB64Url(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Non_service_account_json_is_rejected() =>
        Assert.Throws<ArgumentException>(() => new GoogleServiceAccountTokenProvider(new HttpClient(), "{\"type\":\"authorized_user\"}", [], "google_play"));

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
