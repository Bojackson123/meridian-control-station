-- 0001_baseline.sql -- the ledger every later migration is recorded in.
--
-- Applied by Mcs.Api at startup, in one transaction, under an advisory lock. The runner
-- discovers this file by name: the leading number is its order and its identity.
--
-- This file is immutable once it has shipped. The runner stores a checksum of what it applied
-- and refuses to start against a database whose recorded checksum no longer matches, because a
-- schema that has quietly drifted from the code is the database-shaped form of HAZ-01 -- a
-- system reporting a state it is not actually in. Schema changes go in a new numbered file.
--
-- There is no telemetry table here and there will not be one. Position reports live in a
-- bounded in-memory ring buffer; durable telemetry history is a stated non-goal. What earns a
-- row in this database is what an operator may have to answer for weeks later -- mission plans,
-- the command lifecycle, deconfliction overrides, alert acknowledgements -- and those tables
-- arrive with the features that define them, not in advance of them.

CREATE TABLE IF NOT EXISTS schema_version (
    version    integer     NOT NULL PRIMARY KEY,
    name       text        NOT NULL,
    checksum   text        NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now()
);

COMMENT ON TABLE schema_version IS
    'One row per applied migration file. Written by Mcs.Api at startup, read by /health/db.';

COMMENT ON COLUMN schema_version.checksum IS
    'SHA-256 of the migration file as applied, newlines normalised. A mismatch means the file '
    'changed after it shipped, which is a startup failure rather than a warning.';
