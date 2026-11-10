# Adjust setup

[Adjust](https://www.adjust.com) is a mobile measurement partner (MMP). It attributes each install to exactly one
network, campaign, ad group and creative, across Meta, Google Ads and every other source. Connecting it adds:

| Tool | What it answers |
|---|---|
| `adjust_list_apps` | Which Adjust apps the token can read (app token and name) |
| `adjust_get_performance` | Installs, clicks, impressions, sessions, cost, CPI, CTR, IPM and revenue by network, campaign, ad group, creative, country, platform or day |
| `adjust_get_retention` | D1 / D3 / D7 / D14 / D30 install-cohort retention, overall or per network, campaign, ad group, creative, country or platform |

In the dashboard, a project with a linked Adjust app also gets:

- **Retention & cohorts**: a second row of segments, *All users / Network / Campaign / Country / Platform (Adjust)*.
  A project with Adjust but no GA4 uses Adjust for the standard segments too.
- **Campaigns**: D1 and D7 per campaign for **Meta and Google Ads**, matched by the network's campaign id. GA4 can
  only match Google Ads campaigns, by name.
- **Countries & attribution**: Adjust installs (all, Meta, Google, organic) next to the platform and GA4 counts.

Access is read-only: the server only calls the Adjust Report Service API with `GET` requests.

## Steps

1. **Copy your API token.** In Adjust, select the settings icon (bottom left) → **Account settings** → **My profile**.
   The API token is under **User details**. If your company uses SSO, the token isn't shown there: ask your Adjust
   representative or support@adjust.com.
2. Open the dashboard → **Data sources** → **Adjust**, paste the token and press **Save**. The token is stored in the
   encrypted local store (or set `ADJUST_API_TOKEN` as an environment variable).
3. **Optional:** set **App tokens** (`ADJUST_APP_TOKENS`) to limit access to specific apps, and **Report currency**
   (`ADJUST_CURRENCY`, default `USD`).
4. Press **Test connection**. It lists the apps the token can see.
5. Open **Project settings** for your game, tick its Adjust app under **Data sources** and save. The first sync
   loads the project's history; after that Adjust syncs with the other sources.

The token has the same access as the Adjust user it belongs to. Resetting the token in Adjust revokes it here
immediately; save the new one in Data sources.

## Reading the numbers

- **Installs** are Adjust-attributed with *first attribution* (the source that won the install), deduplicated across
  networks. They usually differ from what Meta and Google Ads claim for themselves, and from GA4 `first_open`. Compare
  them; never add them up.
- **Cost** is the spend networks report to Adjust, converted by Adjust into the report currency. It is the same money
  as your Meta / Google Ads spend seen through Adjust, so it is never added to ad-platform spend. This is the only
  place amounts are converted, and every Adjust amount is labelled with its currency.
- **Revenue** in `adjust_get_performance` is revenue earned in the period from all users (by activity date), not
  cohort revenue, so revenue ÷ cost there is not ROAS.
- **Retention** = users of an install cohort active again on day N (Adjust `retained_users_dN`, counted in 24-hour
  periods after install) ÷ the cohort's installs. GA4 counts calendar days, so the two are close but not identical.
  Days a cohort has not fully reached are blank, never 0%.
- **Dates** are UTC days. Today is incomplete, and the last few days can still change as late events and cost arrive;
  every sync re-reads the last 33 days.

## Troubleshooting

| Message | Fix |
|---|---|
| *Check the Adjust API token* (401) | The token is wrong or was reset. Copy it again from My profile. |
| *no permission to view that app's data* (403) | The Adjust user behind the token can't see that app. Ask an Adjust admin for access. |
| *it is not in ADJUST_APP_TOKENS* | The app isn't in your allow-list. Add its token or clear the list. |
| *Adjust is busy or the query took too long* (503 / 504) | Try again; the sync uses 14-day windows to stay within Adjust's limits. |
| *Adjust retention by … unavailable* (warning) | Adjust rejected one cohort split. Other data is still stored. |
