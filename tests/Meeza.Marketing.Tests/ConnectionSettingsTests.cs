using Meeza.Marketing.Shared.Security;
using Meeza.MarketingMcp.Services;

namespace Meeza.Marketing.Tests;

public class ConnectionSettingsTests
{
    [Fact]
    public void Meta_account_ids_are_normalised() =>
        Assert.Equal("act_123,act_456", ConnectionSettings.Normalize(SettingKeys.MetaAdAccountIds, "123, act_456"));

    [Fact]
    public void Google_customer_ids_lose_dashes() =>
        Assert.Equal("1234567890", ConnectionSettings.Normalize(SettingKeys.GoogleAdsLoginCustomerId, "123-456-7890"));

    [Fact]
    public void Bucket_accepts_gs_uri() =>
        Assert.Equal("pubsite_prod_rev_0123", ConnectionSettings.Normalize(SettingKeys.PlayReportsBucket, "gs://pubsite_prod_rev_0123/stats"));

    [Theory]
    [InlineData(SettingKeys.MetaAdAccountIds, "not-an-id")]
    [InlineData(SettingKeys.GoogleAdsClientId, "abc")]
    [InlineData(SettingKeys.PlayReportsBucket, "my-bucket")]
    [InlineData(SettingKeys.PlayPackageNames, "not a package")]
    [InlineData(SettingKeys.MetaAccessToken, "two words")]
    [InlineData(SettingKeys.PlayServiceAccountJson, "{not json")]
    [InlineData(SettingKeys.PlayServiceAccountJson, "{\"type\":\"service_account\"}")]
    public void Invalid_values_are_rejected(string key, string value) =>
        Assert.ThrowsAny<ArgumentException>(() => ConnectionSettings.Normalize(key, value));

    [Fact]
    public void Valid_service_account_json_is_kept()
    {
        const string json = "{\"type\":\"service_account\",\"client_email\":\"x@y.iam.gserviceaccount.com\",\"private_key\":\"k\"}";
        Assert.Equal(json, ConnectionSettings.Normalize(SettingKeys.PlayServiceAccountJson, json));
    }

    [Fact]
    public void Every_field_is_a_known_setting()
    {
        foreach (var f in ConnectionSettings.Fields.Values.SelectMany(x => x))
        {
            Assert.Contains(f.Key, SettingKeys.All);
            Assert.Equal(SettingKeys.IsSecret(f.Key), f.Secret);
        }
    }
}
