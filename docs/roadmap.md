# Roadmap: what a game UA team needs next

This is a gap analysis of Meeza Marketing MCP against what a mobile-game user-acquisition (UA) team needs. It was written from a growth/UA-engineering point of view in November 2026.

**The core gap.** Today the server measures **cost** (spend, installs, CPI, CTR, CPM) but not **value** (revenue, retention, LTV, ROAS). It can tell you what an install costs. It can't tell you whether a campaign pays back. Most of the items below close that gap.

Every item can stay **read-only**. Anything that sends data out (Slack or email reports) stays a separate, opt-in CLI command and never becomes an MCP tool.

## Done in this release

| Feature | Where |
|---|---|
| Period-over-period comparison (spend, installs, CPI, CTR, CPM, IPM, biggest CPI moves) | `compare_periods` tool; KPI deltas on the dashboard |
| Alerts: spend without installs, CPI outliers (>2× median), CPI ±20%, spend ±30% | `get_marketing_alerts` tool; "needs attention" strip on the dashboard |
| Sourced industry benchmarks and big-studio practices | `get_industry_benchmarks` tool; Benchmarks section |
| Creative testing board: test / scale / steady / refresh (frequency) / cut | `get_creative_test_board` tool; Creative testing lab section |
| Growth health score, "What's going on" and "Improve next" actions | Dashboard top strip |
| **Firebase / GA4**: DAU, new users, stickiness, engagement, breakdowns, cohort retention, revenue and ARPDAU | `ga4_get_active_users`, `ga4_get_retention`, `ga4_get_revenue`; dashboard Players section |
| **Soft-launch go/no-go scorecard**: retention graded vs GameAnalytics percentiles, verdict and next steps, estimated LTV (ARPDAU × fitted retention) vs CPI | `get_game_scorecard`; Players section |
| **Tracking health**: GA4 new users vs ad-reported installs, app-version coverage, diagnosis | `check_analytics_tracking` |
| **Anomaly detection on daily series** (median ± 3 robust SDs over 14 days) | `get_marketing_alerts` |

## Quick wins (existing credentials, no new accounts)

| Feature | What enables it | Effort |
|---|---|---|
| **Platform-reported ROAS and in-app value** | Meta insights: `action_values`, `mobile_app_purchase_roas`, `purchase_roas`, `action_attribution_windows`. Google Ads: `metrics.conversions_value` ÷ cost, `conversions_value_by_conversion_date`, `campaign.target_roas`. Label it *platform-claimed*, not true ROAS. | S |
| **Creative analytics and fatigue** | Meta ad level, daily: `frequency`, `video_p25…p100_watched_actions`, `video_thruplay_watched_actions`, creative thumbnails. Google: `ad_group_ad_asset_view.performance_label`. Rule: CTR or IPM falls more than 30% from the ad's first 3 days while frequency rises. | M |
| **Budget pacing** | Meta: `daily_budget`, `lifetime_budget`, `budget_remaining`, account `spend_cap`/`amount_spent`. Google: `campaign_budget.amount_micros` plus today's cost by `segments.hour`. | S |
| **Change log next to the CPI curve** | Google Ads `change_event`, Meta ad account `activities` | S |
| **iOS SKAN (Meta only)** | Meta breakdowns `skan_campaign_id`, `skan_conversion_id` | S |
| **Store conversion and retained installers** | Play bulk reports bucket: `acquisition/retained_installers/*` (visitors, installers, D1/7/15/30 retained installers by channel and country) | S |
| **Play IAP revenue** | Play bucket `earnings/` and `sales/` (needs the financial-data permission) | S |
| **More vitals** | Play Developer Reporting API: `slowStartRateMetricSet`, `errorCountMetricSet`, `vitals.errors.issues` | S |
| **Review themes** | Snapshot `play_get_reviews` locally, since the API keeps only 7 days | S |
| **Optional currency normalisation** | ECB reference rates (Data Portal SDMX API). Opt-in, with the source and date stated in `notes` | S |

## P0: must-have to run UA properly

| Feature | Source | Effort | Tools / panel |
|---|---|---|---|
| **MMP as the source of truth.** Deduplicated installs and an organic/paid split. Removes the double counting between Meta and Google. | AppsFlyer Pull API and Master API, Adjust Report Service API, or Singular Reporting API, behind one `IMmpProvider` interface | L | `mmp_get_installs`, `mmp_get_organic_vs_paid`, `mmp_get_network_report`; MMP installs become the default install/CPI source |
| **Cohort LTV and ROAS (D0, D7, D30)** | MMP cohort APIs; GA4 Data API `cohortSpec` as a fallback | M | `get_cohort_roas`, `get_ltv_curve`, `get_retention_cohorts`; cohort ROAS heatmap |
| **Ad revenue (ILRD) plus IAP.** Hybrid-casual games often earn 50–90% from ads. | AppLovin MAX Revenue Reporting API, ironSource/LevelPlay Reporting API, Unity Monetization Stats API, MMP ad-revenue fields | M | `monetization_get_ad_revenue`, `get_arpdau`, `get_revenue_mix` |
| **GA4 funnels and events** (cohorts, active users and revenue are done) | GA4 Data API `runFunnelReport`, BigQuery export | S | `ga4_get_funnel`, `ga4_get_events` |
| **Multi-game support** | Local `games.json` mapping each game to its Meta app ID, Google app ID, Play package, iOS app ID and MMP app ID | S | `list_games`; `game` parameter on every tool; game selector in the dashboard |

## P1: strong value

| Feature | Source | Effort |
|---|---|---|
| **iOS SKAN / AdAttributionKit**: conversion-value decoding, modelled iOS ROAS | AppsFlyer SKAN Aggregated Pull API, Adjust or Singular SKAN reporting | M |
| **More ad networks** | AppLovin Advertiser Reporting API, Unity Advertising Stats API, Mintegral Reporting API, TikTok Marketing API (`/report/integrated/get/`), Apple Search Ads Campaign Management API (reports) | M each |
| **Cross-network creative leaderboard and tagging** (hook, UGC, gameplay, length) | Network APIs above; tags stored locally | M |
| **iOS store data**: impressions, product page views, conversion, Product Page Optimization tests, sales, reviews | App Store Connect API (Analytics Reports, Sales and Trends, customer reviews) | M |
| **Payback forecasting** (a first estimate ships in `get_game_scorecard`; next: cohort ARPU curves and payback day) | MMP or GA4 cohort revenue | M |
| **Scheduled reports and alerts** to Slack or email | Slack webhooks / SMTP, run from a CLI command with a recipient allow-list | M |

## P2: later

| Feature | Source |
|---|---|
| ASO: keyword ranks, listing conversion | AppTweak, AppFollow or Sensor Tower APIs. Play store listing experiments have no public API. |
| Competitor ad intelligence | Sensor Tower Ad Intelligence API, AppMagic API, Meta Ad Library API (EU and political ads only) |
| Live-ops / event calendar overlaid on KPIs | Local `events.json` or Google Calendar, plus Play version codes |
| Warehouse mode | BigQuery, AppsFlyer Data Locker; allow-listed saved queries only |
| Incrementality and MMM | Meta Conversion Lift results, Google geo experiments, Robyn or Meridian |

## Security and privacy rules for new modules

- **Aggregates by default.** MMP raw data, impression-level revenue, BigQuery events and purchase data contain personal data (GDPR, CCPA, Apple ATT). Tools return aggregates only. Any user-level pull would be opt-in, kept out of Claude's context and never cached unencrypted.
- **SKAN privacy thresholds** produce null conversion values. Show them as unavailable, never as zero.
- **Credentials.** Use a dedicated read-only user for MMP API tokens. Apple JWT private keys go into the encrypted secret store. Each network gets its own `HttpClient`, and every new key is added to the redactor.
- **Data leaving the machine.** Slack and email reports need a recipient allow-list and send aggregates only, never review text with user names.
- **Local caches** (review snapshots, cohort history) are encrypted and have a retention limit.
- **Untrusted text.** Review text, ad names and competitor creatives are data, not instructions, and stay fenced and labelled when passed to Claude.
- **Licensing.** Sensor Tower, AppMagic and AppTweak terms often forbid redistribution. Mark that data and keep it out of shared reports. Never scrape the stores.

## Suggested order

1. Quick wins (about a week).
2. Multi-game support.
3. One MMP (AppsFlyer or Adjust) with cohort ROAS.
4. Ad revenue (MAX or LevelPlay) and GA4.
5. SKAN, more ad networks and creative analytics.
