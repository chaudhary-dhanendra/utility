# VBGRAMG conversion-hang root-cause report

Date: 2026-07-26  
Release: 1.0.11

## Production evidence

The observed operation was `b4e1332e68234acea21e6424a15eef68`, using mapping set
`dc38caf9-f7ae-4f08-b53a-b717feaa2491`.

The original log proves that the worker did not stop at the displayed 88 percent:

- operation start: 19:38:15
- central identifier map ready: 19:38:18
- object conversion started: 19:38:18
- targeted column conversion observed: 19:42:14
- 35,767 conversion artifacts complete: 19:42:56
- 238,453 mappings published: 19:42:58
- background operation completed: 19:44:53

There was therefore no first uncompleted mapping or infinite collision group. The stale UI
reported a late `Column` candidate-generation callback while the worker was already converting
objects. The last targeted mapping visible in the production diagnostics was:

`Column|tableObjectId=4a3d2c873dcd5f969ad49a8aa9fea6e5|columnId=4`

for `[nrega_SK].[verify_observe1819].discre_obsrv`.

## Root causes

1. `WorkspaceViewModel.StartConversionAsync` created a regular
   `Progress<ConversionProgress>` from a worker context. Its callbacks were queued
   asynchronously and could arrive after newer stages. The main panel and Operations grid did
   not consume one authoritative snapshot.
2. `WorkspaceViewModel` used synchronous dispatcher invocation and projected tens of thousands
   of artifacts plus hundreds of thousands of mappings into `ObservableCollection` one item at
   a time. The worker waited for the UI projection before its operation could complete.
3. `ConversionEngine.ConvertAsync` scanned the complete dependency collection once per source
   object.
4. `ConversionEngine.OrderArtifacts` repeatedly scanned remaining artifacts and dependency
   collections.
5. `TableConverter` scanned all 156,444 production columns for every table. Constraint and index
   conversion performed analogous repeated facet searches.
6. `PostgreSqlIdentifierMappingService.EffectiveObjectType` repeatedly scanned every facet
   collection, and duplicate child mappings searched the complete accumulated mapping list.
7. Mapping liveness used an estimated denominator and did not force a truthful terminal
   processed count. Its displayed elapsed time and rate came only from worker callbacks, so both
   appeared frozen when callbacks stopped advancing.
8. Simple Wizard package generation occurred after the tracked Convert operation, giving the
   wizard and Operations grid different completion boundaries.

This was combined repeated CPU work, stale/out-of-order presentation progress, and blocking WPF
result projection—not a deadlock or an identifier collision loop.

## Implemented correction

- Added a single ordered `ConversionProgressSnapshot` consumed by both WPF surfaces.
- Added explicit weighted stages from scope collection through package/report completion.
- Added a monotonic heartbeat, live elapsed time, stale-rate reset, ETA, current object, and
  responsiveness state.
- Added a 15/30/60-second watchdog with sanitized rolling diagnostics and a terminal
  `ConversionStalledException`.
- Replaced blocking dispatcher calls with non-blocking posts.
- Replaced per-item WPF projection with one reset notification per result collection and enabled
  recycling virtualization.
- Built immutable lookup indexes for facets, columns by table, dependencies by source, artifact
  ordering, effective object type, and child mapping entries.
- Added bounded deterministic collision allocation.
- Made package generation and integrity verification part of the same tracked Convert operation.
- Awaited report and manifest work and added cancellation checks plus atomic partial-package
  cleanup.

The dominant paths changed from products such as `objects × dependencies`,
`tables × columns`, and `duplicate children × mappings` to indexed O(n) or O(n + d) passes, plus
deterministic ordering costs.

## Measured result

Production input:

- database: `vbgramg`
- discovered inventory objects: 191,444
- columns: 156,444
- exact identifier candidate/recovery work: 562,866
- identifier validation entries: 191,416
- published identifier mappings: 238,453
- converted objects: 34,799
- ordered artifacts: 35,767

Measured on the development machine:

- candidate generation: approximately 2.56 seconds in the diagnostic run
- identifier validation: approximately 0.20 seconds
- map publication stage: approximately 0.12 seconds
- complete Release production conversion regression: 9–10 seconds
- conversion plus package creation and integrity verification: 51 seconds
- verified package: 1,416,932,350 bytes and 14,141 files
- measured peak test-host working set: 1,394.1 MB

The previous production operation required approximately 4.6 minutes for conversion and another
1.9 minutes before its operation completed. Release 1.0.11 completes the same persisted
production conversion regression in approximately 10 seconds.

## Verification

- Release suite: 254 passed, 12 live-endpoint tests skipped.
- Explicit production-inventory Release test: passed.
- Collision-heavy 63-byte-prefix regression: passed.
- Cancellation, authoritative-progress, stale-rate, watchdog diagnostic, and watchdog terminal
  fault regressions: passed.
- Production package write and `MigrationPackageReader` integrity verification: passed.
- Application and installer version: 1.0.11.
- Installed executable path:
  `C:\Program Files\SQL Server to PostgreSQL Migration Studio\MigrationStudio.exe`.
- Published and installed hashes match for the executable and all Migration Studio assemblies.
- Installed footer:
  `Version 1.0.11 · build 2026-07-26T15:34:48Z · commit unavailable`.

Live PostgreSQL Deploy, first-batch migration, and post-migration validation were not executed
because `MIGRATIONSTUDIO_POSTGRES_INTEGRATION` is not configured and no target credential is
persisted by design. These endpoint-mutating checks remain explicitly unverified; package
integrity and the deploy/data-migration entry-point test suite passed without a live endpoint.
