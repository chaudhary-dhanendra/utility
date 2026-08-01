# Deployment recovery and rollback

## Journal

Every deployment has an atomic, versioned JSON journal in the application data directory. It records
the deployment/package/run IDs, package fingerprint, target identity, options hash, machine and OS
user, overrides, destructive confirmations, object dependencies, SQL hashes, status, transaction
commit state, retry history, redacted failure metadata, data migration run ID, and timestamps.

The journal is updated after each object and atomically replaced using a flushed temporary file.
Success is reported only when every required selected artifact is succeeded or deliberately skipped
under policy and no required post-deployment action failed.

## Resume

Resume verifies:

- package ID and complete package fingerprint;
- deployment options hash;
- target host, port, and database;
- committed object's SQL hash.

Changed packages, options, targets, or SQL refuse resume. Committed or explicitly nontransactional
objects with the same hash are skipped. Failed objects can be selected for retry. Objects whose
prerequisites failed remain blocked.

Phase restart and repair use the same checks. Repair mode never bypasses package integrity or
destructive confirmation.

## Rollback limits

PostgreSQL transaction rollback is exact only inside the active transaction boundary. The journal
distinguishes committed, rolled back, pending, and nontransactional work and never claims the whole
deployment rolled back when earlier work committed.

Operations that cannot be transaction-wrapped include:

- `CREATE DATABASE` and `DROP DATABASE`;
- `VACUUM`;
- concurrent index creation/reindex;
- PostgreSQL versions/operations where `ALTER TYPE ... ADD VALUE` cannot be used in the selected
  transactional context;
- external effects performed by extensions or administrator-authored procedural code.

Database creation/drop is executed through the maintenance database and recorded separately.
Object-level cleanup is generated only for object kinds with a defined safe drop operation. Tables
containing data require explicit destructive confirmation.

If cancellation occurs, the active transactional boundary rolls back through Npgsql disposal or an
explicit rollback, while prior committed and nontransactional entries remain in the journal. Resume
continues from the last compatible committed state.
