-- 0007_trades: escrowed trades (server-design 2.14, roadmap M1-E5).
-- A trade is one JSON document (items, acceptances, request marks) plus the columns the server and the GUI filter on.
-- trade_locks is the "unique index" of the design: an asset is locked by at most one open trade, enforced by the primary key.

CREATE TABLE trades (
  session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  id INTEGER NOT NULL,
  state TEXT NOT NULL,
  version INTEGER NOT NULL,
  initiator INTEGER NOT NULL,
  counterparty INTEGER NOT NULL,
  body_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY (session_id, id));
CREATE INDEX ix_trades_state ON trades(session_id, state);

CREATE TABLE trade_locks (
  session_id INTEGER NOT NULL,
  entity_id INTEGER NOT NULL,                    -- net id of the ship or ware container
  trade_id INTEGER NOT NULL,
  PRIMARY KEY (session_id, entity_id),
  FOREIGN KEY (session_id, trade_id) REFERENCES trades(session_id, id) ON DELETE CASCADE);
CREATE INDEX ix_trade_locks_trade ON trade_locks(session_id, trade_id);
