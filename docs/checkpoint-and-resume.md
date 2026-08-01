# Checkpoint and resume

Checkpoints are versioned JSON files under the application's local data directory. Writes use a
new file, asynchronous flush with write-through, and atomic replacement. A process interruption
therefore leaves either the prior complete checkpoint or the new complete checkpoint.

Each checkpoint records:

- run ID and application version;
- source database identity and aggregate source metadata hash;
- target host/port/database identity without credentials;
- complete configuration hash;
- table identifiers and source/target names;
- transfer strategy and resumability;
- last committed batch and stable key;
- rows read, written, and rejected;
- timestamps and terminal state.

The engine advances a table checkpoint only after the PostgreSQL batch completes. Cancellation saves
the latest committed state. Passwords, connection strings, row values, and encryption keys are not
serialized.

## Resume safety

Resume recomputes the plan and compares source identity, target identity, metadata hash, and
configuration hash. A mismatch refuses resume. This prevents a changed column mapping, predicate,
selection, target, or schema from silently skipping rows.

A table is resumable only with a single stable configured numeric or UUID key. The source query uses
keyset pagination (`key > checkpoint`) and deterministic ordering. Physical row order, offsets, and
row ordinals are never used to skip source rows.

Tables with composite keys or no stable key still create progress checkpoints, but an interrupted
partial load is explicitly non-resumable. Clear that table checkpoint and restart it with a safe
target preparation strategy.

## Restart operations

`RestartTableAsync` removes only the selected table checkpoint. `RestartRunAsync` removes the run
checkpoint. Neither method silently deletes target rows; the configured and explicitly confirmed
target preparation policy runs when the restarted load begins.

Completed tables remain skipped during run resume. A partially written safely resumable table does
not re-run the initial empty-target check, because its committed rows are the expected resume base.
