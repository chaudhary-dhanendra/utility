# Discovery Doctor design review

Date: 2026-07-25

## Current normal discovery sequence

`WorkspaceViewModel.StartDiscoveryAsync` creates an `InventoryDiscoveryRequest`, applies an
in-flight guard, and queues a deduplicated `BackgroundOperationDefinition`.
`BackgroundOperationService` executes it outside the UI thread. The delegate calls
`SqlServerInventoryDiscoveryService.DiscoverAsync`, projects typed `DiscoveryProgress` into the
operations grid, and marshals workspace updates through `IUiDispatcher`.

The discovery service opens one `Microsoft.Data.SqlClient.SqlConnection`, detects the product
version, executes set-based metadata commands sequentially, builds an immutable inventory, and
applies scope selection. It never queries ordinary table rows.

## Exact stage ordering and progress weights

The denominator is 15. A stage-start event retains the prior completed-stage percentage; a
completion advances to the listed value.

| Completion | Percentage | Required | Query ID | Query definition |
|---:|---:|:---:|---|---|
| Connection | 0% | Yes | `SQLSERVER.CONNECTION.OPEN` | `SqlConnection.OpenAsync` |
| Server metadata | 6.67% | Yes | `SQLSERVER.SERVER_METADATA.V1` | `ServerMetadata` |
| Database metadata | 13.33% | Yes | `SQLSERVER.DATABASE_METADATA.V2` | `DatabaseMetadata` |
| Schemas | 20.00% | Yes | `SQLSERVER.SCHEMAS.V1` | `Schemas` |
| Objects | 26.67% | Yes | `SQLSERVER.OBJECTS.V{major}` | `Objects(major)` |
| Tables | 33.33% | Yes | `SQLSERVER.TABLES.V{major}` | `Tables(major)` |
| Columns | 40.00% | Yes | `SQLSERVER.COLUMNS.V{major}` | `Columns(major)` |
| Constraints | 46.67% | Yes | `SQLSERVER.CONSTRAINTS.V1` | `Constraints` |
| Indexes | 53.33% | Yes | `SQLSERVER.INDEXES.V1` | `Indexes` |
| Programmable objects | 60.00% | Yes | `SQLSERVER.PROGRAMMABLE.V1` | `ProgrammableObjects` |
| Dependencies | 66.67% | Yes | `SQLSERVER.DEPENDENCIES.V1` | `Dependencies` |
| Extended properties | 73.33% | No | `SQLSERVER.EXTENDED_PROPERTIES.V1` | `ExtendedProperties` |
| Server triggers | 73.33% | No/opt-in | `SQLSERVER.SERVER_TRIGGERS.V1` | `ServerTriggers` |
| Security | 80.00% | No | `SQLSERVER.SECURITY.V1` | `Security` |
| Advanced features | 86.67% | No | `SQLSERVER.ADVANCED.V{major}` | `Advanced(major)` |
| External/partitioning | 93.33% | No | `SQLSERVER.EXTERNAL.V{major}` | `ExternalAndPartitioning(major)` |
| SQL Agent | 93.33% | No/opt-in | `SQLSERVER.SQL_AGENT.V1` | `SqlAgent` |
| Inventory finalization | 100% | Yes | `INVENTORY.FINALIZE.V1` | In-memory graph and classification |

The original twelve-stage implementation reported schemas as stage 2 of 12 (16.67%, rendered
as 17%). Its next command was `Objects`. The current implementation has already corrected that
ambiguous progress model; a historical 17% screenshot is therefore not evidence of a table,
column, or optional-feature failure. The exact cause can only be established by executing the
same query and mapper against the connected database.

## Queries executed after schemas

In order: objects, tables/storage/external tables, columns/types/identity/computed/default/masking/
encryption, constraints, indexes/partitions, programmable objects, dependencies, extended
properties, optional server triggers, security, advanced features, external/partition metadata,
optional SQL Agent, then in-memory finalization.

The immediate post-schema `Objects` query references `sys.objects`, `sys.schemas`,
`sys.sql_modules`, and `OBJECTPROPERTYEX`. It does **not** reference ledger, graph, temporal,
hidden-column, encryption, external-table, memory-optimized, or durability columns. Those occur
in later table, column, advanced, or external stages.

## SQL Server version assumptions

SQL Server 2016 (major 13) is the supported floor. Query builders reject older versions.
`temporal_type`, `history_table_id`, `generated_always_type`, hidden columns, masking, Always
Encrypted, memory-optimized, and durability metadata are valid at that floor. `is_node` and
`is_edge` are emitted only for major 14+, ledger metadata only for major 16+, and external data
source `connection_options` only for major 16+. No production query references `graph_type` or
`ledger_view_id`.

The doctor must additionally report server edition/engine edition, database compatibility level,
metadata visibility, MSDB access, catalog-column presence, and optional feature/catalog
availability before executing version-sensitive queries.

## Existing diagnostics and remaining gaps

Normal discovery now retains stage, query ID, SQL errors, timing, remediation, correlation ID,
retry state, cancellation state, and a sanitized export. `OperationMonitor` no longer replaces the
message with the literal `Failed`; the workspace displays the failure envelope.

What normal discovery does not provide is a preflight audit or an independently executable catalog
query explorer. Its stages share one accumulator and connection by design, so it cannot determine
whether a command fails in isolation, whether the raw SQL succeeds but its mapper fails, or whether
one optional permission is missing without starting a complete discovery.

The historical exception-loss points were `OperationMonitor.Fail` (literal `Failed`) and the bare
workspace worker catch (stale status). Both are already corrected. The doctor must consume the same
error model rather than add another generic-error path.

## WPF diagnostic bindings

The ribbon binds to `ShellViewModel`; page content binds to the currently resolved view model.
The operations grid binds `OperationSnapshot.Progress` and `OperationSnapshot.Failure`.
`WorkspaceView` binds discovery failure properties directly from `WorkspaceViewModel`.
Tools commands therefore belong on `ShellViewModel` and should activate the persistent workspace
doctor panel, whose rows are exposed by `WorkspaceViewModel`.

## Proposed Discovery Doctor architecture

- Add an application-layer doctor contract and immutable audit/query-result models.
- Add one infrastructure service that uses the existing `SqlServerConnectionFactory`,
  `SqlServerCatalogQueries`, SQL error mapping, redactor, and retry rules.
- Detect server/database capabilities first.
- Run every exact production catalog command independently on its own connection, sequentially by
  default to avoid server pressure. Drain metadata result sets with `SequentialAccess`; never read
  user table data.
- Capture query ID, stage, required/optional policy, attempts, result-set and row counts, duration,
  SQL error collection, exception type, remediation, and correlation ID.
- Permit bounded retry only for known transient SQL errors. Manual retry executes only the selected
  read-only metadata query.
- Extend the existing discovery diagnostic session to retain/export the doctor report. Exports
  omit connection strings and SQL text, pseudonymize server/database names, and redact credential
  assignments.
- Add a workspace doctor panel and Tools ribbon commands for Discovery Doctor, compatibility audit,
  catalog query explorer, diagnostic export, and the existing application log directory.
- When a full doctor run is requested, execute independent raw commands and then the production
  discovery pipeline. A raw-command failure identifies a SQL/catalog/permission problem; a raw
  success followed by a production failure identifies the exact mapper/finalization exception.

## Evidence-based diagnosis of the `vbgramg` 17% failure

The sanitized application JSONL log contains four matching failures on 2026-07-25 at 10:18:15,
10:18:33, 11:53:45, and 11:56:48 local time. Each run connected to the database, completed schema
discovery, entered `ReadObjectsAsync`, and threw:

`InvalidOperationException: Invalid attempt to read from column ordinal '0'. With
CommandBehavior.SequentialAccess, you may only read from column ordinal '17' or greater.`

The `Objects` SQL batch itself did not report a SQL Server error. The defect was in the application
reader contract. `ExecuteCommandAsync` requested `CommandBehavior.SequentialAccess`, while
`ReadObjectsAsync` read named columns out of ordinal order: it reached `is_encrypted` at ordinal 17
and subsequently requested `object_id` at ordinal 0. SQLClient correctly rejected the backwards
read before the first object could be mapped.

The production reader now uses `CommandBehavior.Default`, which matches the existing named-column
mappers and still reads one metadata row at a time. The doctor retains `SequentialAccess` only for
its independent query drain, where it does not access columns out of order. This diagnosis is based
on the actual `vbgramg` run logs, not on the 17% percentage alone.

## Verification

The complete solution builds with zero warnings and zero errors. The test project reports 211
passed and 8 opt-in integration tests skipped. A read-only
`MIGRATIONSTUDIO_DISCOVERY_DOCTOR_CONNECTION` integration test now runs all independent catalog
queries and the production pipeline against an explicitly configured existing database.

A fresh automated connection to `localhost/vbgramg` was attempted from the test host, but the
current Windows session could not authenticate (`Cannot generate SSPI context`; the alternate
encrypted attempt reported unavailable client encryption). No credentials were inferred or
retrieved. The final diagnosis above instead uses four successful-connection application runs
already captured in the sanitized application log. The WPF doctor can be rerun with the connection
settings already supplied by the operator.

## Empty-query report defect and repair

The first Discovery Doctor implementation used a nullable query-ID collection as an implicit mode:
`null` meant full diagnostics, a non-empty set meant selected-query retry, and an empty set meant
select no queries. `WorkspaceViewModel.RunCompatibilityAuditAsync` deliberately passed an empty
set. The service completed capability detection, selected zero catalog entries, skipped its
execution loop, and published a successful-looking report with `Queries: []`.

DI, assembly loading, and publish resources were not involved. The defect was the ambiguous
selection contract shared by `WorkspaceViewModel.RunCompatibilityAuditAsync` and
`SqlServerDiscoveryDoctorService.DiagnoseAsync`.

The contract now uses explicit `QuickPreflight`, `FullDiagnostic`, and `SelectedQueries` modes.
Compatibility audit calls `AuditAsync` and does not publish a Doctor result. The registry contains
18 resolvable metadata queries for SQL Server 2022; Quick Preflight selects 10, including the exact
`Objects` query immediately after schemas. Empty registration or empty diagnostic selection is a
visible configuration failure and cannot be serialized as success.

Database compatibility level is recorded as behavioral evidence but is not used as an engine
version. SQL Server 2022 query selection follows major version 16 and explicit catalog-column
probes. Optional Full Text diagnostics require the independently detected `Full Text installed`
capability and are recorded as `SkippedUnsupported` when unavailable.

Catalog SQL is compiled into `SqlServerCatalogQueries` and
`SqlServerDiscoveryDoctorService`; there are no external `.sql` files to lose during publish or
installation. Registry tests require every descriptor to contain non-empty SQL text, while the
sanitized JSON serializer deliberately omits that text.

Release 1.0.1 verification produced both self-contained and framework-dependent Windows builds
and a WiX MSI. The release `MigrationStudio.Infrastructure.dll` differs from the currently installed
1.0.0 assembly, so the installed application was not claimed as updated while two user-launched
Migration Studio processes were active.
