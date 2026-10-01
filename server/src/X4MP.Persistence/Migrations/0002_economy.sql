-- 0002_economy: ledger core (server-design 2.14, roadmap M1-E1/M1-E2).
-- Append-only double-entry ledger; wallet balances are a cache verified by the EconomyAuditor.
-- Loans, trade offers and economy_events belong to M1-E4/E5 and are not created here.

CREATE TABLE wallets (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('player','team_shared','team_pool','escrow','world')),
  owner_id INTEGER NOT NULL,                     -- player id | team id | loan/trade id | 0 (world)
  balance INTEGER NOT NULL CHECK (balance >= 0 OR kind IN ('player','team_shared','world')), -- only game spend / forced reversal may overdraw
  version INTEGER NOT NULL DEFAULT 0,
  frozen INTEGER NOT NULL DEFAULT 0, frozen_reason TEXT, overdrawn INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT NOT NULL, PRIMARY KEY (session_id, kind, owner_id));

CREATE TABLE ledger_tx (
  id TEXT PRIMARY KEY,                           -- ULID
  session_id INTEGER NOT NULL, ts TEXT NOT NULL, kind TEXT NOT NULL, actor TEXT NOT NULL,
  request_id TEXT, ref_type TEXT, ref_id INTEGER,
  reverses_tx TEXT REFERENCES ledger_tx(id), reversed_by_tx TEXT REFERENCES ledger_tx(id), note TEXT);
CREATE UNIQUE INDEX ux_ledger_reverses ON ledger_tx(reverses_tx) WHERE reverses_tx IS NOT NULL; -- reverse at most once
CREATE INDEX ix_ledger_tx_session_ts ON ledger_tx(session_id, ts);
CREATE INDEX ix_ledger_tx_actor ON ledger_tx(session_id, kind, actor, ts);

CREATE TABLE ledger_entries (
  tx_id TEXT NOT NULL REFERENCES ledger_tx(id), seq INTEGER NOT NULL,
  wallet_kind TEXT NOT NULL, wallet_owner_id INTEGER NOT NULL,
  amount INTEGER NOT NULL, balance_after INTEGER NOT NULL, PRIMARY KEY (tx_id, seq));
CREATE INDEX ix_entries_wallet ON ledger_entries(wallet_kind, wallet_owner_id);

-- Append-only enforcement. The one allowed change is linking a reversal once (reversed_by_tx NULL -> value).
CREATE TRIGGER ledger_tx_no_delete BEFORE DELETE ON ledger_tx
BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
CREATE TRIGGER ledger_tx_no_update BEFORE UPDATE ON ledger_tx
WHEN OLD.reversed_by_tx IS NOT NULL
  OR NEW.id IS NOT OLD.id OR NEW.session_id IS NOT OLD.session_id OR NEW.ts IS NOT OLD.ts
  OR NEW.kind IS NOT OLD.kind OR NEW.actor IS NOT OLD.actor OR NEW.request_id IS NOT OLD.request_id
  OR NEW.ref_type IS NOT OLD.ref_type OR NEW.ref_id IS NOT OLD.ref_id
  OR NEW.reverses_tx IS NOT OLD.reverses_tx OR NEW.note IS NOT OLD.note
BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
CREATE TRIGGER ledger_entries_no_update BEFORE UPDATE ON ledger_entries
BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
CREATE TRIGGER ledger_entries_no_delete BEFORE DELETE ON ledger_entries
BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;

CREATE TABLE economy_requests (                  -- idempotency / anti-dupe
  session_id INTEGER NOT NULL, player_id INTEGER NOT NULL, request_id TEXT NOT NULL,
  type TEXT NOT NULL, payload_hash BLOB NOT NULL, result_json TEXT NOT NULL, ts TEXT NOT NULL,
  PRIMARY KEY (session_id, player_id, request_id));

-- Highest CreditDelta.seq booked per sending node (acked back in WalletUpdate.acked_delta_seq).
CREATE TABLE economy_delta_seq (
  session_id INTEGER NOT NULL, player_id INTEGER NOT NULL, last_seq INTEGER NOT NULL,
  PRIMARY KEY (session_id, player_id));

-- The credit layout the balances currently follow (written in the same transaction as the migration that changed it).
CREATE TABLE economy_state (
  session_id INTEGER PRIMARY KEY, applied_mode TEXT NOT NULL CHECK (applied_mode IN ('PerPlayer','Shared')));
CREATE TABLE economy_player_team (
  session_id INTEGER NOT NULL, player_id INTEGER NOT NULL, team_id INTEGER,   -- NULL = unassigned
  PRIMARY KEY (session_id, player_id));
