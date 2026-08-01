# Architecture Decisions

This file records decisions that materially affect compatibility, security, persistence, or module boundaries.

## ADR-001: SQL Server discovery uses catalog queries, not SMO

**Status:** Accepted  
**Date:** 2026-07-24

Discovery uses explicit, parameter-free or parameterized set-based queries against `SERVERPROPERTY`, `sys.*` catalog views, documented database properties, and permission-safe DMVs. SMO is not referenced.

Catalog access is implemented in `MigrationStudio.Infrastructure`; `Microsoft.Data.SqlClient` types do not cross the application boundary. The reader uses one active result reader per connection and does not require Multiple Active Result Sets. Large result sets are consumed sequentially. Queries are grouped into bounded discovery stages to provide cancellation and progress without issuing one query per object.

SQL Server version- or permission-sensitive stages are capability-gated. Failure of an optional stage creates an evidence-bearing finding and does not discard metadata already discovered. Core connection, database, schema, object, table, column, constraint, index, and dependency stages remain mandatory.

## ADR-002: Inventory separates common identity from typed facets

**Status:** Accepted  
**Date:** 2026-07-24

Every discoverable item is represented by an `InventoryObject` containing the required common identity, provenance, selection, definition, hashing, warning, and status fields. Specialized metadata is represented by strongly typed facets keyed by the stable `InventoryObjectId`: table, column, constraint, index, module, parameter, sequence, type, synonym, security, partition, full-text, Service Broker, encryption, change-data, SQL Agent, and external-dependency facets.

Stable IDs are deterministic SHA-256-derived GUIDs over source database identity, object type, schema, name, SQL object ID, and parent identity. This makes two snapshots comparable without treating mutable catalog row order as identity.

## ADR-003: Discovery precedes scope selection

**Status:** Accepted  
**Date:** 2026-07-24

The catalog reader discovers a complete user-relevant inventory first. Complete-database, selected-schema, Excel-selected-table, and manual selection modes are policies over that immutable inventory. This prevents four discovery implementations from diverging and allows dependency policies to include objects outside the initial scope with an explicit selection reason.

System schemas `sys` and `INFORMATION_SCHEMA` are excluded by default, based on their exact catalog names and principal metadata. User schemas that merely resemble system names are not excluded.

## ADR-004: Dependency graph is explicit and tolerant of unresolved references

**Status:** Accepted  
**Date:** 2026-07-24

Dependencies from foreign keys, parent-child ownership, `sys.sql_expression_dependencies`, types, sequences, constraints, security policies, synonyms, and external references are normalized into directed `InventoryDependency` edges.

Unresolved and cross-server/cross-database references remain graph edges with external target descriptors. They are never silently dropped. Tarjan strongly connected component analysis assigns cycle IDs. A broken module cannot fail the entire graph.

## ADR-005: Connection secrets are ephemeral

**Status:** Accepted  
**Date:** 2026-07-24

SQL passwords exist only in the connection form ViewModel and transient connection options. They are never written to settings or inventory snapshots. The connection string builder is the only component that combines endpoint metadata and a password.

Logs receive redacted messages through a shared `ISensitiveDataRedactor`. Connection strings are parsed with `SqlConnectionStringBuilder` where possible and password values are replaced. SQL login password hashes and key material are never queried.

A future opt-in persistence feature must use the existing credential-store port and Windows-protected storage; normal JSON settings are not an acceptable secret store.

## ADR-006: Inventory snapshots use versioned compressed JSON

**Status:** Accepted  
**Date:** 2026-07-24

Inventory snapshots use deterministic UTF-8 JSON inside GZip with a format version, discovery engine version, application version, source server version, and UTC timestamp. Files use the `.msinventory` extension. Atomic replace prevents partially written snapshots. Connection options and passwords are excluded by type and are not serializable snapshot members.

JSON was chosen for inspectability and forward migration; compression addresses large module definitions and metadata collections.

## ADR-007: ClosedXML is isolated behind an application port

**Status:** Accepted  
**Date:** 2026-07-24

ClosedXML is used only in `MigrationStudio.Infrastructure`. Workbook enumeration, table-name parsing, duplicate removal, matching, ambiguity reporting, and issue export are exposed through provider-neutral application contracts. Excel COM automation is prohibited.

## ADR-008: Serilog becomes the single logging pipeline

**Status:** Accepted  
**Date:** 2026-07-24

The application continues to log through `Microsoft.Extensions.Logging`, but Serilog is the only configured provider and file pipeline. The previous bespoke JSON file provider is removed rather than run in parallel. Serilog writes compact structured rolling files with size limits and retention.

A destructuring policy/redaction layer sanitizes scalar strings and exception text before persistence. High-risk connection configuration is never passed as a structured object.

## ADR-009: Integration tests are explicit and opt-in

**Status:** Accepted  
**Date:** 2026-07-24

SQL Server integration tests run only when `MIGRATIONSTUDIO_SQLSERVER_INTEGRATION` is set. When absent, a custom xUnit fact marks the test skipped, never passed. The fixture creates a uniquely named disposable database and removes it in `finally`.

## ADR-010: Discovery UI virtualizes large collections

**Status:** Accepted  
**Date:** 2026-07-24

The manual-selection tree creates category nodes eagerly but materializes object children only when a category is expanded. Tree and grid panels enable recycling virtualization. Search results use bounded/paged projections. Selection state is stored by stable object ID outside WPF nodes, allowing more than 5,000 tables without keeping every visual node alive.

## ADR-011: SQL translation uses a tokenizer and structured transformations

**Status:** Accepted  
**Date:** 2026-07-24

The conversion engine uses a lossless T-SQL tokenizer that distinguishes words, numbers, string literals, quoted/bracketed identifiers, whitespace, symbols, line comments, and nested block comments. Function calls and argument lists are transformed structurally. Narrow string operations are allowed only after token boundaries are known.

The engine does not claim full T-SQL grammar coverage. Constructs without a proven semantic rule produce manual-review or unsupported artifacts with preserved source text. This is preferable to accepting a parser tree whose unsupported nodes are silently discarded.

## ADR-012: PostgreSQL rendering uses deterministic rule-owned SQL

**Status:** Accepted  
**Date:** 2026-07-24

Object-family converters own small PostgreSQL statement models and deterministic rendering. A single universal PostgreSQL AST was rejected for this stage because DDL, routines, security, comments, and package metadata have materially different semantics. All output still passes the shared offline structural validator and optional live transactional validator.

Artifact hashes, ordering, identifier allocation, and run IDs are deterministic for the same inventory and options. Manual skeletons are compile-safe only where useful and are always classified `ManualConversion`.

## ADR-013: Supported PostgreSQL majors are 14 through 18

**Status:** Accepted  
**Date:** 2026-07-24

The selectable target range is PostgreSQL 14, 15, 16, 17, and 18, matching the supported upstream major versions at implementation time. Version is part of `ConversionOptions`, manifests, scripts, reports, and rule context. Unsupported versions fail validation instead of silently using the newest syntax.

## ADR-014: Live validation uses Npgsql and an always-rolled-back transaction

**Status:** Accepted  
**Date:** 2026-07-24

Offline validation is always performed. Optional live validation opens an explicitly configured PostgreSQL test connection, executes non-manual artifacts in deployment order inside one transaction, captures SQLSTATE/message/position per object, and rolls the transaction back in `finally`.

The feature is not enabled implicitly against a production connection. Integration tests require `MIGRATIONSTUDIO_POSTGRES_INTEGRATION` and are reported as skipped when it is absent.
