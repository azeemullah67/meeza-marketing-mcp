# Google Analytics 4 / Firebase setup

Firebase Analytics for apps stores its data in a Google Analytics 4 (GA4) property. Connecting it adds:

| Tool | What it answers |
|---|---|
| `ga4_get_active_users` | DAU, new users, sessions, engaged time per user, stickiness. Breakdowns by country, app version, platform, source, campaign, device |
| `ga4_get_retention` | D1 / D3 / D7 / D14 / D30 cohort retention, per-cohort table and a fitted retention curve |
| `ga4_get_revenue` | IAP and ad revenue GA4 knows about, and ARPDAU |
| `get_game_scorecard` | "Can this game work?": retention graded against industry percentiles, a verdict with next steps, and estimated LTV next to CPI |
| `check_analytics_tracking` | "Why does Analytics show fewer users?": GA4 new users vs ad-platform installs per day, app versions sending data, and a diagnosis |

The dashboard also gets a **Players** section.

Access is read-only (`analytics.readonly` scope).

## Steps

1. **Enable two APIs** in the Google Cloud project that owns your service account:
   - [Google Analytics Data API](https://console.cloud.google.com/apis/library/analyticsdata.googleapis.com)
   - [Google Analytics Admin API](https://console.cloud.google.com/apis/library/analyticsadmin.googleapis.com)
2. **Choose the service account.** The easiest option is to reuse the Google Play one: leave `GA4_SERVICE_ACCOUNT_JSON` empty and it is used automatically. To use a separate account, save its JSON key in Settings → Google Analytics 4 / Firebase.
3. **Grant access.** In Google Analytics, open **Admin → Property access management → + → Add users**, paste the service account email (shown in Settings) and choose **Viewer**. From Firebase you can get there via Project settings → Integrations → Google Analytics.
4. **Optional:** set `GA4_PROPERTY_IDS` to the property ID (Admin → Property details, digits only, not the `G-…` measurement ID) to limit access to one game.
5. Press **Test connection** in Settings, or run `meeza-marketing-mcp check`.

## Reading the numbers

- GA4 app data takes **24–48 hours** to finish processing. The last two days always look low.
- GA4 *Active users* counts only engaged users. Compare **New users** (first opens) with installs instead.
- When GA4 hides small counts ("thresholding"), set **Admin → Reporting identity → Device-based**.
- Retention only uses cohorts old enough to reach each day. A grade needs 100+ users at that day.
- LTV, ROAS and the predicted D30/D90 values are **estimates** from the fitted curve, labelled as such. They are compared with CPI only when the currencies match.
