-- 0010_ledger_seq: an explicit, stable ledger sequence number (task from the PR #28 review).
-- Until now the mark sequence (checkpoints.ledger_seq, migration 0009) was the IMPLICIT rowid of ledger_tx. ledger_tx has a TEXT primary key, so
-- SQLite may renumber its rowids on VACUUM, and a stored mark could then select the wrong transactions. `seq` is a real column: backfilled from the
-- rowid in rowid order (so every mark stored so far, which WAS a rowid, stays valid), unique, and assigned MAX(seq)+1 by the store when a row is committed.
-- The append-only trigger refuses the backfill UPDATE (it also fires for a row that already has reversed_by_tx), so it is dropped and recreated;
-- the new definition also freezes seq.

DROP TRIGGER ledger_tx_no_update;
ALTER TABLE ledger_tx ADD COLUMN seq INTEGER;
UPDATE ledger_tx SET seq = rowid;
CREATE UNIQUE INDEX ux_ledger_tx_seq ON ledger_tx(seq);
CREATE TRIGGER ledger_tx_no_update BEFORE UPDATE ON ledger_tx
WHEN OLD.reversed_by_tx IS NOT NULL
  OR NEW.id IS NOT OLD.id OR NEW.session_id IS NOT OLD.session_id OR NEW.ts IS NOT OLD.ts
  OR NEW.kind IS NOT OLD.kind OR NEW.actor IS NOT OLD.actor OR NEW.request_id IS NOT OLD.request_id
  OR NEW.ref_type IS NOT OLD.ref_type OR NEW.ref_id IS NOT OLD.ref_id
  OR NEW.reverses_tx IS NOT OLD.reverses_tx OR NEW.note IS NOT OLD.note
  OR NEW.seq IS NOT OLD.seq
BEGIN SELECT RAISE(ABORT, 'ledger is append-only'); END;
