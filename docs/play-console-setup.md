# Google Play Console setup

The server uses three Google sources, all read-only and all through one service account:

| Data | Source |
|---|---|
| Accessible apps, crash and ANR vitals | [Play Developer Reporting API](https://developers.google.com/play/developer/reporting) |
| Recent reviews | [Play Developer API](https://developer.android.com/google/play/developer-api) `reviews.list` (read only) |
| Store installs and ratings history | Play Console **bulk report exports** in Cloud Storage |

> **Limitation:** The Play APIs don't expose every Play Console dashboard metric. Install and acquisition numbers aren't available through the Developer API, so this server reads them from the monthly CSV exports that Play Console writes to a Cloud Storage bucket. Those exports lag by a few days.

## 1. Google Cloud project

1. In the [Google Cloud console](https://console.cloud.google.com/), select or create a project.
2. Enable **Google Play Android Developer API** and **Google Play Developer Reporting API**.
3. Under **IAM & Admin → Service accounts**, create a service account. It doesn't need any Cloud IAM roles.
4. Under **Keys → Add key → JSON**, download the key and keep it somewhere private.

## 2. Grant access in Play Console

1. In Play Console, open **Users and permissions → Invite new users**.
2. Enter the service account's email address (`…@….iam.gserviceaccount.com`).
3. Grant **only** these permissions, for all apps or only the apps you want analysed:
   - **View app information and download bulk reports (read-only)**
   - **View app quality information (read-only)**, for Android vitals
   - If `play_get_reviews` returns 403, you may also need to grant review access. This server never posts replies.
4. Permission changes can take up to 24 hours to apply to the APIs.

## 3. Find the reports bucket (optional, for store installs and ratings)

In Play Console, open **Download reports → Statistics** (or **Reviews**) and click **Copy Cloud Storage URI**. It looks like `gs://pubsite_prod_rev_01234567890987654321/…`. The bucket name is the part after `gs://`.

## 4. Configure

```powershell
# Option A: point at the key file (keep it outside the repository)
$env:PLAY_SERVICE_ACCOUNT_KEY_PATH = "C:\secure\play-reporting-key.json"

# Option B: store the key encrypted and delete the file afterwards
meeza-marketing-mcp secrets set PLAY_SERVICE_ACCOUNT_JSON --from-file C:\secure\play-reporting-key.json
```

Then add the non-secret settings to `appsettings.json`:

```json
{
  "Settings": {
    "PLAY_PACKAGE_NAMES": "com.example.game",
    "PLAY_REPORTS_BUCKET": "pubsite_prod_rev_01234567890987654321"
  }
}
```

## 5. Verify

```powershell
meeza-marketing-mcp check
```

## Notes on the data

- **Reviews:** `reviews.list` only returns reviews *with text* that were created or modified in the **last 7 days**. For older reviews, use the reviews export.
- **Ratings:** `play_get_ratings` reads `stats/ratings/ratings_<package>_<yyyyMM>_overview.csv` (or `_country.csv`) for the official averages. It also shows a star distribution of recent reviews, which is a small and biased sample.
- **Store installs:** `play_get_store_installs` reads `stats/installs/installs_<package>_<yyyyMM>_overview.csv` or `_country.csv`. Column names have changed over the years, so the server sums every `Daily …` / `… events` column and reports the latest `Total …` / `Active …` values. `primaryInstalls` uses *Daily User Installs*, falling back to *Install events* and then *Daily Device Installs*.
- **Vitals:** Vitals data uses the America/Los_Angeles time zone and is usually 1–2 days behind. End dates are clamped automatically to the latest complete day. Period rates are weighted by `distinctUsers`. They're shown next to Google Play's bad-behavior thresholds: a 1.09% user-perceived crash rate and a 0.47% user-perceived ANR rate.
- **Store listing details:** These aren't read, because the Developer API only exposes them through *edit* sessions, which are a write workflow.
