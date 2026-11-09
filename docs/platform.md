# Marketing intelligence platform

The dashboard at `http://127.0.0.1:5057/` is a multi-project marketing intelligence app built on top of the existing
read-only connectors. The original single-page live dashboard is still available at `/classic`.

## How data flows

```text
Google Ads ─┐                ┌─ Sync engine (backfill + incremental, retries, leases) ─► SQLite store (per project)
Meta Ads   ─┼─ connectors ───┤                                                              │
GA4        ─┘ (read-only)    └─ Live tools (MCP / classic dashboard)                         ▼
                                                                     Analytics engine ─► /api/v2 ─► app UI
                                                                                     └─► MCP get_project_* tools
                                                                                     └─► AI analyst (Claude API)
```

* **Store**: `%APPDATA%\MeezaMarketingMcp\marketing.db` (override with `MEEZA_DB_PATH`). Schema migrations are in
  `src/Meeza.MarketingMcp/Storage/Migrations.cs` (forward-only, versioned).
* **Every view is computed from the store**, never from live APIs, so pages load instantly and keep working while a sync
  runs or a platform is down.

## Projects

A project = one game. It has its own settings (genre, platforms, store URLs, target countries, monetization, currency,
time zone, launch stage, objectives, tutorial/purchase GA4 events), its own linked accounts (Google Ads customers,
Meta ad accounts, GA4 properties, Play apps), its own synced data, KPI targets, analyst conversations and research
reports. Credentials stay in the encrypted secret store and are shared; a project only selects which accounts it uses.
Unlinking an account deletes that account's data from the project. Deleting a project deletes all of its data.

## Synchronization

| Behaviour | Implementation |
|---|---|
| Initial backfill | `backfill_days` (default 90) in chunks (Google Ads 31 d, Meta 14 d, GA4 30 d), newest first. Interrupted backfills resume from `backfilled_from`. |
| Incremental | Every run re-reads a lookback window because platforms revise recent days: Google Ads 14 d (conversion window), Meta 10 d (attribution window), GA4 33 d (cohorts mature to D30 + ~48 h processing). A gap since the last sync is covered automatically. |
| Schedule | Background scheduler checks every 5 min; each project syncs every `sync_interval_minutes` (default 60). Failures back off 15 min → 6 h. Disable with `MEEZA_SYNC=off`. |
| Manual | **Sync now** in the header / Integrations, `meeza-marketing-mcp sync [project]`, or the `sync_project_data` MCP tool. |
| Pagination | Meta follows `paging.next` and refuses to store a truncated window; GA4 follows `offset` until every row is read; Google Ads uses `searchStream`. |
| Retries / rate limits | `RetryHandler`: exponential backoff with jitter on 429/5xx/network errors, honouring `Retry-After`. Google OAuth tokens refresh automatically. |
| Idempotency | Each window is replaced inside one transaction (delete window → insert), keyed by primary keys, so re-runs never duplicate and restated values overwrite. |
| Never lose good data | A failed window writes nothing. An empty response for a window that already has data is **not** applied (warning recorded). |
| Concurrency | A database lease per account stops the MCP server and a terminal dashboard from syncing the same account at once. Runs left `running` by a crash are marked failed at start-up. |
| Status per account | last attempt, last success, latest data date, records, duration, error, warnings, consecutive failures, history of runs. |

**Freshness** (shown on every KPI card, in the header and in Integrations): `fresh`, `syncing`, `delayed` (synced, but
the newest day with activity is older than expected), `error` (last attempt failed; data is from the last success),
`stale` (no success for 3× the interval / 6 h), `never`.

## Data rules

* `null` = unavailable, `0` = measured zero. Installs are null when no install conversion tracking exists.
* Currencies are never converted. KPIs use the project currency (or the largest one) and list others separately.
  ROAS is unavailable when GA4 revenue and ad spend use different currencies.
* Platform-attributed installs are never added together. The Installs KPI uses GA4 `first_open` (deduplicated, all
  sources) when GA4 is connected; per-platform numbers are shown beside it. Blended CPI across platforms is labelled.
* Reach is not additive: it is stored per window (last 7/14/30 complete days, Meta) and only shown for those ranges.
* Retention is **exact-day** cohort retention from GA4 (`firstSessionDate` cohorts, `cohortActiveUsers/cohortTotalUsers`).
  Cells a cohort has not reached are blank, never 0%; cells in the last two complete days are *provisional*; curves
  use mature cells only. Rolling retention and retention by creative are reported as unavailable (they need
  user-level data / an MMP) rather than approximated.
* Dates are each account's/property's own calendar day; "today" is computed in the account's time zone.

## Analyses

| Page | What it computes |
|---|---|
| Executive overview | **Diagnosis & marketing plan** (stage, health check vs your targets or cited published benchmarks, prioritised strategy moves with steps and success metric, what needs to improve); 25 KPIs with value, target and achievement %, previous period, daily trend, source, last update, freshness, definition; organic vs paid. |
| Acquisition | Daily spend/installs, countries with GA4 D1/D7 and growth-opportunity flags, organic demand without spend, attribution reconciliation. |
| Retention & cohorts | D0–D30 heatmap, curve vs previous period, segment comparison (country, platform, channel, campaign, ad group), churn trend, new vs returning. |
| Campaigns | Campaign / ad set / ad table with period-over-period changes and rule flags (high CPI, wasted spend, low CTR, rising CPM, declining conversion, poor quality, good retention, scale candidate, investigate before scaling). |
| Creatives | Per creative preview, metrics, composite score (CPI 50%, IPM 35%, CTR 15% vs the median of the same platform and format — never CTR alone), fatigue detection, formats, hook patterns, gameplay vs promotional (heuristic), suggestions. |
| Funnel | Impressions → clicks → (store visits) → installs → first open → tutorial → D1 return → purchasers / ad viewers, with diagnosis by stage. |
| Budget & forecast | Spend→installs power-curve model (or constant CPI with a warning), 14-day CPI forecast with 80% band, scenarios, LTV estimates, reallocation proposals. Nothing is ever changed in an ad account. |
| AI analyst | Deterministic findings (problem, severity, evidence, period, hypothesis, confidence, action, expected impact, validation) + chat with Claude over tools that read the same computations. |
| Market research | Claude with web search / web fetch; reports keep citations and separate verified findings from estimates. |
| KPI targets | Targets by KPI and scope (all / country / platform / campaign); sourced industry benchmarks for context. |

## AI features (optional)

Add an Anthropic API key in **Integrations → Claude** (stored encrypted, or `ANTHROPIC_API_KEY`). Requests use
`claude-opus-5-5` (override with `MEEZA_AI_MODEL`) with server-side refusal fallbacks (`fallbacks: "default"`).
Without a key, everything except the chat and research works. In Claude Desktop / Code, the `get_project_*` MCP tools
give Claude the same data without a key.

## CLI

```powershell
meeza-marketing-mcp projects                       # projects, connections, last sync per account
meeza-marketing-mcp sync [PROJECT_ID] [--integration google_ads|meta|ga4]
meeza-marketing-mcp dashboard                      # app + background sync
```
