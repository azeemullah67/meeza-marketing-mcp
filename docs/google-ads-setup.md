# Google Ads setup

The server calls the [Google Ads API](https://developers.google.com/google-ads/api/) over REST. It only uses `GoogleAdsService.SearchStream` (GAQL queries) and `CustomerService.ListAccessibleCustomers`, and never calls mutate endpoints.

> **Alternative:** Google publishes an [official Google Ads MCP server](https://developers.google.com/google-ads/api/docs/developer-toolkit/mcp-server). You can run it alongside this server. This server's Google Ads module exists so that the unified cross-platform tools (`get_cpi_comparison`, `get_country_comparison`, …) can normalize Google Ads data together with Meta and Play.

## 1. Developer token

1. Sign in to your Google Ads **manager (MCC) account**.
2. Open **Admin → API Center** and copy the developer token.
3. Make sure the token's access level allows the accounts you want to read.

> Google changed developer-token and access-level onboarding in 2026. Follow the current instructions in the [Google Ads API docs](https://developers.google.com/google-ads/api/docs/get-started/dev-token).

## 2. OAuth client

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project (or reuse one) and **enable the Google Ads API**.
2. Configure the **OAuth consent screen**. While the app's publishing status is **Testing**, add yourself as a test user.
   - Refresh tokens issued to apps in *Testing* status expire after 7 days. Publish the app to *In production* to get long-lived tokens. Internal apps aren't affected.
3. Create an **OAuth client ID** of type **Desktop app**, and copy the client ID and secret.

## 3. Store credentials and authorize

```powershell
meeza-marketing-mcp secrets set GOOGLE_ADS_DEVELOPER_TOKEN
meeza-marketing-mcp secrets set GOOGLE_ADS_CLIENT_SECRET
$env:GOOGLE_ADS_CLIENT_ID = "1234-abc.apps.googleusercontent.com"   # or put it in appsettings.json
meeza-marketing-mcp auth google-ads
```

`auth google-ads` opens your browser, runs the OAuth loopback flow with PKCE, and stores the refresh token in the encrypted secret store. The only scope requested is `https://www.googleapis.com/auth/adwords`, which Google requires for API access. The server itself only issues read queries.

## 4. Choose accounts

| Your setup | Settings |
|---|---|
| You access client accounts through a manager | `GOOGLE_ADS_LOGIN_CUSTOMER_ID` = manager ID (digits only, dashes are fine) |
| You want to restrict which accounts can be read | `GOOGLE_ADS_CUSTOMER_IDS` = comma-separated customer IDs |
| Neither | All accounts directly accessible to the signed-in user are used |

Manager accounts are skipped in reports, because they have no metrics of their own.

## 5. Verify

```powershell
meeza-marketing-mcp check
```

## How installs are measured

`installs` is the sum of `metrics.conversions` from **enabled conversion actions in the `DOWNLOAD` category**: Google Play app installs, Firebase `first_open`, or third-party app analytics. If an account has no such action, `installs` is `null`. Use `google_ads_get_conversion_metrics` to see every conversion action. Note that `conversions` only counts actions marked *primary*.

Country data comes from `geographic_view`, filtered to `LOCATION_OF_PRESENCE` (the user's physical location).

## API version

The default is `v25`. Override it with `GOOGLE_ADS_API_VERSION`. Google sunsets each major version about a year after release, so update the setting when you see `NOT_FOUND` errors.
