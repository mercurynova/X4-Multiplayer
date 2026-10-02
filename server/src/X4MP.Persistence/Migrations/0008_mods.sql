-- 0008_mods: mod management (docs/mod-management.md section 7, tasks M1-X3/X4).
--
-- player_extension_reports: the full extension report of every ClientHello (items_json) and how the policy judged it. The janitor rule
--   (keep the newest 20 per player) runs in the same transaction as each insert.
-- session_mod_policy / session_mod_entries: the session mod list. The server runs one standing policy that every session uses, stored
--   under session_id 0 (no foreign key on purpose: the policy is edited before any session row exists and outlives sessions).
-- mod_catalog: server-wide memory of a mod's name, links, class override and notes, reused by every session.

CREATE TABLE player_extension_reports (
  id INTEGER PRIMARY KEY,
  player_id INTEGER NOT NULL REFERENCES players(id),
  session_id INTEGER REFERENCES sessions(id),
  ts TEXT NOT NULL,
  ext_hash BLOB,
  items_json TEXT NOT NULL,
  outcome TEXT NOT NULL,
  violation_json TEXT,
  policy_version INTEGER NOT NULL DEFAULT 0);
CREATE INDEX ix_ext_reports_player ON player_extension_reports(player_id, ts);

CREATE TABLE session_mod_policy (
  session_id INTEGER PRIMARY KEY,
  version INTEGER NOT NULL,
  source_mode TEXT NOT NULL,
  unknown_default TEXT NOT NULL,
  enforcement TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  updated_by TEXT NOT NULL);

CREATE TABLE session_mod_entries (
  session_id INTEGER NOT NULL,
  ext_id TEXT NOT NULL,
  name TEXT NOT NULL,
  rule TEXT NOT NULL,
  enabled INTEGER NOT NULL,
  class TEXT NOT NULL,
  version_rule TEXT NOT NULL,
  version TEXT,
  content_hash BLOB,
  nexus_url TEXT,
  workshop_id INTEGER,
  notes TEXT,
  sort INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (session_id, ext_id));

CREATE TABLE mod_catalog (
  ext_id TEXT PRIMARY KEY,
  name TEXT,
  nexus_url TEXT,
  workshop_id INTEGER,
  class_override TEXT,
  notes TEXT,
  updated_at TEXT NOT NULL);
