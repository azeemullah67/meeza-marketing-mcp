# Meeza Marketing MCP v1.0.0: draft release notes

> Draft for review. Nothing has been tagged, released or published. Items marked **[confirm]** need the maintainer's decision before this goes out.

## What it is

A read-only [Model Context Protocol](https://modelcontextprotocol.io) server with a local dashboard. It connects your own ad and analytics accounts to Claude, so you can ask about spend, installs, CPI, retention and app quality in plain language. It runs on your machine with your credentials; there is no hosted service.

## What it connects

| Platform | What you get |
|---|---|
| Meta Ads | Accounts, campaigns, ad sets, ads, insights, country performance, cost per install |
| Google Ads | Accounts, campaigns, performance, conversions, country performance, cost per install, asset performance |
| Google Play Console | App details, reviews, ratings, Android vitals, crash and ANR rates, store installs |
| Google Analytics 4 / Firebase | Properties, active users, cohort retention, level progression, revenue |
| Adjust | Apps, performance by network / campaign / creative / country, install-cohort retention |

- **54 MCP tools**, including cross-platform reports (spend, installs, CPI, country comparison, period comparison, alerts, creative test board, industry benchmarks, game scorecard) and project tools that read the synchronized local store.
- **Local dashboard** at `http://127.0.0.1:5057`: one project per game, synchronized local history, executive KPIs with targets, campaigns, creatives, countries and attribution, funnel, cohort retention, budget forecast, KPI targets and a task list from an automatic 30-day check.
- **Optional AI analyst and market research** in the dashboard. Off by default; they run only with your own Anthropic API key and then send the project's synced advertising and analytics data, the project profile and your question to the Anthropic API.

## Read-only posture

This is what the code does, as checked on the release branch. It is not a security guarantee.

- The platform clients only call list and report endpoints: `GET` for Meta, Play and Adjust; `listAccessibleCustomers` and `searchStream` (a query) for Google Ads; `runReport` for GA4; vitals `:query` for Play. There is no mutate, edit or reply code path, so the server can't create, edit, pause or delete campaigns, and it never posts review replies.
- Every tool except `sync_project_data` is marked read-only (`readOnlyHint: true`), and a test checks those annotations. `sync_project_data` refreshes the local copy of a project's data: it reads from the platforms and writes only to the local database. Annotations describe tools to the client; they do not enforce behavior.
- Optional allow-lists (`META_AD_ACCOUNT_IDS`, `GOOGLE_ADS_CUSTOMER_IDS`, `GA4_PROPERTY_IDS`, `ADJUST_APP_TOKENS`, `PLAY_PACKAGE_NAMES`) limit which accounts are read, in the live tools, when a project links an account, and before every sync.
- The dashboard listens on `127.0.0.1` only, needs a private access key, and accepts changes only as same-origin JSON `POST`s with an anti-forgery token.
- Secrets are read from environment variables or the encrypted local store, never from `appsettings.json`, and platform tokens travel in the `Authorization` header.
- Known limitations are listed in [docs/security.md](../security.md#known-limitations).

## Demo mode

```powershell
./publish/meeza-marketing-mcp dashboard --demo
```

Opens the dashboard with one invented project, "Puzzle Quest (demo)", and about 60 days of sample data, labelled **Demo data** on every page. It runs on a temporary database, reads no credentials and contacts no account. Saving credentials and syncing are turned off. Demo mode covers the dashboard only: the MCP tools Claude calls always use your real connections.

## Requirements

- .NET 9 SDK to build and publish (the published program needs the .NET 9 runtime, which the SDK includes).
- Claude Code or Claude Desktop.
- Credentials for at least one platform you use.
- The encrypted secret store (dashboard **Save**, `secrets set`) is available on Windows only. On macOS and Linux, supply credentials as environment variables.
- **[confirm]** Platform statement. What is known: the unit tests run on Windows locally and on Ubuntu in CI; `dotnet publish` produces `meeza-marketing-mcp.exe` on Windows and `meeza-marketing-mcp` for Linux and macOS. The server has not been run end to end on macOS or Linux as part of this release preparation.

## Getting started

```powershell
git clone https://github.com/azeemullah67/meeza-marketing-mcp.git
cd meeza-marketing-mcp
dotnet publish src/Meeza.MarketingMcp -c Release -o publish
./publish/meeza-marketing-mcp check
claude mcp add meeza-marketing -- "C:\path\to\publish\meeza-marketing-mcp.exe"
```

See the [README](../../README.md) for adding credentials and for Claude Desktop.

## Before publishing: checklist for the maintainer

- [ ] Review and push the release branch; wait for CI (it runs the file-permission tests on Linux, which could not be run locally).
- [ ] Resolve the **[confirm]** item above.
- [ ] Create the tag `v1.0.0` and the GitHub release from these notes.
- [ ] Add the repository topics below.

## Suggested GitHub topics

Add these by hand in the repository's **About** settings (gear icon → Topics). They cannot be set from a commit.

```text
mcp
mcp-server
model-context-protocol
claude
meta-ads
google-ads
google-analytics
google-play
mobile-marketing
dotnet
csharp
```
