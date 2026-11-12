# Demo recording checklist (30–60 seconds)

For a short screen capture of Meeza Marketing MCP. Nothing here records or uploads anything; it is the list to follow when you record.

## Before you record

- [ ] Close or hide anything personal: other browser tabs, bookmarks bar, notifications, terminal history.
- [ ] Use a clean terminal in the repository folder, with a large font.
- [ ] Decide which of the two endings below you will use. They differ in what data appears on screen.

## Commands

Build once, before recording:

```powershell
git clone https://github.com/azeemullah67/meeza-marketing-mcp.git
cd meeza-marketing-mcp
dotnet publish src/Meeza.MarketingMcp -c Release -o publish
```

On camera:

```powershell
./publish/meeza-marketing-mcp dashboard --demo
```

The browser opens the dashboard with the invented "Puzzle Quest (demo)" project. The header shows **Demo data**. If port 5057 is taken by your own dashboard, run this first: `$env:MEEZA_DASHBOARD_PORT = "5099"`.

## Shot list

1. **0–5 s.** Terminal: run the `dashboard --demo` command. The line "running with DEMO DATA (no accounts are contacted)" appears.
2. **5–20 s.** Overview: the verdict, the KPI tiles (spend, installs, CPI) and the marketing plan.
3. **20–35 s.** Campaigns: sort or scan for the campaign flagged as spending without installs.
4. **35–45 s.** Retention & cohorts: the retention curve and the cohort heatmap.
5. **45–60 s.** The question, using one of the endings below.

## The question

> Which campaigns are spending money without installs?

### Ending A: answer on the demo dashboard (no real data on screen)

Stay in the demo. Open **Overview → Needs attention**, which lists the campaign that spent without tracked installs, then **Campaigns**, where the same campaign carries the wasted-spend flag. Show the question as a caption.

### Ending B: ask Claude (real data on screen)

Ask the question in Claude Code or Claude Desktop with the server connected. Claude answers from the MCP tools, and **the MCP tools always use your real connected accounts; demo mode covers the dashboard only.** Only use this ending with an account whose campaign names and spend you are willing to publish, or blur them afterwards.

## After recording

- [ ] Stop the dashboard with Ctrl+C. The temporary demo database is deleted.
- [ ] Watch the capture once for account names, IDs, e-mail addresses and the access key in the address bar (the key is removed from the URL after the first load, but check the first frame).
