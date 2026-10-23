-- 0009_ledger_marks: the ledger follows the checkpoint on a rollback (ADR-053, task F13b).
-- ledger_seq: the ledger's monotonic sequence number (rowid of ledger_tx, never a clock) at the moment the authority began the checkpoint.
--   NULL = unknown (a checkpoint stored before this migration, or one stored without an economy): a rollback to it reverses nothing and logs a warning.
-- ledger_snapshot: JSON of the state a rollback cannot derive from the entries: the loans, the trades and the credit layout at that mark.

ALTER TABLE checkpoints ADD COLUMN ledger_seq INTEGER;
ALTER TABLE checkpoints ADD COLUMN ledger_snapshot TEXT;
