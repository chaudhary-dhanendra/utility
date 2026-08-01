# SQL Server catalog query reference

All statements are centralized in `SqlServerCatalogQueries`. Mappers are split by metadata area so column ordinals and result-set transitions remain reviewable.

| Query | Principal catalogs/views | Output |
|---|---|---|
| `DatabaseMetadata` | `SERVERPROPERTY`, `sys.databases`, `sys.database_files`, `sys.filegroups`, `sys.database_scoped_configurations` | Server/database identity, compatibility, options, storage, scoped settings |
| `Schemas` | `sys.schemas`, `sys.database_principals`, `sys.objects` | Schema owners and object counts |
| `Objects(version)` | `sys.objects`, `sys.tables` | Common object identity plus temporal, memory-optimized, graph, external, ledger, FileTable, and Stretch flags when supported |
| `Columns(version)` | `sys.columns`, `sys.types`, computed/identity/default metadata, masking/encryption/graph/ledger metadata | Complete column and SQL type facets |
| `Constraints` | key/check/default/foreign-key catalogs and FK columns | Constraint definitions, trust/disable state, actions, ordered columns |
| `Indexes` | `sys.indexes`, `sys.index_columns`, `sys.statistics`, `sys.data_spaces`, partition catalogs | Index kind/options, key/include columns, statistics, compression and partition placement |
| `ProgrammableObjects` | `sys.sql_modules`, `sys.parameters`, `sys.types`, triggers, sequences, synonyms | Definitions, module settings, parameters, sequence/type/synonym metadata |
| `Dependencies` | `sys.sql_expression_dependencies`, foreign keys and typed/default/computed relationships | Resolved and unresolved dependency evidence |
| `ExtendedProperties` | `sys.extended_properties` | Database/schema/object/column annotations |
| `Security` | `sys.database_principals`, `sys.database_role_members`, `sys.database_permissions` | Users, roles, memberships, grants and denies |
| `Advanced(version)` | temporal/change-tracking/RLS/full-text/Broker/assembly/credential/encryption/trigger/replication catalogs | Advanced database features and compatibility findings |
| `ExternalAndPartitioning(version)` | external data source/file format catalogs and partition functions/schemes | External endpoints, format options, partition boundaries and mappings |
| `ServerTriggers` | `sys.server_triggers`, `sys.server_sql_modules` | Opt-in server DDL trigger inventory |
| `SqlAgent` | `msdb.dbo.sysjobs` and related schedules/steps | Opt-in job, step and schedule inventory |

## Version gating

Query builders accept the discovered product major version and emit only catalog columns known to exist for that version. Examples include temporal metadata (SQL Server 2016+), graph columns (2017+), ledger metadata (2022+), and newer external-data-source fields. Optional stages also catch unsupported-feature and permission errors and convert them to findings.

Catalog evolution must be handled by adding a version branch and mapping test; it must not be handled by retrying a failing query per object.

## Definition and dependency fallback

`sys.sql_expression_dependencies` is authoritative when it resolves an in-database object. Unresolved, cross-database, linked-server, dynamic SQL, and external references remain explicit edges/findings instead of being discarded. Lightweight definition scanning records evidence for patterns that catalog dependency tracking cannot resolve reliably; it does not attempt to become a full T-SQL parser.

## Permissions

The source login should have `CONNECT`, `VIEW DEFINITION`, and sufficient access to the selected database. Server triggers and SQL Agent require additional server/MSDB rights. Encrypted modules can legitimately have no readable definition and are recorded with `DefinitionUnavailable`.
