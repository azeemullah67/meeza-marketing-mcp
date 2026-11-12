# Security policy

## Reporting a vulnerability

Please report security problems **privately** through GitHub:

1. Go to this repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Describe the issue, the affected version or commit, and steps to reproduce it.

Only the maintainer can see the report. Please don't open a public issue, pull request or discussion about a vulnerability until a fix is released.

Useful details to include:

- what an attacker could do (for example read a credential, reach the dashboard from another origin, or read an ad account outside the allow-list),
- the operating system and how you ran the server (MCP client, `dashboard`, demo mode),
- a minimal proof of concept that uses **synthetic data only**.

Please don't test against ad accounts, analytics properties or Claude accounts you don't own, and never include real tokens or keys in a report. If a credential was exposed, revoke it with its provider first.

## What to expect

This is a project maintained by one person, so response times are best effort. The maintainer aims to acknowledge a report within a week, keep you updated while it is investigated, and credit you in the release notes when the fix ships unless you'd rather stay anonymous.

## Supported versions

Fixes go into the latest release and the `main` branch. Older releases don't get updates.

## Scope

In scope: the code in this repository, including the MCP server, the local dashboard and its API, the local secret store, the sync engine and the CI workflow.

Out of scope: the ad and analytics platforms themselves (Meta, Google Ads, Google Play, GA4, Adjust), the Claude API and the Claude Code app, and problems that require an attacker who already controls your user account on the computer.

For how the server handles credentials, account scoping and the local dashboard, and for its known limitations, see [docs/security.md](docs/security.md).
