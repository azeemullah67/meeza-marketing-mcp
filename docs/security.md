# Security notes

What the server protects, how, and what it does not do. For setup see the platform-specific guides in this folder.

## Model

- **Read-only by construction.** The HTTP clients only issue GET (Meta, Play, Adjust) or report queries (Google Ads, GA4, Play vitals). Nothing can change a campaign, budget or bid. Every MCP tool except `sync_project_data` is annotated read-only; a test checks those annotations for every tool class and fails if a tool name suggests a write. `sync_project_data` is not annotated read-only because it refreshes the local copy of a project's data: it reads from the platforms and writes only to the local database. Annotations describe tools to the client; the absence of write calls in the clients is what keeps the server read-only.
- **Local only.** The dashboard binds to 127.0.0.1, rejects foreign `Host` headers (DNS rebinding), requires a per-user access key, and accepts writes (local configuration, projects, targets, sync, AI requests) only as same-origin JSON POSTs carrying the anti-forgery token.
- **Secrets stay on the machine.** Credentials live in the encrypted local secret store (`%APPDATA%\MeezaMarketingMcp\secrets.json`, DPAPI) or environment variables. They are never written to the reporting database, the logs or tool output; error text passes through a redactor that masks token formats, OAuth parameters and PEM blocks.

## Account scoping (allow-lists)

`META_AD_ACCOUNT_IDS`, `GOOGLE_ADS_CUSTOMER_IDS`, `GA4_PROPERTY_IDS`, `ADJUST_APP_TOKENS` and `PLAY_PACKAGE_NAMES` limit what the server may read even when the credential can see more (typical for agency tokens). They are enforced:

- in every live tool (`ResolveAccountsAsync` / `ResolvePropertiesAsync` / `ResolveAppsAsync`),
- in the synchronizer: GA4 and Adjust sync go through the same authorization gate as the tools (`AnalyticsService.RunReportAsync`, `AdjustService.RunReportAsync`), and Meta / Google Ads sync resolve the account through the platform adapter,
- when a project connection is saved: the dashboard refuses to link an account outside the allow-list (HTTP 403 with the reason, nothing stored), so an excluded account can never be scheduled for sync,
- when GA4 properties are listed: properties outside `GA4_PROPERTY_IDS` are dropped before their data streams are looked up, so no request is made about an excluded property,
- for Meta child objects: a campaign or ad set passed to `meta_get_adsets` / `meta_get_ads` must be a plain numeric ID and must belong to the resolved account. The server fetches the child's `account_id` first and fails closed if it is missing or different.

The ownership check itself reads the `id` and `account_id` of the campaign or ad set that was named; nothing from it is returned when the check fails.

**Narrowing an allow-list later.** Data already synced for an account that is no longer allowed stays in the local database until the connection is removed from the project (Project settings → untick the account, or delete the project). Removing the connection deletes that account's rows. The dashboard shows the account as "not visible with current credentials" so it is obvious which connection to remove.

## Dashboard access key

- `%APPDATA%\MeezaMarketingMcp\dashboard.key` is user-private (0600 / explicit ACL). The link printed by `open_dashboard` and `meeza-marketing-mcp dashboard` carries it once; the browser then holds an HttpOnly, SameSite=Strict cookie.
- **Revocation is immediate.** The key file is re-checked on every request. `meeza-marketing-mcp dashboard rotate-key` replaces it; deleting the file has the same effect. Old links and cookies stop working in every running instance on their next request, no restart needed. `rotate-key` prints the new link only when the dashboard on the port passes the identity check below; otherwise it tells you to start the dashboard.
- **Unreadable key file fails closed.** If the key file exists but cannot be read (for example while another process is replacing it), a running instance discards the key it had loaded from the file instead of continuing to accept it. Sessions resume when the file is readable again. A key file found readable by others (an older version, a restored backup) is restricted to the user when it is loaded.
- **Port reuse requires proof.** Before a link is handed out for an already-running dashboard, the caller challenges the listener on the port with a nonce and expects an HMAC computed with a random token the real instance wrote to a user-private file (`dashboard-<port>.instance`, one per port so a second instance never overwrites the first's record). A different program squatting on the port cannot answer, and no link is issued ("Port 5057 is in use by a program that is not this user's Meeza dashboard"). Set `MEEZA_DASHBOARD_PORT` to move.

## Data directory permissions

`%APPDATA%\MeezaMarketingMcp` (or `~/.config/MeezaMarketingMcp`) is created with user-only permissions: mode 0700 on Unix, an explicit ACL (current user + SYSTEM, inheritance disabled) on Windows. The secret store, dashboard key, instance file and the SQLite database with its WAL/SHM side files are user-only (0600 / inherited ACL). A custom `MEEZA_DB_PATH` outside that directory gets private *files*, but the surrounding directory's permissions are yours to manage. Full-disk encryption and encrypted backups are separate decisions: DPAPI protects the secret store, not the reporting database.

## Outbound requests

- Meta pagination links are followed only when they are absolute `https://graph.facebook.com/...` URLs on the default port with no user-info. Anything else (including `graph.facebook.com.attacker.example` or `graph.facebook.com@attacker.example`) aborts the request with an error instead of being followed or silently treated as "no more pages". The same check runs immediately before the bearer token is attached.
- Automatic HTTP redirects are disabled for all provider clients so a credential is never re-sent to a redirect target.

## AI features

- The analyst runs only when an Anthropic API key is configured or the Claude Code app is installed on this computer; research needs the API key. Both are project-scoped read computations over synced data plus web search.
- Through the Claude app, each question is a separate `claude -p` process in an empty temporary folder with `--tools ""`, `--strict-mcp-config`, `--setting-sources ""`, `--disable-slash-commands` and `--no-session-persistence`, so it can read nothing but the prompt and cannot run commands or change files. `ANTHROPIC_API_KEY` is removed from its environment so it uses your Claude sign-in. Demo mode never uses the Claude app.
- At most two research jobs run at once and one per project; a running job can be cancelled from the dashboard. Analyst conversations are bounded (10 tool rounds, 16k output tokens per call).
- Ad names, ad copy and web pages are untrusted model input. Content is kept separate from instructions and the model has no tool that reaches credentials or ad-account writes. This limits impact; it is not a proof of resistance to prompt injection.

## Demo mode

`meeza-marketing-mcp dashboard --demo` runs on a temporary database that holds one invented project, and with a settings resolver that returns nothing. No environment variable, stored secret or `appsettings.json` value is read, so no platform is configured and no request can be sent to a provider. Saving credentials and starting a sync are refused in demo mode, the page labels the data as demo data, and the temporary database is deleted when the dashboard stops.

## Known limitations

- The identity check for an occupied dashboard port protects against a program that cannot read the user's files. A program running as the same user can read the key file directly. There is also a short gap between the check and the browser opening the link.
- The dashboard page is a single HTML file with inline script, so its Content-Security-Policy allows `'unsafe-inline'` for scripts and styles. All provider and AI text is HTML-escaped before insertion and external links/images are limited to `http(s)` URLs; moving to nonce-based CSP is planned.
- OAuth scopes for Google Ads and Play are the platforms' read scopes but still broad; keep the service account / user at a read-only role on the provider side.
- Windows ACL handling and Unix modes are applied best-effort: a file system without permission support does not stop the application.
- The encrypted secret store uses Windows DPAPI and is available on Windows only. On Linux and macOS, credentials come from environment variables or configuration, and the dashboard cannot save them.

## CI

Every push runs, on both Ubuntu and Windows, a locked NuGet restore (`packages.lock.json` must match), build with warnings as errors, the test suite and `dotnet list package --vulnerable --include-transitive`, plus a gitleaks secret scan (Ubuntu only) of the pushed commits; the audit also runs weekly.
