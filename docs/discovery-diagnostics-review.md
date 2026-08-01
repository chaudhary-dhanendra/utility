# SQL Server discovery diagnostics review

Date: 2026-07-25

## Current control flow

1. `WorkspaceViewModel.StartDiscoveryAsync` validates only that a database is selected, sets
   `IsBusy`, creates an `InventoryDiscoveryRequest`, creates a `BackgroundOperationDefinition`,
   and enqueues it through `IBackgroundOperationScheduler`.
2. `BackgroundOperationService` adds an `OperationSnapshot`, runs the delegate on a worker, and
   marks the operation completed, cancelled, or failed.
3. The delegate creates a `Progress<DiscoveryProgress>`, projects it to `OperationProgress`, and
   also updates the workspace `Progress` and `Status`.
4. `SqlServerInventoryDiscoveryService.DiscoverAsync` opens one SQL connection and executes twelve
   grouped stages sequentially. Required stages use `ExecuteStageAsync`; optional stages use
   `ExecuteOptionalStageAsync`.
5. Each command uses `SequentialAccess`, a bounded command timeout, and cancellation-aware
   `ExecuteReaderAsync`/`ReadAsync`. The accumulator is converted to an immutable snapshot after all
   queries complete, then scope selection is applied.

The stage immediately after `Schemas` is the required `Objects` command
(`SqlServerCatalogQueries.Objects`), which reads `sys.objects`, `sys.schemas`,
`sys.sql_modules`, and `OBJECTPROPERTYEX`. `Schemas` is reported as stage 2 of 12, or 16.67%.
Consequently, a failure in `Objects` leaves the last successful progress value rounded to 17% and
the workspace text at `Schemas: Schemas discovered.` This exactly matches the reported symptom.

## Current error flow and information loss

- `ExecuteStageAsync` has no stage-level catch. It does not attach the stage name, query identity,
  attempt, elapsed time, or returned-row count to an exception.
- `DiscoverAsync` catches only `SqlException`. Mapping or reader exceptions such as
  `InvalidCastException`, `IndexOutOfRangeException`, `InvalidDataException`, and `IOException`
  escape without discovery context.
- The `SqlException` is wrapped in `SourceDatabaseException` with the generic message
  `SQL Server discovery failed for database ...`. Although `SqlServerError` objects are retained,
  the stage and query are not retained.
- `BackgroundOperationService` catches the wrapper, sends the full exception only to structured
  logging, and calls `OperationMonitor.Fail` with `exception.Message`.
- `OperationMonitor.Fail` overwrites the last useful progress message with the literal `Failed`.
  The operations grid binds only `Progress.Message`; it does not display `ErrorMessage`.
- The workspace delegate uses a bare `catch`, clears only `IsBusy`, and rethrows. It does not
  update `Status`, display the SQL Server number/state/class, or preserve a user-facing failure
  summary. Therefore the stale schema status remains visible.
- The JSONL formatter does preserve exception text, but the UI provides no correlation identifier,
  stage/query selector, or exportable discovery-specific diagnostic document.

## Current progress flow

Progress is based on twelve broad grouped commands, not on the actual discovery activities. A
group may contain multiple result sets and optional feature families, so a failure cannot be
localized beyond the group. The percentage is the last completed stage divided by twelve. A
stage-start event is never reported. Optional stages do not report their own start, skip, retry,
or failure state.

The grouped `Objects` command includes object identity and complete module definitions. On a
large catalog it can run significantly longer than `Schemas` while the UI remains fixed at 17%.

## Version and feature compatibility risks

- `Tables(int)` correctly gates graph columns (`is_node`, `is_edge`) at SQL Server major 14 and
  ledger metadata at major 16.
- `ExternalAndPartitioning(int)` gates `connection_options` at major 16.
- Temporal columns (`temporal_type`, `history_table_id`), generated/hidden column metadata,
  dynamic masking, and Always Encrypted metadata are queried unconditionally. They are valid for
  the documented SQL Server 2016+ floor, but the `Columns` and advanced query builders do not
  explicitly encode that floor or provide compatibility fallbacks.
- `Advanced` combines temporal, change tracking, row-level security, Full Text, Service Broker,
  CLR, credentials, encryption keys, triggers, and replication in one command. A missing catalog,
  edition difference, or permission failure in any result set discards the remainder of the
  combined optional stage.
- `Tables(int)` combines required table/storage metadata with external-table catalogs. An external
  catalog compatibility failure can currently abort required table discovery.
- SQL Agent is optional and permission errors are converted to findings. Server triggers,
  security, extended properties, advanced metadata, and external/partition metadata are also
  optional, but their failure evidence lacks a query ID and retry classification.
- No query references `graph_type`; graph support is derived from table flags.

## Duplicate-operation risk

There is no XAML event handler in addition to `StartDiscoveryCommand`, so an event/command double
binding was not found. CommunityToolkit prevents overlapping execution only while
`StartDiscoveryAsync` itself is awaiting. That method returns as soon as enqueue completes, while
the worker continues discovery. The command then becomes executable again, the button has no
`CanExecute` guard, and `_operationId` is overwritten. A second click can therefore enqueue a
second discovery, create a duplicate operation row, and make cancellation target only the newer
run.

## Cancellation and recovery weaknesses

- SQL commands and readers are asynchronously disposed and receive the token, which is a good
  baseline.
- The UI changes immediately to `Cancellation requested`; it does not use `Cancelling` until the
  worker confirms reader/command/connection disposal.
- `_operationId` is not cleared on completion, cancellation, or failure.
- `IsBusy` is cleared by the workspace delegate before the operation monitor records its terminal
  state, and duplicate runs can race that property.
- No cancellation timestamps or final resource-release state are retained.
- The accumulator is in memory only and no incomplete snapshot is saved, which avoids corrupting a
  valid snapshot. There is no explicit statement in diagnostics confirming that partial inventory
  was discarded.
- Retry does not exist. A user can click Start again, but that is an unrelated new operation rather
  than a bounded retry of a known idempotent stage.

## Proposed changes

- Introduce `DiscoveryStage` and stage status models and report stage start, completion, optional
  skip/failure, retry, cancellation, and terminal state.
- Give every query a stable non-sensitive query ID and compatibility descriptor. Do not export SQL
  text or connection strings.
- Wrap every required-stage failure, including reader/mapping failures, in a stage-aware sanitized
  exception retaining SQL Server error number/class/state/procedure/line.
- Split optional feature families so one unsupported catalog or permission failure does not suppress
  unrelated metadata.
- Use bounded retry only for classified transient SQL errors on idempotent read-only stages, opening
  a fresh connection before retry. Do not retry permission, syntax, invalid-column, cancellation,
  or mapping errors.
- Record per-attempt timing and row/facet counts and expose a sanitized JSON diagnostic document.
- Preserve actionable failure details on `OperationSnapshot` instead of replacing them with
  `Failed`.
- Guard discovery with both command `CanExecute` and an atomic in-flight gate. Keep the command
  disabled until the background operation actually terminates.
- Show stage, query ID, SQL error, remediation, retryability, and correlation ID in WPF. Keep
  `Cancelling` visible until resource disposal and worker termination.
- Keep the existing discovery service, operation monitor, JSONL logger, DI container, and WPF MVVM
  architecture; extend them rather than creating a parallel diagnostics framework.

## Files and classes to modify

- `MigrationStudio.Application/Discovery/DiscoveryContracts.cs`
- `MigrationStudio.Application/Discovery/SourceDatabaseException.cs`
- `MigrationStudio.Infrastructure/SqlServer/SqlServerCatalogQueries.cs`
- `MigrationStudio.Infrastructure/SqlServer/SqlServerInventoryDiscoveryService*.cs`
- `MigrationStudio.Infrastructure/Operations/BackgroundOperationService.cs`
- `MigrationStudio.Infrastructure/Operations/OperationMonitor.cs`
- `MigrationStudio.Domain/Operations/OperationSnapshot.cs`
- `MigrationStudio.Desktop/ViewModels/WorkspaceViewModel.cs`
- `MigrationStudio.Desktop/Views/WorkspaceView.xaml`
- `MigrationStudio.Desktop/MainWindow.xaml`
- `MigrationStudio.Infrastructure/DependencyInjection.cs`
- discovery, operation, diagnostics, compatibility, retry, cancellation, and ViewModel tests

## Implementation outcome

The implementation extends the existing discovery, operation-monitoring, logging, DI, and MVVM
paths. It does not introduce a second diagnostics framework.

- Discovery now emits typed stage transitions with stable query IDs, required/optional status,
  attempt number, elapsed time, accumulated-object count, and terminal state.
- Required SQL, reader, mapping, connection-open, and inventory-finalization failures are wrapped
  with their stage, query ID, SQL Server errors, remediation, retry classification, and correlation
  ID. Partial inventories are explicitly discarded.
- Optional command groups convert failures into inventory findings and discovery continues with the
  next optional group.
- Automatic retry is bounded and limited to known transient SQL Server errors on read-only catalog
  commands when no inventory facets were added. Retry reopens the primary connection before the
  command so subsequent stages use the recovered connection.
- SQL Server 2016 is an explicit minimum. Graph, ledger, and external-data-source catalog columns
  are selected only on versions that expose them.
- An atomic ViewModel guard and scheduler deduplication key prevent re-entrant and queued duplicate
  discovery runs.
- Cancellation uses a distinct `Cancelling` state and becomes `Cancelled` only after the operation
  delegate has unwound and asynchronous SQL resources have been released.
- The workspace and operations grid retain stage/query/remediation details instead of replacing the
  last progress message with `Failed`.
- Diagnostic JSON exports contain no connection string or SQL text, redact credential assignments,
  and pseudonymize server and database names.

Verification on 2026-07-25: the complete solution builds with zero warnings and zero errors.
The unit test project reports 202 passed and 7 skipped database-backed integration tests. The live
SQL Server discovery integration test remains opt-in and was not executed because no endpoint was
configured in this environment.
