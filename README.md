# Meeza Marketing MCP

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server that connects **Meta Ads**, **Google Ads**, **Google Play Console**, **Google Analytics 4 / Firebase** and **Adjust** to Claude. You can then ask questions about ad spend, acquisition costs, countries and app quality in plain language, without opening each dashboard.

The server isn't tied to any game, company or ad account. Each user runs it on their own machine with their own credentials.

> **Live dashboard included.** When Claude connects, a private local dashboard starts at `http://127.0.0.1:5057`. It shows spend, installs and CPI **by ad**, ad set or campaign, plus daily trends, countries and Google Play health. Ask Claude to *"open the marketing dashboard"*, or run `meeza-marketing-mcp dashboard`. Try it with sample data: `meeza-marketing-mcp dashboard --demo`. See [docs/dashboard.md](docs/dashboard.md).
>
> ![Overview page of the Meeza Growth dashboard, showing a synthetic demo project](docs/images/dashboard-overview.png)
>
> *Overview page with invented demo data: plan verdict, top actions, KPI tiles against targets, prioritised marketing plan and findings. See [docs/dashboard.md](docs/dashboard.md) for the retention and cohort views.*

> **Marketing intelligence platform.** The dashboard is now a multi-project app: create a project per game, link its Google Ads / Meta / GA4 accounts, and it keeps a synchronized local history (backfill + hourly incremental sync with freshness tracking). Pages: executive KPIs with targets, acquisition and attribution reconciliation, D0–D30 cohort retention, campaigns, creative intelligence, funnel, budget forecasting, an AI analyst and cited market research. The previous dashboard remains at `/classic`. See [docs/platform.md](docs/platform.md).

> **Read-only by design.** The server can't create, edit, pause or delete campaigns, and it never posts review replies. Every tool is annotated `readOnlyHint: true`, and a test enforces that.

## What you can ask

- "Analyze my advertising campaigns for the last seven days."
- "Compare Pakistan, Philippines and the United States based on CPI."
- "Which campaigns are spending money without generating installs?"
- "What needs my attention in marketing this week?" / "Compare this week with last week."
- "Show my top 10 ads by spend this week with CPI." / "Open the marketing dashboard."
- "Compare Meta Ads and Google Ads acquisition performance for last month."
- "Are crash or ANR rates above Google Play's bad-behavior thresholds?"
- "Summarize this week's 1–2 star reviews and group them by theme."
- "How does my CPI and CTR compare with industry benchmarks for puzzle games?"
- "Can this game work?" / "What is my D1 and D7 retention?"
- "Why does Google Analytics show fewer users than my installs?"

See [examples/prompts.md](examples/prompts.md) for more.

## Tools

| Area | Tools |
|---|---|
| Dashboard | `open_dashboard`, `get_ad_performance` (by ad / ad set / campaign across platforms) |
| Insights | `compare_periods` (this period vs the previous one), `get_marketing_alerts` (spend without installs, CPI outliers, CPI and spend swings, daily anomalies), `get_creative_test_board` (every ad sorted into test / scale / steady / refresh / cut, studio-style) |
| Benchmarks | `get_industry_benchmarks`: sourced mobile-game CPI, CTR, CPM, IPM, retention, ROAS, channel mix and big-studio practices |
| Unified | `get_connected_accounts`, `get_campaign_summary`, `get_spend_summary`, `get_install_summary`, `get_country_comparison`, `get_cpi_comparison`, `get_date_range_report` |
| Meta Ads | `meta_get_ad_accounts`, `meta_get_campaigns`, `meta_get_adsets`, `meta_get_ads`, `meta_get_ad_insights`, `meta_get_country_performance`, `meta_get_cost_per_install` |
| Google Ads | `google_ads_get_accounts`, `google_ads_get_campaigns`, `google_ads_get_performance`, `google_ads_get_conversion_metrics`, `google_ads_get_country_performance`, `google_ads_get_cost_per_install`, `google_ads_get_asset_performance` (each headline, image and video with Google's Low / Good / Best rating) |
| Growth | `get_game_scorecard` (retention graded vs industry, verdict, next steps, estimated LTV vs CPI), `check_analytics_tracking` (GA4 users vs ad installs, diagnosis) |
| Google Analytics 4 / Firebase | `ga4_get_properties`, `ga4_get_active_users`, `ga4_get_retention`, `ga4_get_revenue` ([setup](docs/analytics-setup.md)) |
| Adjust (MMP) | `adjust_list_apps`, `adjust_get_performance` (installs, cost, CPI, revenue by network / campaign / ad group / creative / country), `adjust_get_retention` (install-cohort retention per network, campaign or creative) ([setup](docs/adjust-setup.md)) |
| Google Play | `play_get_app_details`, `play_get_reviews`, `play_get_ratings`, `play_get_android_vitals`, `play_get_crash_metrics`, `play_get_anr_metrics`, `play_get_store_installs` |

Every tool returns the same envelope: `{ tool, ok, dateRange, data, errors[], notes[] }`. If one platform fails, the others still return data.

### Data rules

- **Currencies are never converted or mixed.** Totals are always grouped by currency.
- **`null` means unavailable, not zero.** CPI is only computed when installs are greater than 0.
- **Install sources are kept separate.** Meta- and Google-attributed installs can overlap. Play Console installs are store-reported (organic + paid). Adjust installs are deduplicated by Adjust. They are shown side by side and never summed.
- **Adjust cost is never added to ad-platform spend.** It is the same money seen through Adjust, and is the only amount converted (by Adjust, into `ADJUST_CURRENCY`).
- **Time zones.** Each platform reports in its own account time zone. Play vitals use America/Los_Angeles.

## Quick start

Requirements: .NET 9 SDK or newer (the project targets `net9.0`; see [Changing the target framework](#changing-the-target-framework)).

```powershell
git clone https://github.com/<your-org>/meeza-marketing-mcp.git
cd meeza-marketing-mcp
dotnet test
dotnet publish src/Meeza.MarketingMcp -c Release -o publish
```

1. **Configure at least one platform**:
   - [Meta Ads](docs/meta-setup.md)
   - [Google Ads](docs/google-ads-setup.md)
   - [Google Play Console](docs/play-console-setup.md)

   Easiest: open the dashboard (`meeza-marketing-mcp dashboard`) and click **⚙ Settings**. Each connection has numbered steps with links, fields to paste into, **Save**, **Test connection** and, for Google Ads, **Sign in with Google**.

2. **Or store your secrets from the terminal.** On Windows the store uses DPAPI, scoped to the current user:

   ```powershell
   ./publish/meeza-marketing-mcp secrets set META_ACCESS_TOKEN
   ./publish/meeza-marketing-mcp secrets list
   ```

3. **Check your setup**:

   ```powershell
   ./publish/meeza-marketing-mcp check
   ```

4. **Connect Claude**: follow [docs/claude-setup.md](docs/claude-setup.md).

   Claude Code:

   ```powershell
   claude mcp add meeza-marketing -- "C:\path\to\publish\meeza-marketing-mcp.exe"
   ```

5. **Optional: install the skill.** [`skills/meeza-marketing/SKILL.md`](skills/meeza-marketing/SKILL.md) tells the
   agent when to reach for which tool, how to read attribution and retention correctly, and how to answer a
   marketer. For Claude Code copy the folder to `.claude/skills/meeza-marketing/` (project) or
   `~/.claude/skills/meeza-marketing/` (user); other agents that read `SKILL.md` files work the same way.

## Configuration

Settings are resolved in this order:

1. Environment variables
2. The encrypted secret store (`secrets set`)
3. `appsettings.json` (`Settings` section). This step only applies to **non-secret** settings: secrets are ignored if they appear in `appsettings.json`.

| Setting | Secret | Purpose |
|---|---|---|
| `META_ACCESS_TOKEN` | yes | Meta token with `ads_read` |
| `META_APP_SECRET` | yes | Optional: adds `appsecret_proof` to every call |
| `META_AD_ACCOUNT_IDS` | | Optional allow-list, e.g. `act_123,act_456` |
| `META_API_VERSION` | | Default `v25.0` |
| `GOOGLE_ADS_DEVELOPER_TOKEN` | yes | From the Google Ads API Center |
| `GOOGLE_ADS_CLIENT_ID` | | OAuth Desktop client ID |
| `GOOGLE_ADS_CLIENT_SECRET` | yes | OAuth client secret |
| `GOOGLE_ADS_REFRESH_TOKEN` | yes | Created by `auth google-ads` |
| `GOOGLE_ADS_LOGIN_CUSTOMER_ID` | | Manager (MCC) ID, when access goes through a manager |
| `GOOGLE_ADS_CUSTOMER_IDS` | | Optional allow-list |
| `GOOGLE_ADS_API_VERSION` | | Default `v25` |
| `PLAY_SERVICE_ACCOUNT_KEY_PATH` | | Path to the service account JSON key |
| `PLAY_SERVICE_ACCOUNT_JSON` | yes | Alternative: the key stored in the secret store |
| `PLAY_PACKAGE_NAMES` | | Allow-list, e.g. `com.example.game` |
| `PLAY_REPORTS_BUCKET` | | `pubsite_prod_rev_…` bucket for store installs and ratings exports |
| `ADJUST_API_TOKEN` | yes | Adjust API token (Account settings → My profile) |
| `ADJUST_APP_TOKENS` | | Optional allow-list of Adjust app tokens |
| `ADJUST_CURRENCY` | | Currency Adjust reports cost and revenue in (default `USD`) |
| `MEEZA_DASHBOARD` | | `off` stops the dashboard from starting with the MCP server |
| `MEEZA_DASHBOARD_PORT` | | Dashboard port (default `5057`) |

On macOS and Linux the encrypted store isn't available yet. Supply secrets as environment variables there, for example from your OS keychain or a secrets manager. The server refuses to write secrets to disk unencrypted.

## Security

- Secrets never come from `appsettings.json`.
- Secrets are never written into URLs. Meta and Google tokens travel in the `Authorization` header.
- All error text passes through a redactor that strips tokens, `client_secret` values and private keys.
- Logs go to stderr, and HTTP request URLs aren't logged.
- Each platform has its own `HttpClient` and its own credentials.
- Optional allow-lists restrict which ad accounts and apps can be queried. Account access is validated before every request.
- Throttling is handled: 429 and 5xx responses are retried with exponential backoff and `Retry-After`, and Meta rate-limit error codes are recognised.
- Only read endpoints are implemented against Meta, Google and Play. There's no mutate, edit or reply code path.
- The dashboard's only write action is saving your own connection settings to the encrypted local store. It needs a POST to `/api/settings`, a same-origin `Origin` header, a JSON body (128 KB max), a per-process anti-forgery token and the access-key cookie. Secret values are never sent back to the browser.

## Repository layout

```text
src/
  Meeza.MarketingMcp/          MCP host, CLI commands, unified tools
  Meeza.Marketing.Shared/      Normalized models, metrics, secrets, HTTP retry, redaction
  Meeza.Marketing.Meta/        Meta Marketing API client and tools
  Meeza.Marketing.GoogleAds/   Google Ads API (REST + GAQL) client and tools
  Meeza.Marketing.PlayConsole/ Play Developer Reporting API, reviews, bulk-report exports
tests/Meeza.Marketing.Tests/   Unit tests (no network)
skills/meeza-marketing/        SKILL.md: when and how an agent should use these tools
docs/                          Setup guides, architecture, security, troubleshooting
examples/                      Claude config and example prompts
```

See [docs/architecture.md](docs/architecture.md) for the design.

## Changing the target framework

The target framework is set in one place, [Directory.Build.props](Directory.Build.props) (`MeezaTargetFramework`). Once the .NET 10 SDK is installed, set it to `net10.0`.

## Roadmap

See [docs/roadmap.md](docs/roadmap.md) for the full UA gap analysis (MMP, cohort ROAS, ad revenue, SKAN, more networks, creative analytics). Highlights:

- Firebase Analytics / GA4 module: DAU/MAU, D1/D7/D30 retention, sessions, level progression, ad and IAP revenue.
- Encrypted secret store for macOS Keychain and Linux libsecret.
- Hosted multi-tenant version (Streamable HTTP, OAuth account linking, tenant isolation).

## License

[MIT](LICENSE) © Meeza Technologies. You are responsible for your own Meta and Google developer access, API permissions, and compliance with each platform's policies.
