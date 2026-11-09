# Meta Ads setup

The server reads Meta data through the [Meta Marketing API](https://developers.facebook.com/docs/marketing-apis/), using a token with the **`ads_read`** permission and nothing else.

## 1. Create a Meta app

1. Go to <https://developers.facebook.com/apps> and create an app. Choose the **Business** type and connect it to your Business portfolio.
2. Add the **Marketing API** product.
3. Under **App settings → Basic**, copy the **App secret**. It's optional, but recommended: see step 4.

> While the app is in development mode it can read ad accounts owned by your Business. Reading ad accounts of other businesses requires Advanced Access for `ads_read` through App Review. Follow Meta's current requirements.

## 2. Create a System User token (recommended)

System User tokens don't expire and aren't tied to a person.

1. In **Business Settings → Users → System users**, add a system user with the **Employee** role.
2. Click **Assign assets** and choose **Ad accounts**. Select your accounts with the **View performance** permission only.
3. Click **Generate new token**, select your app, and tick only **`ads_read`**.
4. Copy the token.

A user access token from the Graph API Explorer also works for testing. Short-lived user tokens expire after about an hour.

## 3. Store the credentials

```powershell
meeza-marketing-mcp secrets set META_ACCESS_TOKEN
meeza-marketing-mcp secrets set META_APP_SECRET      # optional: enables appsecret_proof
```

Optionally, limit the server to specific ad accounts. Add this to `appsettings.json` or set it as an environment variable:

```json
{ "Settings": { "META_AD_ACCOUNT_IDS": "act_1234567890,act_9876543210" } }
```

If you don't set an allow-list, the server can query every ad account the token can see (up to 25 per request).

## 4. Verify

```powershell
meeza-marketing-mcp check
```

## How installs are measured

`installs` comes from the `mobile_app_install` action, which Meta attributes using your ad account's attribution setting. Installs only appear if the Meta SDK or a Mobile Measurement Partner reports install events for your app. When a campaign reports no install action, `installs` is `null` rather than `0`, because Meta can't distinguish "no installs" from "not tracked".

## API version

The default is `v25.0`. Override it with `META_API_VERSION`. Meta supports each version for about two years.
