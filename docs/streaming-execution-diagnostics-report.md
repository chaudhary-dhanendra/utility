# Streaming Execution Diagnostics Report

## Production failure identified

The persisted run `7191ddcd-6999-4f7c-8aea-d2292b382d2a` started at
2026-07-26 07:49:35.752 UTC and completed at 2026-07-26 07:49:35.783 UTC.
Four tables were attempted in parallel. Every table recorded zero rows read,
zero rows written, batch zero, and PostgreSQL SQLSTATE `42P01`
(`undefined_table`).

The first failing execution boundary in the current engine is:

- Stage: **7 - ResolvePostgreSqlTable**
- Component: **PostgreSQL target resolver / target preparation**
- Last successful run-level stage: **1 - CreateCheckpoint**
- SQL Server connection opened: **No**
- SQL Server reader created: **No**
- PostgreSQL COPY writer created: **No**
- COPY initialized: **No**
- First row converted or written: **No**
- First commit attempted: **No**

The previous engine invokes target preparation before opening the SQL Server
connection. With the selected `FailIfNotEmpty` strategy, the deterministic
target-resolution statements for the four failed table attempts are:

```sql
SELECT EXISTS (SELECT 1 FROM "nrega_sk"."sk02delmustroll2223" LIMIT 1);
SELECT EXISTS (SELECT 1 FROM "nrega_sk"."sk02mb_msr_dtl1920" LIMIT 1);
SELECT EXISTS (SELECT 1 FROM "nrega_sk"."rolemaster" LIMIT 1);
SELECT EXISTS (SELECT 1 FROM "nrega_sk"."period_wise_msr_dist2021" LIMIT 1);
```

The target identifiers above are taken from the persisted conversion mapping
snapshot. Combined with SQLSTATE `42P01`, the evidence shows that the mapped
PostgreSQL target relation was not present at resolution/preparation time.
This is not a SQL Server reader, conversion, COPY, INSERT, or commit failure.

## Remediation

Deploy/create the mapped PostgreSQL schemas and tables before executing the
data-only streaming plan, or select the explicitly confirmed `Recreate`
preparation strategy when appropriate. Then retry the affected table or run.

## Implemented diagnostics

The streaming engine now records all 17 execution boundaries with timestamps,
elapsed milliseconds, outcome, table, batch, row counters, reader/writer,
sanitized source/target SQL, SQLSTATE, component, reason, remediation,
exception type, sanitized inner exception, and sanitized stack trace.

The checkpoint is saved before table execution. A pre-row table failure now
persists a failed table checkpoint with zero row and batch counters, rather
than leaving an in-memory running state that is absent from the checkpoint.

The WPF data-migration screen now presents the current stage/table/batch,
reader, writer, last successful stage, failure stage/component/reason, and
remediation. The Excel and HTML data-migration exports include a dedicated
Streaming Execution section.

## Verification

- Release solution build: succeeded with zero warnings and zero errors.
- Full unit/UI/reporting test suite: 245 passed, 12 integration tests skipped,
  zero failed.
- Focused data-migration/WPF suite: 43 passed, one credentialed integration
  test skipped, zero failed.
- Streaming diagnostics tests: two passed, zero failed.

The credentialed production rerun could not be executed unattended because
neither integration connection variable is configured and the application
correctly does not persist the PostgreSQL password. The next interactive run
will show the stage and sanitized reason in the UI and log, and will retain
the exact sanitized target statement and exception details in the in-memory
result and exportable report.
