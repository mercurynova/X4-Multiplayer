-- 0003_teams: the team tables of server-design 2.13 / 2.8 (task M1-T1): teams, team_members, team_relations, team_assets.
--
-- Deviation from the 2.8 sketch: team ids are session-local small integers (1, 2, 3, ... the wire team_id is a ushort and
-- the module assigns ids without waiting for the database), so a team is identified by (session_id, id) and the child
-- tables reference that pair, instead of a database-wide `id INTEGER PRIMARY KEY`.
--
-- The team module writes one full snapshot per change (delete + insert for its session in one transaction) and, at start,
-- loads the most recent session that has teams: memberships are sticky across a restart.
-- Timestamps are ISO-8601 UTC text, as everywhere else.

CREATE TABLE teams (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  id INTEGER NOT NULL,
  name TEXT NOT NULL COLLATE NOCASE,
  color TEXT NOT NULL,
  faction_slot INTEGER NOT NULL CHECK (faction_slot BETWEEN 1 AND 8),
  leader_player_id INTEGER,
  locked INTEGER NOT NULL DEFAULT 0,
  max_members INTEGER,
  join_pw_hash BLOB,
  created_at TEXT NOT NULL,
  deleted_at TEXT,
  PRIMARY KEY (session_id, id),
  UNIQUE (session_id, name),
  UNIQUE (session_id, faction_slot));

CREATE TABLE team_members (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL REFERENCES players(id),
  team_id INTEGER NOT NULL,
  role TEXT NOT NULL DEFAULT 'member',
  since TEXT NOT NULL,
  assigned_by TEXT NOT NULL,                       -- 'auto'|'lobby'|'preset'|'admin:<user>'
  PRIMARY KEY (session_id, player_id),             -- one team per player per session
  FOREIGN KEY (session_id, team_id) REFERENCES teams(session_id, id) ON DELETE CASCADE);
CREATE INDEX ix_team_members_team ON team_members(session_id, team_id);

CREATE TABLE team_relations (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  team_a INTEGER NOT NULL,
  team_b INTEGER NOT NULL,
  relation INTEGER NOT NULL CHECK (relation IN (-1, 0, 1)),   -- -1 hostile, 0 neutral, 1 allied
  updated_at TEXT NOT NULL,
  CHECK (team_a < team_b),
  PRIMARY KEY (session_id, team_a, team_b),
  FOREIGN KEY (session_id, team_a) REFERENCES teams(session_id, id) ON DELETE CASCADE,
  FOREIGN KEY (session_id, team_b) REFERENCES teams(session_id, id) ON DELETE CASCADE);

-- Player-relevant assets only (ships, stations). Written by the asset mirror (M1-T4); no producer in M1-T1.
CREATE TABLE team_assets (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  entity_id INTEGER NOT NULL,
  team_id INTEGER,
  owner_player_id INTEGER,
  kind TEXT NOT NULL,
  macro TEXT,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (session_id, entity_id));
