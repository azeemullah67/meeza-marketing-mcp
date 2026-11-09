# Contributing

Thank you for helping improve Meeza Marketing MCP.

## Ground rules

1. **Read-only.** Don't add tools or client methods that create, modify, pause or delete anything on an ad platform or in Play Console. `ReadOnlyGuaranteeTests` enforces this for tool definitions, and reviewers will reject mutate code paths.
2. **No secrets in the repository**, including test fixtures. Use obviously fake values.
3. **Unavailable means null.** Never default a missing metric to 0.
4. **Never mix currencies or attribution sources** in a total.
5. **Error messages must be redacted.** Throw `PlatformApiException`, which redacts automatically.

## Development

```powershell
dotnet build
dotnet test
```

Unit tests must not call the network. Put parsing logic in pure static mapping or report classes so it can be tested with JSON or CSV fixtures.

## Pull requests

- Keep changes focused, and add tests for new mappings or calculations.
- Update the relevant `docs/*-setup.md` when you add settings or permissions.
- Describe any API version or permission requirement changes.
