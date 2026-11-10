namespace Meeza.MarketingMcp.Storage;

/// <summary>
/// Forward-only schema migrations. Never edit an applied migration: add a new version instead.
/// Conventions: dates are account/property-local calendar days as yyyy-MM-dd; timestamps are UTC ISO-8601;
/// NULL means "not available / not tracked", never zero.
/// </summary>
public static class Migrations
{
    public static readonly IReadOnlyList<(int Version, string Sql)> All =
    [
        (1, """
        CREATE TABLE projects(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            genre TEXT, platforms TEXT, android_store_url TEXT, ios_store_url TEXT,
            target_countries TEXT, monetization TEXT, currency TEXT, timezone TEXT,
            launch_stage TEXT, objectives TEXT,
            tutorial_event TEXT, purchase_event TEXT,
            backfill_days INTEGER NOT NULL DEFAULT 90,
            sync_interval_minutes INTEGER NOT NULL DEFAULT 60,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
            archived INTEGER NOT NULL DEFAULT 0
        );

        -- Which ad accounts / GA4 properties / Play apps belong to a project. Credentials stay in the encrypted
        -- secret store; a project only selects which accounts its data comes from.
        CREATE TABLE project_connections(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            integration TEXT NOT NULL,
            external_id TEXT NOT NULL,
            display_name TEXT, currency TEXT, timezone TEXT,
            added_at TEXT NOT NULL,
            PRIMARY KEY(project_id, integration, external_id)
        );

        -- Ad-level daily facts (Google Ads ad_group_ad, Meta ad insights). Campaign / ad set totals are sums of these.
        CREATE TABLE ad_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            platform TEXT NOT NULL, account_id TEXT NOT NULL, date TEXT NOT NULL,
            campaign_id TEXT NOT NULL, campaign_name TEXT, campaign_status TEXT, channel TEXT,
            adset_id TEXT NOT NULL, adset_name TEXT, adset_status TEXT,
            ad_id TEXT NOT NULL, ad_name TEXT, ad_status TEXT,
            currency TEXT NOT NULL,
            spend REAL, impressions INTEGER, reach INTEGER, clicks INTEGER,
            conversions REAL, installs REAL, install_source TEXT, conversion_value REAL,
            video_views INTEGER, video_p25 INTEGER, video_p100 INTEGER, thruplays INTEGER,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, platform, account_id, date, adset_id, ad_id)
        );
        CREATE INDEX ix_ad_daily_date ON ad_daily(project_id, date);

        -- Campaign x country daily facts.
        CREATE TABLE campaign_country_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            platform TEXT NOT NULL, account_id TEXT NOT NULL, date TEXT NOT NULL,
            campaign_id TEXT NOT NULL, campaign_name TEXT, country TEXT NOT NULL,
            currency TEXT NOT NULL,
            spend REAL, impressions INTEGER, clicks INTEGER, conversions REAL, installs REAL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, platform, account_id, date, campaign_id, country)
        );
        CREATE INDEX ix_ccd_date ON campaign_country_daily(project_id, date);

        -- Per-asset daily facts for asset-based ads (Google App campaigns). Overlapping: never sum into totals.
        CREATE TABLE asset_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            platform TEXT NOT NULL, account_id TEXT NOT NULL, date TEXT NOT NULL,
            campaign_id TEXT, adset_id TEXT NOT NULL, ad_id TEXT NOT NULL, asset_id TEXT NOT NULL, field_type TEXT NOT NULL,
            currency TEXT NOT NULL,
            spend REAL, impressions INTEGER, clicks INTEGER, installs REAL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, platform, account_id, date, adset_id, ad_id, asset_id, field_type)
        );

        -- Creative metadata (one row per Meta ad creative or Google asset).
        CREATE TABLE creatives(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            platform TEXT NOT NULL, account_id TEXT NOT NULL, creative_key TEXT NOT NULL,
            ad_id TEXT, asset_id TEXT, name TEXT, format TEXT, field_type TEXT,
            preview_url TEXT, thumbnail_url TEXT, video_id TEXT, duration_seconds REAL,
            title TEXT, body TEXT, performance_label TEXT,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(project_id, platform, account_id, creative_key)
        );

        -- Reach is not additive across days or ads, so it is stored per fixed window as the platform reports it.
        CREATE TABLE reach_windows(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            platform TEXT NOT NULL, account_id TEXT NOT NULL,
            window_start TEXT NOT NULL, window_end TEXT NOT NULL,
            reach INTEGER, impressions INTEGER, frequency REAL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, platform, account_id, window_start, window_end)
        );

        CREATE TABLE ga4_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            property_id TEXT NOT NULL, date TEXT NOT NULL,
            active_users INTEGER, new_users INTEGER, active_7d INTEGER, active_28d INTEGER,
            sessions INTEGER, engagement_seconds REAL,
            total_revenue REAL, ad_revenue REAL, purchase_revenue REAL, purchasers INTEGER,
            currency TEXT,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, property_id, date)
        );

        -- GA4 daily users by one dimension (country, platform, first-user channel / source / campaign).
        CREATE TABLE ga4_dim_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            property_id TEXT NOT NULL, date TEXT NOT NULL, dimension TEXT NOT NULL, value TEXT NOT NULL,
            active_users INTEGER, new_users INTEGER, total_revenue REAL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, property_id, date, dimension, value)
        );

        CREATE TABLE ga4_events_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            property_id TEXT NOT NULL, date TEXT NOT NULL, event_name TEXT NOT NULL,
            event_count INTEGER, users INTEGER,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, property_id, date, event_name)
        );

        -- Exact-day cohort retention: users of cohort_date active on day_n (GA4 cohort report, firstSessionDate cohorts).
        CREATE TABLE ga4_cohorts(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            property_id TEXT NOT NULL, segment_type TEXT NOT NULL, segment_value TEXT NOT NULL,
            cohort_date TEXT NOT NULL, day_n INTEGER NOT NULL,
            active_users INTEGER NOT NULL, cohort_users INTEGER NOT NULL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, property_id, segment_type, segment_value, cohort_date, day_n)
        );

        CREATE TABLE sync_runs(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            integration TEXT NOT NULL, external_id TEXT NOT NULL,
            kind TEXT NOT NULL, trigger TEXT NOT NULL,
            started_at TEXT NOT NULL, finished_at TEXT,
            status TEXT NOT NULL,
            window_start TEXT, window_end TEXT,
            records INTEGER, latest_data_date TEXT,
            error TEXT, warnings TEXT, duration_ms INTEGER
        );
        CREATE INDEX ix_sync_runs ON sync_runs(project_id, integration, external_id, id DESC);

        CREATE TABLE sync_state(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            integration TEXT NOT NULL, external_id TEXT NOT NULL,
            last_attempt_at TEXT, last_success_at TEXT, last_status TEXT, last_error TEXT,
            latest_data_date TEXT, backfilled_from TEXT,
            records_last INTEGER, duration_ms_last INTEGER,
            consecutive_failures INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(project_id, integration, external_id)
        );

        -- Cross-process lease so the MCP server and a terminal dashboard never sync the same account at once.
        CREATE TABLE sync_leases(lease_key TEXT PRIMARY KEY, owner TEXT NOT NULL, expires_at TEXT NOT NULL);

        CREATE TABLE kpi_targets(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            metric TEXT NOT NULL, scope_type TEXT NOT NULL DEFAULT 'all', scope_value TEXT NOT NULL DEFAULT '',
            target REAL NOT NULL, notes TEXT, updated_at TEXT NOT NULL,
            UNIQUE(project_id, metric, scope_type, scope_value)
        );

        CREATE TABLE analyst_messages(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            conversation_id TEXT NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL,
            evidence_json TEXT, created_at TEXT NOT NULL
        );

        CREATE TABLE research_reports(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            topic TEXT NOT NULL, question TEXT NOT NULL, status TEXT NOT NULL,
            content TEXT, citations_json TEXT, error TEXT,
            created_at TEXT NOT NULL, finished_at TEXT
        );
        """),
        (2, """
        -- Adjust (MMP) daily totals per app. Installs are Adjust-attributed (first attribution, deduplicated across
        -- networks); cost and revenue are converted by Adjust into `currency`. Dates are UTC days.
        CREATE TABLE adjust_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            app_token TEXT NOT NULL, date TEXT NOT NULL,
            installs INTEGER, clicks INTEGER, impressions INTEGER, sessions INTEGER, daus INTEGER,
            cost REAL, revenue REAL, ad_revenue REAL, all_revenue REAL,
            currency TEXT NOT NULL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, app_token, date)
        );

        -- Adjust daily facts by network x campaign x country x platform. campaign_id is the network's own campaign id
        -- (Meta / Google Ads campaign id) or '' when unknown, e.g. organic.
        CREATE TABLE adjust_campaign_daily(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            app_token TEXT NOT NULL, date TEXT NOT NULL,
            network TEXT NOT NULL, campaign_id TEXT NOT NULL, campaign_name TEXT NOT NULL, country TEXT NOT NULL, platform TEXT NOT NULL,
            installs INTEGER, clicks INTEGER, impressions INTEGER, sessions INTEGER,
            cost REAL, all_revenue REAL,
            currency TEXT NOT NULL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, app_token, date, network, campaign_id, campaign_name, country, platform)
        );
        CREATE INDEX ix_adjust_campaign_date ON adjust_campaign_daily(project_id, date);

        -- Adjust install cohorts: users of cohort_date (install day) active on day_n (retained_users_dN) and the
        -- cohort's installs. Only days the cohort has fully reached are stored.
        CREATE TABLE adjust_cohorts(
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            app_token TEXT NOT NULL, segment_type TEXT NOT NULL, segment_value TEXT NOT NULL,
            cohort_date TEXT NOT NULL, day_n INTEGER NOT NULL,
            active_users INTEGER NOT NULL, cohort_users INTEGER NOT NULL,
            synced_at TEXT NOT NULL,
            PRIMARY KEY(project_id, app_token, segment_type, segment_value, cohort_date, day_n)
        );
        """),
        (3, """
        -- First-time player (NUX) journey: JSON array of {"e": GA4 event name, "l": optional label}, in journey order.
        ALTER TABLE projects ADD COLUMN nux_events TEXT;
        """),
        (4, """
        -- Result of the automatic check that runs after every sync: the findings for the last 30 complete days.
        -- One row per project (the latest check); the history of what was found lives in the tasks below.
        CREATE TABLE project_checks(
            project_id TEXT PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
            analyzed_at TEXT NOT NULL, trigger TEXT NOT NULL,
            range_start TEXT NOT NULL, range_end TEXT NOT NULL,
            findings_json TEXT NOT NULL
        );

        -- Per-project to-do list. source='analysis' rows come from a problem the check found (finding_key is the
        -- problem's stable id, finding_json its evidence); source='manual' rows are written by the user.
        -- status: open | in_progress | done | dismissed. priority: high | medium | low.
        -- detected = 1 while the latest check still finds the problem. closed_by: user | analysis (the check closed
        -- the task because the problem is gone).
        CREATE TABLE tasks(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            project_id TEXT NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
            title TEXT NOT NULL, details TEXT,
            status TEXT NOT NULL DEFAULT 'open',
            priority TEXT NOT NULL DEFAULT 'medium',
            source TEXT NOT NULL DEFAULT 'manual',
            finding_key TEXT, category TEXT, finding_json TEXT,
            detected INTEGER NOT NULL DEFAULT 0,
            closed_by TEXT, due_date TEXT,
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL, completed_at TEXT, last_seen_at TEXT
        );
        CREATE INDEX ix_tasks_status ON tasks(project_id, status);
        CREATE INDEX ix_tasks_finding ON tasks(project_id, finding_key);
        """),
        (5, """
        -- GA4 event that marks the start of the tutorial (funnel step). NULL = tutorial_begin.
        ALTER TABLE projects ADD COLUMN tutorial_start_event TEXT;
        """),
    ];
}
