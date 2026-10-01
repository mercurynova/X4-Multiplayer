-- 0004_saves: the save service (task M1-12). 0001 anticipated this ("revise in a later migration when the save service lands").
--
-- checkpoints: the authority's checkpoint id (the 128-bit Id128 as 32 hex digits) and the manifest uploaded next to the save, so
-- the service can find the manifest of a checkpoint and the janitor can delete it with the save. Manifests are files only
-- (data/saves/<sha256>.x4mf); they have no row of their own.
-- saves: ghosts_cleaned records the authority's flag. A save with ghosts_cleaned = 0 is stored but never made current (default 1:
-- an admin upload has no ghosts).

ALTER TABLE checkpoints ADD COLUMN checkpoint_id TEXT;
ALTER TABLE checkpoints ADD COLUMN manifest_sha256 TEXT;
ALTER TABLE checkpoints ADD COLUMN manifest_size INTEGER;
ALTER TABLE saves ADD COLUMN ghosts_cleaned INTEGER NOT NULL DEFAULT 1;
CREATE INDEX ix_checkpoints_save ON checkpoints(save_id);
