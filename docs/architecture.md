# Architecture

```text
Claude (Desktop / Code)
        │  MCP over stdio (JSON-RPC)
        ▼
Meeza.MarketingMcp ───────────── host, CLI, unified tools, PlatformRegistry
        │
        ├── Meeza.Marketing.Meta ─────────► Meta Marketing API (Graph API, GET only)
        ├── Meeza.Marketing.GoogleAds ────► Google Ads API (REST SearchStream / GAQL)
        ├── Meeza.Marketing.PlayConsole ──► Play Developer Reporting API, Play Developer API (reviews),
        │                                   Cloud Storage (Play Console bulk report exports)
        ├── Meeza.Marketing.Analytics ────► GA4 Data API / Admin API (read-only scope)
        ├── Meeza.Marketing.Adjust ───────► Adjust Report Service API (GET only)
        └── Meeza.Marketing.Shared ─────── models, metric math, aggregation, secrets, OAuth, retry, redaction
```

## Layers

| Layer | Responsibility |
|---|---|
| `*Client` | HTTP transport for one platform: authentication, paging, error parsing, rate-limit handling. Returns raw `JsonElement`s. |
| `*Mapping` / `*Reports` | Pure functions that convert raw payloads into shared models. Fully unit-tested without network access. |
| `*Platform` / `*Service` | Account and app resolution, allow-list enforcement, caching (10 minutes), and the `IAdPlatform` implementation. |
| `*Tools` | MCP tool definitions. Input validation and response envelopes only. |
| `UnifiedReportingTools` | Fan-out across platforms through `PlatformRegistry`, with per-platform error isolation. |

## Normalized model

`CampaignPerformance` (in `Shared/Models`) is the common row:

- `Spend` stays in the account's `Currency`. Aggregation always groups by currency.
- `Installs` plus `InstallSource` record who attributed the install (`meta_attributed:mobile_app_install`, `google_ads_attributed:DOWNLOAD_conversions`). Play store installs are reported separately and never merged into this model.
- Derived ratios (CTR, CPC, CPM, CPI, cost per conversion) are recomputed from raw counts by `MetricMath`, so every platform uses the same formulas. Ratios are `null` when the denominator is `null` or zero.
- `TimeZone` records the account's reporting time zone.

## Extending with a new platform

1. Add a `Meeza.Marketing.<Platform>` project that references `Shared`.
2. Implement `<Platform>Options.FromSettings`, a client, mapping functions, and (for ad networks) `IAdPlatform`.
3. Add `[McpServerToolType]` tools whose names start with the platform prefix, marked `ReadOnly = true, Destructive = false`.
4. Register the platform in `ServiceRegistration.AddMeezaMarketing` and `Program.cs`, and add it to `PlatformRegistry.AdPlatforms`.
5. Add any new setting names to `SettingKeys`. Mark secrets in `SettingKeys.Secrets`.

The Firebase / GA4 module (roadmap) would follow the same pattern, using the GA4 Data API.

## Hosted version (future)

The tool layer has no dependency on stdio. A hosted version would:

- switch to the Streamable HTTP transport (`ModelContextProtocol.AspNetCore`) with authenticated endpoints;
- replace `SettingsResolver` with a per-tenant credential provider backed by encrypted storage;
- create the platform clients per tenant (scoped DI) so tokens and caches can't cross tenants.
