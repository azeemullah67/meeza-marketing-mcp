using System.Reflection;
using System.Text;
using System.Text.Json;
using Meeza.Marketing.GoogleAds;
using Meeza.Marketing.GoogleAds.Models;
using Meeza.Marketing.Meta;
using Meeza.Marketing.Meta.Models;
using Meeza.Marketing.PlayConsole;
using Meeza.Marketing.PlayConsole.Models;
using Meeza.Marketing.Shared.Models;
using Meeza.MarketingMcp.Tools;
using ModelContextProtocol.Server;

namespace Meeza.Marketing.Tests;

public class MetaMappingTests
{
    private static readonly DateRange Range = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Insight_row_with_installs_is_normalized()
    {
        var row = Parse("""
            {"account_id":"111","account_currency":"USD","campaign_id":"9","campaign_name":"PK - Install",
             "spend":"120.50","impressions":"50000","reach":"30000","clicks":"1000","country":"PK",
             "actions":[{"action_type":"link_click","value":"1000"},{"action_type":"mobile_app_install","value":"241"}],
             "date_start":"2026-10-01","date_stop":"2026-10-31"}
            """);
        var p = MetaMapping.ToPerformance(row, Range, "Asia/Karachi");

        Assert.Equal("act_111", p.AccountId);
        Assert.Equal(120.50m, p.Spend);
        Assert.Equal(241m, p.Installs);
        Assert.Equal("meta_attributed:mobile_app_install", p.InstallSource);
        Assert.Equal(0.5m, p.Cpi);
        Assert.Equal(2m, p.Ctr);
        Assert.Equal(2.41m, p.Cpm);
        Assert.Equal("PK", p.Country);
        Assert.Null(p.Notes);
    }

    [Fact]
    public void Missing_install_action_is_null_not_zero()
    {
        var p = MetaMapping.ToPerformance(Parse("""{"account_id":"1","account_currency":"EUR","spend":"10","impressions":"100","clicks":"0"}"""), Range, null);
        Assert.Null(p.Installs);
        Assert.Null(p.Cpi);
        Assert.Null(p.Cpc);
        Assert.NotNull(p.Notes);
    }

    [Theory]
    [InlineData("USD", "2500", 25.00)]
    [InlineData("JPY", "2500", 2500)]
    public void Budgets_respect_currency_offsets(string currency, string minor, decimal expected) =>
        Assert.Equal(expected, MetaMapping.Budget(Parse($$"""{"daily_budget":"{{minor}}"}"""), "daily_budget", currency));

    [Theory]
    [InlineData("123", "act_123")]
    [InlineData(" act_456 ", "act_456")]
    public void Account_ids_are_normalized(string input, string expected) => Assert.Equal(expected, MetaAdsOptions.NormalizeAccountId(input));

    [Fact]
    public void Invalid_account_ids_are_rejected() => Assert.Throws<ArgumentException>(() => MetaAdsOptions.NormalizeAccountId("act_12/../me"));

    [Fact]
    public void App_secret_proof_is_hmac_sha256_hex() =>
        Assert.Equal(
            Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData("secret"u8.ToArray(), "token"u8.ToArray())),
            MetaAdsClient.AppSecretProof("token", "secret"));
}

public class GoogleAdsMappingTests
{
    private static readonly DateRange Range = new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
    private static readonly AdAccount Account = new("google_ads", "1234567890", "Game", "USD", "America/New_York", "ENABLED");

    [Fact]
    public void Micros_and_int64_strings_are_parsed()
    {
        var row = JsonDocument.Parse("""
            {"campaign":{"id":"77","name":"UAC PH","status":"ENABLED","advertisingChannelType":"MULTI_CHANNEL","advertisingChannelSubType":"APP_CAMPAIGN"},
             "metrics":{"costMicros":"45120000","impressions":"20000","clicks":"400","conversions":120.0}}
            """).RootElement;
        var p = GoogleAdsMapping.ToPerformance(row, Account, Range, null, 90m);

        Assert.Equal(45.12m, p.Spend);
        Assert.Equal(20000, p.Impressions);
        Assert.Equal(90m, p.Installs);
        Assert.Equal(0.5013m, p.Cpi);
        Assert.Equal("MULTI_CHANNEL/APP_CAMPAIGN", p.Objective);
        Assert.Equal("America/New_York", p.TimeZone);
    }

    [Fact]
    public void Installs_unavailable_without_install_tracking()
    {
        var row = JsonDocument.Parse("""{"campaign":{"id":"1"},"metrics":{"costMicros":"1000000"}}""").RootElement;
        var p = GoogleAdsMapping.ToPerformance(row, Account, Range, null, null);
        Assert.Null(p.Installs);
        Assert.Null(p.Cpi);
        Assert.NotNull(p.Notes);
    }

    [Theory]
    [InlineData("123-456-7890", "1234567890")]
    [InlineData("customers/1234567890", "1234567890")]
    public void Customer_ids_are_normalized(string input, string expected) => Assert.Equal(expected, GoogleAdsOptions.NormalizeCustomerId(input));

    [Fact]
    public void Gaql_injection_through_customer_id_is_rejected() =>
        Assert.Throws<ArgumentException>(() => GoogleAdsOptions.NormalizeCustomerId("123' OR '1'='1"));

    [Fact]
    public void Streamed_error_payload_is_described()
    {
        var body = """
            [{"error":{"code":403,"message":"The caller does not have permission","status":"PERMISSION_DENIED",
              "details":[{"@type":"type.googleapis.com/google.ads.googleads.v25.errors.GoogleAdsFailure",
                "errors":[{"errorCode":{"authorizationError":"USER_PERMISSION_DENIED"},"message":"User doesn't have permission to access customer."}],
                "requestId":"abc123"}]}}]
            """;
        var text = GoogleAdsClient.DescribeError(403, body);
        Assert.Contains("PERMISSION_DENIED", text);
        Assert.Contains("authorizationError=", text);
        Assert.Contains("request-id abc123", text);
        Assert.Contains("Read only user", text);
    }

    [Fact]
    public void Date_queries_only_contain_validated_dates()
    {
        var q = GoogleAdsQueries.CampaignPerformance(Range, daily: false);
        Assert.Contains("BETWEEN '2026-10-01' AND '2026-10-31'", q);
        Assert.Contains("geographic_view.location_type = 'LOCATION_OF_PRESENCE'", GoogleAdsQueries.CountryPerformance(Range));
    }
}

public class PlayReportsTests
{
    private const string InstallsCsv =
        "Date,Package Name,Country,Daily Device Installs,Daily User Installs,Total User Installs,Active Device Installs\n" +
        "2026-10-01,com.example.game,PK,10,8,1000,500\n" +
        "2026-10-02,com.example.game,PK,12,9,1009,505\n" +
        "2026-10-02,com.example.game,\"P,H\",5,4,200,100\n" +
        "2026-11-01,com.example.game,PK,99,99,2000,600\n";

    [Fact]
    public void Csv_parser_handles_quotes_and_commas()
    {
        var rows = PlayReports.ParseCsv("a,\"b,c\",\"d \"\"q\"\"\"\r\n1,2,3\r\n");
        Assert.Equal(["a", "b,c", "d \"q\""], rows[0]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Utf16_exports_are_decoded()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Date,Package Name\n")).ToArray();
        Assert.StartsWith("Date,Package Name", PlayConsoleClient.DecodeReport(bytes));
        var noBom = Encoding.Unicode.GetBytes("Date,X\n");
        Assert.StartsWith("Date,X", PlayConsoleClient.DecodeReport(noBom));
    }

    [Fact]
    public void Install_summary_sums_daily_columns_and_keeps_latest_totals()
    {
        var range = new DateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var json = JsonSerializer.SerializeToElement(PlayReports.SummarizeInstalls(PlayReports.ToRecords(InstallsCsv), range, "Country"));
        var pk = json.EnumerateArray().Single(e => e.GetProperty("group").GetString() == "PK");

        Assert.Equal(17m, pk.GetProperty("summedOverRange").GetProperty("Daily User Installs").GetDecimal());
        Assert.Equal(1009m, pk.GetProperty("latestValues").GetProperty("Total User Installs").GetDecimal());
        Assert.Equal("Daily User Installs", pk.GetProperty("primaryInstalls").GetProperty("Column").GetString());
        Assert.Equal("Pakistan", pk.GetProperty("countryName").GetString());
        Assert.Equal(2, json.GetArrayLength());
    }

    [Fact]
    public void Months_cover_the_range() =>
        Assert.Equal(["202610", "202611", "202612"], PlayReports.Months(new DateRange(new DateOnly(2026, 10, 30), new DateOnly(2026, 12, 2))));

    [Fact]
    public void Vitals_rates_are_weighted_by_users()
    {
        var rows = new[]
        {
            Vitals("2026-10-01", 0.01m, 1000),
            Vitals("2026-10-02", 0.03m, 3000),
        };
        var summary = JsonSerializer.SerializeToElement(PlayReports.SummarizeVitals(rows, ["userPerceivedCrashRate"]));
        // (0.01*1000 + 0.03*3000) / 4000 = 0.025 => 2.5 %
        Assert.Equal(2.5m, summary[0].GetProperty("rates").GetProperty("userPerceivedCrashRatePercent").GetDecimal());
    }

    [Fact]
    public void Reporting_api_row_is_parsed()
    {
        var row = JsonDocument.Parse("""
            {"startTime":{"year":2026,"month":10,"day":3,"timeZone":{"id":"America/Los_Angeles"}},
             "dimensions":[{"dimension":"versionCode","int64Value":"42"}],
             "metrics":[{"metric":"crashRate","decimalValue":{"value":"0.0123"}},{"metric":"distinctUsers","decimalValue":{"value":"1500"}}]}
            """).RootElement;
        var v = PlayReports.ToVitalsRow(row);
        Assert.Equal(new DateOnly(2026, 10, 3), v.Date);
        Assert.Equal("42", v.Dimensions["versionCode"]);
        Assert.Equal(0.0123m, v.Metrics["crashRate"]);
    }

    [Theory]
    [InlineData("com.example.game", true)]
    [InlineData("com.example/../x", false)]
    [InlineData("game", false)]
    public void Package_names_are_validated(string name, bool valid)
    {
        if (valid) Assert.Equal(name, PlayConsoleOptions.ValidatePackageName(name));
        else Assert.Throws<ArgumentException>(() => PlayConsoleOptions.ValidatePackageName(name));
    }

    private static PlayReports.VitalsRow Vitals(string date, decimal rate, decimal users) =>
        new(DateOnly.Parse(date), [], new() { ["userPerceivedCrashRate"] = rate, ["distinctUsers"] = users });
}

public class ReadOnlyGuaranteeTests
{
    public static IEnumerable<object[]> ToolMethods() =>
        new[] { typeof(UnifiedReportingTools), typeof(DashboardTools), typeof(BenchmarkTools), typeof(InsightTools), typeof(GrowthTools), typeof(Meeza.Marketing.Analytics.AnalyticsTools), typeof(Meeza.Marketing.Adjust.AdjustTools), typeof(MetaAdsTools), typeof(GoogleAdsTools), typeof(PlayConsoleTools) }
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => new object[] { $"{m.DeclaringType!.Name}.{m.Name}" });

    [Theory]
    [MemberData(nameof(ToolMethods))]
    public void Every_tool_is_annotated_read_only(string method)
    {
        var parts = method.Split('.');
        var type = new[] { typeof(UnifiedReportingTools), typeof(DashboardTools), typeof(BenchmarkTools), typeof(InsightTools), typeof(GrowthTools), typeof(Meeza.Marketing.Analytics.AnalyticsTools), typeof(Meeza.Marketing.Adjust.AdjustTools), typeof(MetaAdsTools), typeof(GoogleAdsTools), typeof(PlayConsoleTools) }.Single(t => t.Name == parts[0]);
        var attr = type.GetMethod(parts[1])!.GetCustomAttribute<McpServerToolAttribute>()!;
        Assert.True(attr.ReadOnly);
        Assert.False(attr.Destructive);
    }

    [Fact]
    public void No_tool_name_suggests_a_write_operation()
    {
        var names = ToolMethods().Select(m => (string)m[0]).ToArray();
        Assert.Equal(44, names.Length);   // 44 since adjust_list_apps / adjust_get_performance / adjust_get_retention (2026-11-10)
        var writeVerbs = new[] { "Create", "Update", "Delete", "Pause", "Set", "Mutate", "Reply", "Edit" };
        // OpenDashboard only opens a local read-only view.
        Assert.DoesNotContain(names, n => writeVerbs.Any(v => n.Split('.')[1].StartsWith(v)));
    }
}
