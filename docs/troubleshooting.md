# Troubleshooting

Always start with:

```powershell
meeza-marketing-mcp check
meeza-marketing-mcp secrets list
```

`secrets list` shows where each setting comes from (`environment`, `secret-store` or `appsettings`) with secrets masked. Environment variables take precedence over the secret store.

## Claude doesn't show the tools

- Use the **published executable**, not `dotnet run`. Build output on stdout breaks the protocol.
- Use an absolute path in the Claude configuration.
- Run the executable once by hand. It should wait silently for input, so press Ctrl+C to stop it. Any startup error is printed to stderr.
- Claude Desktop logs are in `%APPDATA%\Claude\logs` (Windows) or `~/Library/Logs/Claude` (macOS).

## Meta

| Error | Fix |
|---|---|
| `code 190` invalid or expired token | Generate a new token. Prefer a System User token. |
| `code 200` / `10` permission error | The token needs `ads_read`, and the system user must be assigned to the ad account. |
| `code 17` / `4` / `613` / `80004` rate limit | The server retries automatically. Request fewer accounts, a shorter range, or a higher level (campaign rather than ad). |
| Installs are always `null` | Install events aren't reported for the app. Set up the Meta SDK or an MMP integration. |
| "not in META_AD_ACCOUNT_IDS" | The account isn't in your allow-list. |

## Google Ads

| Error | Fix |
|---|---|
| `invalid_grant` | The refresh token was revoked or expired. Run `auth google-ads` again. Apps in *Testing* status get 7-day tokens. |
| `PERMISSION_DENIED` … developer token | The token isn't approved for this account or access level. Check API Center. |
| `PERMISSION_DENIED` … customer | Set `GOOGLE_ADS_LOGIN_CUSTOMER_ID` to the manager that owns the account. |
| `NOT_FOUND` on every call | `GOOGLE_ADS_API_VERSION` is probably sunset. Set it to a current version. |
| Installs are `null` | The account has no enabled `DOWNLOAD`-category conversion action. Link Google Play or Firebase. |

## Google Play

| Error | Fix |
|---|---|
| 403 on Reporting API | Invite the service account in Play Console with *View app information* and *View app quality information*. Changes can take up to 24 hours. |
| 403 on Cloud Storage | Grant *View app information and download bulk reports*. Check `PLAY_REPORTS_BUCKET`. |
| 403 "API has not been used in project" | Enable both Play APIs in the service account's Google Cloud project. |
| `missingMonths` in store installs | Play hasn't generated that month's export yet, or the app had no data. |
| Reviews look incomplete | The API only returns text reviews from the last 7 days. |

## Dashboard

| Problem | Fix |
|---|---|
| "Dashboard link required" | Open the link from Claude (`open_dashboard`) or from `meeza-marketing-mcp dashboard`; it contains your access key. |
| Port 5057 already in use | Another instance is probably serving it, so its link works. Otherwise set `MEEZA_DASHBOARD_PORT`. |
| Numbers do not change | Views are cached for 3 minutes. Click **Refresh**. |
| Old links stopped working | The key was rotated (`dashboard rotate-key`) or `dashboard.key` was deleted. Get a new link with `meeza-marketing-mcp dashboard` or the `open_dashboard` tool. |
| "Port 5057 is in use by a program that is not this user's Meeza dashboard" | Something else listens on the port, or a Meeza instance from another Windows user. No link is issued on purpose. Close that program or set `MEEZA_DASHBOARD_PORT` to a free port. |

## Logs

The server logs to **stderr** at Information level. Tokens are never logged, request URLs aren't logged, and all error messages pass through the redactor before reaching logs or Claude.
