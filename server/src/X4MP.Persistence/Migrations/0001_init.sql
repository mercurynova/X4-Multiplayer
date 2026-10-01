-- 0001_init: schema v1, CORE tables (server-design 2.8) plus journal, string_table, checkpoints (roadmap M0-14).
--
-- DEFERRED to later migrations (so their owners can finalise the design; do not add here):
--   Teams (server-design 2.13), task M1-T1:
--     teams, team_members, team_relations, team_assets
--   Economy (server-design 2.14), task M1-E1 (ledger core) and the economy tasks after it:
--     wallets, ledger_tx (+ ux_ledger_reverses, ix_ledger_tx_session_ts), ledger_entries (+ ix_entries_wallet),
--     the append-only triggers, economy_requests, loans (+ ix_loans_state), trade_offers
--     (+ ux_trade_entity_lock), economy_events (+ ix_econ_events)
--
-- The journal, string_table and checkpoints tables are NOT specified in server-design 2.8
-- (architecture 9 only names them: "journal + string table + checkpoint index"). Their columns below
-- are the M0-14 proposal; revise in a later migration when the save service (M1-12) lands.
--
-- Timestamps are ISO-8601 UTC text, as in the 2.8 schema.

CREATE TABLE schema_version (version INTEGER NOT NULL);

CREATE TABLE players (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  key_hash BLOB NOT NULL, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
  last_ip TEXT, total_seconds INTEGER NOT NULL DEFAULT 0, notes TEXT,
  is_muted INTEGER NOT NULL DEFAULT 0, mute_until TEXT);

CREATE TABLE bans (
  id INTEGER PRIMARY KEY, player_id INTEGER REFERENCES players(id),
  ip_cidr TEXT, reason TEXT NOT NULL, created_by TEXT NOT NULL,
  created_at TEXT NOT NULL, expires_at TEXT, revoked_at TEXT,
  CHECK (player_id IS NOT NULL OR ip_cidr IS NOT NULL));

CREATE TABLE saves (
  id INTEGER PRIMARY KEY, sha256 TEXT NOT NULL UNIQUE, size_bytes INTEGER NOT NULL,
  display_name TEXT NOT NULL, original_file_name TEXT, source TEXT NOT NULL, -- 'authority'|'admin-upload'
  uploaded_by TEXT, uploaded_at TEXT NOT NULL, game_version TEXT, mod_version TEXT,
  save_time TEXT, player_name TEXT, meta_json TEXT, pinned INTEGER NOT NULL DEFAULT 0);

CREATE TABLE sessions (
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, state TEXT NOT NULL,
  save_id INTEGER REFERENCES saves(id), current_save_id INTEGER REFERENCES saves(id),
  authority_player_id INTEGER REFERENCES players(id), settings_json TEXT NOT NULL,
  created_at TEXT NOT NULL, started_at TEXT, ended_at TEXT, end_reason TEXT);

CREATE TABLE session_players (
  session_id INTEGER REFERENCES sessions(id), player_id INTEGER REFERENCES players(id),
  joined_at TEXT NOT NULL, left_at TEXT, leave_reason TEXT, role TEXT NOT NULL);

CREATE TABLE session_events (
  id INTEGER PRIMARY KEY, session_id INTEGER NOT NULL, ts TEXT NOT NULL, server_seq INTEGER,
  type TEXT NOT NULL, player_id INTEGER, sector_id INTEGER, data_json TEXT);
CREATE INDEX ix_events_session_ts ON session_events(session_id, ts);

CREATE TABLE chat_messages (
  id INTEGER PRIMARY KEY, session_id INTEGER, ts TEXT NOT NULL, from_player_id INTEGER,
  from_admin TEXT, channel TEXT NOT NULL, text TEXT NOT NULL);

CREATE TABLE galaxy_cache (save_sha256 TEXT PRIMARY KEY, metadata_blob BLOB NOT NULL, created_at TEXT NOT NULL);

CREATE TABLE config_overrides (key TEXT PRIMARY KEY, value_json TEXT NOT NULL,
  updated_at TEXT NOT NULL, updated_by TEXT NOT NULL);

CREATE TABLE admin_users (id INTEGER PRIMARY KEY, username TEXT NOT NULL UNIQUE,
  pw_hash BLOB NOT NULL, pw_salt BLOB NOT NULL, pw_iter INTEGER NOT NULL,
  role TEXT NOT NULL, created_at TEXT NOT NULL, must_change INTEGER NOT NULL DEFAULT 0);

CREATE TABLE api_tokens (id INTEGER PRIMARY KEY, name TEXT NOT NULL, token_hash BLOB NOT NULL UNIQUE,
  role TEXT NOT NULL, created_at TEXT NOT NULL, last_used_at TEXT, revoked_at TEXT);

CREATE TABLE audit_log (id INTEGER PRIMARY KEY, ts TEXT NOT NULL, actor TEXT NOT NULL,
  action TEXT NOT NULL, target TEXT, data_json TEXT, remote_ip TEXT);

-- Checkpoint index: one row per authority save used as a replay base (architecture 5.5).
-- journal_seq is the server_seq of the SaveStarted marker; catch-up replays journal rows after it.
CREATE TABLE checkpoints (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  save_id INTEGER REFERENCES saves(id),
  save_sha256 TEXT,
  journal_seq INTEGER NOT NULL,
  game_time REAL,
  next_net_id INTEGER,
  ghosts_cleaned INTEGER NOT NULL DEFAULT 0,  -- only ghosts_cleaned=1 saves become current
  created_at TEXT NOT NULL);
CREATE INDEX ix_checkpoints_session ON checkpoints(session_id, journal_seq);

-- Journal: persistent entity events (EntitySpawn/Despawn/Change/Cargo, SaveStarted markers) in server order.
-- Strings (macros, names) are interned through string_table to keep rows small.
CREATE TABLE journal (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  server_seq INTEGER NOT NULL,
  ts TEXT NOT NULL,
  kind INTEGER NOT NULL,                       -- protocol message type id
  net_id INTEGER,
  sector_id INTEGER,
  payload BLOB NOT NULL,
  UNIQUE(session_id, server_seq));
CREATE INDEX ix_journal_net ON journal(session_id, net_id);

CREATE TABLE string_table (
  id INTEGER PRIMARY KEY,
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  value TEXT NOT NULL,
  UNIQUE(session_id, value));
