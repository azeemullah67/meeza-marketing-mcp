# Live dashboard

The server includes a local web dashboard. It shows the data that has been synchronized for each project, and it is where you add credentials and link accounts. Nothing in it can change an ad account.

![Overview page (synthetic demo data)](images/dashboard-overview.png)

*Overview: verdict, top actions, KPI tiles vs targets, prioritised marketing plan, findings that need attention.*

![Retention & cohorts page (synthetic demo data)](images/dashboard-retention.png)

*Retention & cohorts: D1/D3/D7 against the previous period, retention curve, D1 churn trend and the day 0–30 cohort heatmap, segmentable by country, platform, channel, campaign, ad group or creative.*

Both screenshots use an invented "Puzzle Quest" project, not real accounts.

## Opening it

| How | What happens |
|---|---|
| Ask Claude: *"open the marketing dashboard"* | Claude calls `open_dashboard`, which opens your browser and returns the link |
| `meeza-marketing-mcp dashboard` | Starts the dashboard (or reuses a running one) and opens your browser |
| `meeza-marketing-mcp dashboard --demo` | Shows one invented project so you can look around. See [Demo mode](#demo-mode). |
| `--no-browser` | Prints the link without opening a browser |

The dashboard starts automatically whenever Claude launches the MCP server. To turn that off, set `MEEZA_DASHBOARD=off` in the server's environment. The default address is `http://127.0.0.1:5057`; change the port with `MEEZA_DASHBOARD_PORT`.

## Demo mode

`meeza-marketing-mcp dashboard --demo` shows one invented project, "Puzzle Quest (demo)", with about 60 days of sample data. The header shows a **Demo data** label on every page.

- The project lives in a temporary database that is deleted when the dashboard stops. Your own projects are not shown and not touched.
- No credentials are read: environment variables, the secret store and `appsettings.json` are all ignored, so no account can be contacted.
- Saving credentials and **Sync now** are turned off.
- If your live dashboard already uses the port, set `MEEZA_DASHBOARD_PORT` to another port for the demo.

## Credentials (Data sources page)

Open **Data sources** in the sidebar. The **Credentials** section has one card per connection: Meta Ads, Google Ads, Google Play Console, Google Analytics 4, Adjust and Claude (for the optional AI analyst and market research). Each card has:

- **Fields:** paste tokens and IDs. Secrets are password fields; once saved they show **saved** and are never displayed or sent back to the browser. For a service account, paste the JSON key; the service account email to invite is then shown on the card.
- **Save:** validates the input (account IDs, package names, bucket, JSON key), stores it in the encrypted store (`%APPDATA%\MeezaMarketingMcp\secrets.json`, DPAPI), applies it immediately without a restart, and tests the connection.
- **Test connection:** lists the accounts or apps it can see, or the exact error.

Values set as environment variables take priority and are shown as locked. The encrypted store is available on Windows only; on macOS and Linux the page says so, and you set the values as environment variables instead. For Google Ads with a user sign-in instead of a service account, run `meeza-marketing-mcp auth google-ads` in a terminal.

Credentials are shared by all projects. Each project picks its own accounts in **Project settings**. The same page shows the sync status of the selected project and has a **Sync now** button.

## What it shows

The sidebar has these pages: Overview, Tasks, Campaigns, Creatives, Countries & attribution, Funnel, Retention & cohorts, Budget & forecast, KPI targets, AI analyst, Market research, Data sources and Project settings. [platform.md](platform.md) describes what each page computes and the rules behind the numbers.

The header has the date range (7, 14, 30 or 90 days, month to date, or custom), filters (country, ad platform, campaign, GA4 channel), a freshness indicator for the selected project and **Sync now**.

Totals are always for a single currency.

## How fresh is the data?

- Every page is computed from the local store, not from live API calls. A background scheduler checks every 5 minutes and syncs each project at its own interval (60 minutes by default). **Sync now** starts a sync immediately.
- The header and every KPI card show how fresh each source is.
- Today's numbers are incomplete on every platform.

## Security

- The dashboard listens on **127.0.0.1 only**, so it can't be reached from your network.
- Every request needs a private access key. The key is in the link that Claude or the CLI gives you. After the first visit it's stored as an `HttpOnly`, `SameSite=Strict` cookie, and the URL is cleaned.
- The key is kept in `%APPDATA%\MeezaMarketingMcp\dashboard.key` (on macOS/Linux, `~/.config/MeezaMarketingMcp/dashboard.key`), readable by your user only. Run `meeza-marketing-mcp dashboard rotate-key` (or delete that file) to revoke every existing link and browser session; running instances notice on their next request, no restart needed.
- When the dashboard port is already taken, the CLI and the `open_dashboard` tool first check that the listener really is your Meeza dashboard (a challenge only the running instance can answer). Another program on that port gets no link; the message tells you to set `MEEZA_DASHBOARD_PORT`. See [security.md](security.md).
- Requests with a foreign `Host` header are rejected, which blocks DNS-rebinding attacks.
- Changes are accepted only as a `POST` to `/api/settings` or `/api/v2` with a same-origin `Origin` header, a JSON body and a per-process anti-forgery token. They change local configuration (credentials, projects, targets, task status) or start a sync or an AI request. Every other method is rejected.
- A Content-Security-Policy blocks external scripts, frames and form posts, and the page loads no third-party code. Images may load over `https` (creative thumbnails).
- Ad names, creative text and AI output are HTML-escaped before display, and links and images are limited to `http(s)` URLs.
