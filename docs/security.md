# Security notes

What the server protects, how, and what it does not do. For setup see the platform-specific guides in this folder.

## Model

- **Read-only by construction.** Every MCP tool is annotated read-only and a test fails the build if a tool name suggests a write. The HTTP clients only issue GET (Meta, Play) or report queries (Google Ads, GA4). Nothing can change a campaign, budget or bid.
- **Local only.** The dashboard binds to 127.0.0.1, rejects foreign `Host` headers (DNS rebinding), requires a per-user access key, and accepts writes (local configuration, projects, targets, sync, AI requests) only as same-origin JSON POSTs carrying the anti-forgery token.
- **Secrets stay on the machine.** Credentials live in the encrypted local secret store (`%APPDATA%\MeezaMarketingMcp\secrets.json`, DPAPI) or environment variables. They are never written to the reporting database, the logs or tool output; error text passes through a redactor that masks token formats, OAuth parameters and PEM blocks.

## Account scoping (allow-lists)

`META_AD_ACCOUNT_IDS`, `GOOGLE_ADS_CUSTOMER_IDS`, `GA4_PROPERTY_IDS` and `PLAY_PACKAGE_NAMES` limit what the server may read even when the credential can see more (typical for agency tokens). They are enforced:

- in every live tool (`ResolveAccountsAsync` / `ResolvePropertiesAsync`),
- in the synchronizer: GA4 sync goes through the same authorization gate as the tools (`AnalyticsService.RunReportAsync`), and Meta / Google Ads sync resolve the account through the platform adapter,
- when a project connection is saved: the dashboard refuses to link an account outside the allow-list, so an excluded account can never be scheduled for sync,
- for Meta child objects: a campaign or ad set passed to `meta_get_adsets` / `meta_get_ads` must belong to the resolved account. The server fetches the child's `account_id` first and fails closed if it is missing or different.

**Narrowing an allow-list later.** Data already synced for an account that is no longer allowed stays in the local database until the connection is removed from the project (Project settings → untick the account, or delete the project). Removing the connection deletes that account's rows. The dashboard shows the account as "not visible with current credentials" so it is obvious which connection to remove.

## Dashboard access key

- `%APPDATA%\MeezaMarketingMcp\dashboard.key` is user-private (0600 / explicit ACL). The link printed by `open_dashboard` and `meeza-marketing-mcp dashboard` carries it once; the browser then holds an HttpOnly, SameSite=Strict cookie.
- **Revocation is immediate.** The key file is re-checked on every request. `meeza-marketing-mcp dashboard rotate-key` replaces it; deleting the file has the same effect. Old links and cookies stop working in every running instance on their next request, no restart needed.
- **Port reuse requires proof.** Before a link is handed out for an already-running dashboard, the caller challenges the listener on the port with a nonce and expects an HMAC computed with a random token the real instance wrote to a user-private file (`dashboard.instance`). A different program squatting on the port cannot answer, and no link is issued ("Port 5057 is in use by a program that is not this user's Meeza dashboard"). Set `MEEZA_DASHBOARD_PORT` to move.

## Data directory permissions

`%APPDATA%\MeezaMarketingMcp` (or `~/.config/MeezaMarketingMcp`) is created with user-only permissions: mode 0700 on Unix, an explicit ACL (current user + SYSTEM, inheritance disabled) on Windows. The secret store, dashboard key, instance file and the SQLite database with its WAL/SHM side files are user-only (0600 / inherited ACL). A custom `MEEZA_DB_PATH` outside that directory gets private *files*, but the surrounding directory's permissions are yours to manage. Full-disk encryption and encrypted backups are separate decisions: DPAPI protects the secret store, not the reporting database.

## Outbound requests

- Meta pagination links are followed only when they are absolute `https://graph.facebook.com/...` URLs on the default port with no user-info. Anything else (including `graph.facebook.com.attacker.example` or `graph.facebook.com@attacker.example`) aborts the request with an error instead of being followed or silently treated as "no more pages". The same check runs immediately before the bearer token is attached.
- Automatic HTTP redirects are disabled for all provider clients so a credential is never re-sent to a redirect target.

## AI features

- The analyst and research run only when an Anthropic API key is configured; both are project-scoped read computations over synced data plus web search.
- At most two research jobs run at once and one per project; a running job can be cancelled from the dashboard. Analyst conversations are bounded (10 tool rounds, 16k output tokens per call).
- Ad names, ad copy and web pages are untrusted model input. Content is kept separate from instructions and the model has no tool that reaches credentials or ad-account writes. This limits impact; it is not a proof of resistance to prompt injection.

## Known limitations

- The dashboard page is a single HTML file with inline script, so its Content-Security-Policy allows `'unsafe-inline'` for scripts and styles. All provider and AI text is HTML-escaped before insertion and external links/images are limited to `http(s)` URLs; moving to nonce-based CSP is planned.
- OAuth scopes for Google Ads and Play are the platforms' read scopes but still broad; keep the service account / user at a read-only role on the provider side.
- Windows ACL handling and Unix modes are applied best-effort: a file system without permission support does not stop the application.

## CI

Every push runs a locked NuGet restore (`packages.lock.json` must match), build with warnings as errors, the test suite, `dotnet list package --vulnerable --include-transitive`, and a gitleaks secret scan; the audit also runs weekly.
