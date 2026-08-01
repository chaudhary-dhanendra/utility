# SQL Server discovery subsystem

## Purpose and boundary

The discovery subsystem produces a versioned, immutable `InventorySnapshot`. It reads SQL Server metadata only; it does not convert, deploy, or migrate data. Downstream engines consume the snapshot rather than retaining live `SqlConnection`, data-reader, UI, or ClosedXML objects.

The application uses `Microsoft.Data.SqlClient` and set-based catalog queries. SMO is deliberately not the primary discovery mechanism because it adds a large deployment surface, performs hidden round trips, and makes feature/version behavior harder to audit.

## Execution pipeline

Discovery opens one encrypted connection to the selected database and executes these ordered stages asynchronously:

1. database/server identity, options, files, filegroups, and scoped configuration;
2. schemas;
3. base objects and table feature flags;
4. columns and type metadata;
5. keys, checks, defaults, and foreign keys;
6. indexes, index columns, statistics, and partition placement;
7. programmable objects, parameters, definitions, hashes, and text-level compatibility evidence;
8. catalog and expression dependencies;
9. extended properties;
10. database principals, role membership, and permissions;
11. temporal/change tracking, RLS, full text, Service Broker, assemblies, encryption, triggers, replication, external objects, and partitioning;
12. final normalization, dependency counts, strongly connected components, classification, and scope closure.

Server triggers and SQL Agent jobs are explicit opt-in stages because they require server-wide permissions and are not database-contained.

Each query fully consumes its result sets before the next command starts. MARS is disabled. Optional feature/permission failures become structured findings; failures of the essential identity/object stages abort discovery with preserved SQL Server error number, class, state, procedure, and line.

## Scope semantics

Catalog discovery is complete before selection is applied. This is necessary to calculate reliable dependency closure and detect ambiguous unqualified Excel names.

- `CompleteDatabase` includes all non-system objects.
- `SelectedSchemas` includes objects in checked schemas.
- `ExcelSelectedTables` includes unambiguous workbook matches.
- `ManualObjectSelection` includes checked inventory objects.

The dependency policy can keep only direct selections, add transitive required dependencies, or add both dependencies and dependents. Parent objects are always included. Every included object records its `SelectionReason`.

## Excel selection

Only `.xlsx` files are accepted. ClosedXML is isolated behind `IExcelTableSelectionService`. Users choose a worksheet and identify the table-name column by header, Excel letter, or 1-based number. Values are trimmed, blanks are ignored, and case-insensitive duplicates are removed.

Qualified names such as `[sales].[Order]]Line]` are matched exactly by schema and table. Unqualified names match only when unique across all schemas. Unmatched and ambiguous rows remain visible and can be exported to a separate issue workbook.

## Persistence and security

Snapshots use the `.msinventory` extension and contain GZip-compressed JSON with an explicit format version, engine version, application version, and timestamp. Writes are atomic through a temporary file followed by replacement.

Passwords exist only in the active connection options. They are not stored in settings or snapshots. Connection strings disable `PersistSecurityInfo` and MARS. Serilog output passes through a redacting JSON formatter that removes password, token, secret, and access-key assignments from messages, exceptions, and structured values.

## Performance rules

- Use set-based catalog queries, never one query per object.
- Keep definitions in the snapshot but bind them only in the inspector.
- UI grids enable row/column recycling and expose search-based projection.
- Large work executes through the bounded background scheduler with progress and cancellation.
- Inventory IDs are deterministic so snapshots can later be compared incrementally.

The current implementation is designed for large catalogs but does not claim a fixed object-count limit. Production qualification should benchmark representative databases and capture stage timings and managed-memory high-water marks.

## Running the SQL Server integration fixture

The integration fixture is skipped unless `MIGRATIONSTUDIO_SQLSERVER_INTEGRATION` contains an administrative SQL Server connection string. When enabled, it creates a uniquely named temporary database, installs representative schema/table/temporal/view/sequence objects, runs discovery, asserts the resulting inventory, and drops the database in `finally`.

```powershell
$env:MIGRATIONSTUDIO_SQLSERVER_INTEGRATION = "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true"
dotnet test MigrationStudio.sln --filter Category=Integration
```
