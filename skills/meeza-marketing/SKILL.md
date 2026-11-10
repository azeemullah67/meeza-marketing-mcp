---
name: meeza-marketing
description: Answer mobile-game marketing questions from live Meta Ads, Google Ads, Google Play and Google Analytics 4 / Firebase data through the Meeza Marketing MCP server. Use when the user asks about ad spend, installs, CPI, CPM, CTR, ROAS, retention (D1/D7/D30), cohorts, level progression, creatives, countries, store ratings, crashes or ANRs, whether to scale or cut a campaign, or wants a dashboard of their game's marketing. Read-only. Requires the meeza-marketing MCP server to be configured.
---

# Meeza Marketing: game UA and retention analysis

The `meeza-marketing` MCP server exposes read-only tools over the user's own ad and analytics accounts.
Nothing here can change a campaign, budget or bid. Every answer must come from a tool result, never from memory.

## Before the first question

1. Call `get_connected_accounts`. It says which platforms are configured and which accounts each one can read.
2. If a platform the user asks about is missing, say so and point them at the dashboard's Data sources page
   (`open_dashboard`) or the setup guides. Do not guess numbers for an unconnected platform.
3. If the user has projects (`list_projects` returns any), prefer the `get_project_*` tools: they use a synchronized
   local history with freshness tracking and are faster and comparable over time. Fall back to the live
   `meta_*` / `google_ads_*` / `ga4_*` / `adjust_*` / `play_*` tools when there is no project or the user wants raw platform data.

## Picking the tool

| User asks about | Tool |
|---|---|
| "How is marketing going?" / executive summary | `get_project_overview`, else `get_campaign_summary` + `get_spend_summary` |
| Spend by platform or account | `get_spend_summary` |
| Installs (and why sources disagree) | `get_install_summary`, `get_project_attribution` |
| CPI by campaign / country, campaigns spending without installs | `get_cpi_comparison`, `google_ads_get_cost_per_install`, `meta_get_cost_per_install` |
| Which country to scale or stop | `get_country_comparison`, `google_ads_get_country_performance`, `meta_get_country_performance` |
| Ad-level results, winners and losers | `get_ad_performance`, `get_creative_test_board`, `get_project_creatives` |
| Google App campaign assets (headlines, videos) | `google_ads_get_asset_performance` |
| This week vs last week | `compare_periods` |
| "Anything wrong right now?" | `get_marketing_alerts`, `get_project_findings` |
| Retention D1/D7/D30, cohorts | `ga4_get_retention`, `get_project_retention` |
| Retention or installs by network / campaign / creative (incl. Meta) | `adjust_get_retention`, `adjust_get_performance`, `get_project_retention` with `adjust_*` segments |
| "Can this game work?" / soft-launch verdict | `get_game_scorecard` |
| Where players quit (levels) | `ga4_get_level_progression` |
| Funnel from impression to purchase | `get_project_funnel` |
| DAU, sessions, stickiness | `ga4_get_active_users` |
| Revenue, ARPDAU, LTV | `ga4_get_revenue`, `get_project_overview` |
| Budget change: what would happen | `get_project_forecast` (proposals only) |
| Store reviews, ratings, crashes, ANRs | `play_get_reviews`, `play_get_ratings`, `play_get_android_vitals`, `play_get_crash_metrics`, `play_get_anr_metrics` |
| "GA4 shows fewer users than installs" | `check_analytics_tracking` |
| Is my CPI / CTR normal for the genre | `get_industry_benchmarks` |
| Custom analysis, export | `get_date_range_report` |
| Show me visually | `open_dashboard` |
| Data looks stale | `sync_project_data`, then re-query |

Dates: every reporting tool takes `datePreset` (`yesterday`, `last_7_days`, `last_30_days`, `last_N_days`,
`this_month`, `last_month`) or `startDate` / `endDate` (yyyy-MM-dd, inclusive). Default to the last 7 days for
performance questions and the last 30 days for retention. Say which range you used.

## Rules for interpreting results

- **Never add installs across sources.** Meta-attributed, Google Ads-attributed, GA4 first opens and Play store
  installs count different things with different attribution windows. Present them side by side.
- **Currencies are per account.** Do not sum PKR and USD. Tools group by currency; keep that grouping.
- **`null` means unavailable, not zero.** Say "not tracked" and, when the result includes a note, repeat why.
- **Respect sample sizes.** Fewer than 50 users in a cohort, or fewer than 20 installs on a campaign, is not
  enough to judge. Tools flag this; carry the flag into your answer.
- **Retention windows.** D7 needs cohorts at least 7 days old. The blended curve only uses cohorts old enough
  to reach each day. Do not read an unmatured cohort as a drop.
- **Win rates in level progression** need the `success` parameter registered as a GA4 custom metric. If the
  report says success is not tracked, say win/loss is unknown rather than inventing a rate.
- **Thresholded / data-loss flags** from GA4 mean small segments were hidden. Mention it when drawing conclusions
  about small countries or versions.
- Quote the tool's own notes about attribution and definitions when the user is deciding money.

## Answering pattern

1. Headline number(s) with the date range and source, for example "Google Ads, 2–8 Nov: PKR 5,667 spend, 249
   installs, CPI PKR 22.8".
2. What changed vs the previous period (use `compare_periods` or the project overview's deltas).
3. The one thing to act on, with the evidence row behind it (campaign, ad, country or level).
4. What is not measurable and why (missing connection, no revenue tracking, small sample).

Keep it short. Marketers want the number, the trend and the action, not every row.

## Common workflows

**Weekly check.** `get_marketing_alerts` → `compare_periods` → `get_creative_test_board`. Report: what to cut,
what to scale, what to test next.

**"Should I spend more?"** `get_game_scorecard` first. If D1 < 25% or D7 < 8%, the honest answer is to fix retention
before buying installs, whatever the CPI is. Then `get_project_forecast` for the budget scenario.

**"Why did installs drop?"** `compare_periods` for which platform and campaign moved → `get_ad_performance` for the ad
→ `play_get_android_vitals` and `play_get_reviews` to rule out a broken build → `check_analytics_tracking` if GA4
and the ad platforms disagree.

**New build shipped.** `ga4_get_active_users` with `breakdown: app_version` to confirm the version reports data,
then `ga4_get_retention` and `ga4_get_level_progression` filtered by `appVersion` to compare against the previous
build.

## What this skill will not do

- Change anything in an ad account, GA4 or Play Console. If the user asks to pause, edit or create, explain that
  the server is read-only and tell them exactly what to change in the platform UI.
- Report numbers for accounts outside the configured allow-lists; a denied account is a configuration choice,
  not a bug to work around.
- Use the user's credentials for anything other than these tools. Never ask for tokens in chat; the server
  stores them locally (dashboard → Data sources).

## Setup (if the server is not configured)

Install from https://github.com/azeemullah67/meeza-marketing-mcp, then add it to the agent:

```
claude mcp add meeza-marketing -- <path>/publish/meeza-marketing-mcp.exe
```

Connect accounts in the dashboard (`meeza-marketing-mcp dashboard`): Meta System User token with `ads_read`,
Google Ads service account or OAuth, GA4 service account with Viewer access, Play Console service account.
Allow-lists (`META_AD_ACCOUNT_IDS`, `GOOGLE_ADS_CUSTOMER_IDS`, `GA4_PROPERTY_IDS`, `ADJUST_APP_TOKENS`, `PLAY_PACKAGE_NAMES`) limit
what the server may read. Details in `docs/` of the repository.
