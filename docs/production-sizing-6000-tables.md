# Production Sizing for Approximately 6,000 Tables

Use discovery results—not table count alone—to size and divide the migration. Build waves from
row counts, reserved bytes, LOB presence, foreign-key components, conversion risk, and the
maintenance window. A database with 6,000 mostly empty configuration tables behaves very
differently from one with hundreds of multi-gigabyte transactional tables.

## Starting profiles

These are conservative starting points, not guaranteed throughput settings. Increase concurrency
only after source latency, target WAL/checkpoint pressure, network utilization, and managed-memory
peaks have been observed.

| Setting | 16 GB RAM | 32 GB RAM | 64 GB RAM |
|---|---:|---:|---:|
| Discovery commands | 1–2 | 2 | 2–4 |
| Concurrent table loads | 2 | 4 | 6–8 |
| SQL Server readers | 2 | 4 | 6 |
| PostgreSQL writers | 2 | 4 | 6 |
| SQL Server pool maximum | 8 | 12 | 20 |
| PostgreSQL pool maximum | 8 | 12 | 20 |
| Batch row target | 2,000–5,000 | 5,000 | 5,000–10,000 |
| Batch byte ceiling | 16 MiB | 32 MiB | 32–64 MiB |
| Checkpoint interval | every batch for high-risk tables; otherwise 5,000 rows | same | 10,000 rows after recovery testing |
| Command timeout | 300 seconds; longer only per known large table | 300 seconds | 300 seconds |
| Report mode | one wave at a time; full final summary | complete report with split detail sheets | complete report with split detail sheets |
| Validation | counts plus deterministic samples per wave; full checksums selectively | comprehensive by risk | comprehensive with controlled parallelism |

The PostgreSQL binary COPY strategy is preferred for supported transport kinds. Parameterized
batch insert remains the safe fallback for opaque or custom conversions. The current byte ceiling
is the effective COPY/batch memory guard; provider-internal buffers should remain materially below
that ceiling.

## Disk and retention

- Keep free temporary space of at least the larger of 25 GB or twice the expected report,
  package, checkpoint, and failed-row footprint.
- For a complete 10 GB migration, reserve additional target space for PostgreSQL table/index
  growth, WAL, temporary sorting, and vacuum. A practical initial target is 2.5–3 times source
  used bytes until actual mapped sizes are known.
- Place reports and migration packages on a local fixed drive during generation, then copy them
  to controlled storage after hash verification.
- Retain operational logs for 30 days by default, longer where audit policy requires it. Keep
  sanitized release and acceptance reports separately from diagnostic logs.
- Failed-row payloads remain protected and should have a shorter explicit retention period.

## Wave strategy

The optional migration-wave planner keeps the logical **Complete Database** selection intact while
providing controlled execution groups:

1. foundation schemas, types, and sequences;
2. reference and master tables;
3. independent transactional tables;
4. dependent transactional groups;
5. large and LOB-heavy tables;
6. strongly connected table groups;
7. programmable objects;
8. security;
9. full validation.

For large tables, isolate one or a few tables per wave. Preserve dependent groups together unless
foreign keys are intentionally deferred. Tables without a stable single-column resume key should
be scheduled into a window where a complete table restart is acceptable.

## Source consistency

Prefer a source database snapshot or a quiesced application window. Snapshot isolation can be
used only when enabled and when version-store growth is monitored. A long migration under ordinary
read committed isolation does not represent one database point in time.

## PostgreSQL maintenance

- provision WAL and checkpoint capacity before increasing writer concurrency;
- create or validate tables before data movement, defer foreign keys and expensive secondary
  indexes where the approved deployment plan permits;
- reset sequences after explicit identity values are loaded;
- run `ANALYZE` after each material wave;
- schedule vacuum/analyze according to target policy rather than running unrestricted maintenance
  concurrently with peak COPY activity;
- validate extensions and locale/collation behavior before the first production wave.

## Qualification still required

The local 32 GB run qualified synthetic inventory, report, Excel, graph, checkpoint, and
cancellation kernels. It did not measure live SQL Server or PostgreSQL throughput. Begin with the
32 GB profile above, run the provided scale fixture on disposable infrastructure, capture the
benchmark report, and adjust only from observed bottlenecks.
