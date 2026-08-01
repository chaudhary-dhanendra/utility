# Identifier mapping completeness and production failure review

## Production failure

The failing Data Migration lookup was:

`[nrega_SK].[verify_observe1819].discre_obsrv`

The persisted production run contains the table with source object ID
`e20dc7da-e0b9-5230-82a4-a8b16d0002a0`, but its object type was `Unknown`.
The persisted map also contains an unrelated, top-level column entry for
`discre_obsrv`; it does not contain the canonical table-owned key
`(table object ID, column object ID)`.

The root cause was SQL Server catalog type padding. `sys.objects.type` is
`char(2)`. SqlClient returned the user-table discriminator as `"U "`, while
`SqlServerInventoryDiscoveryService.MapObjectType` switched on the untrimmed
value and only recognized `"U"`. The table was therefore classified as
`Unknown`.

That classification selected `FallbackObjectConverter` instead of
`TableConverter`. The fallback path registered the table but did not register
all table columns. Only columns referenced while converting constraints were
registered as table-owned child identifiers. Because `discre_obsrv` was not one
of those constraint columns, Data Migration's canonical `(table, column)`
lookup could not find it. Conversion still reached Completed because no
identifier-map completeness gate existed.

## Corrected control flow

1. SQL catalog object discriminators are trimmed before classification.
2. Legacy snapshots are reconciled from structural facets (table, sequence,
   synonym and type inventories) before conversion.
3. `PostgreSqlIdentifierMappingService` creates one mapper for the inventory
   and naming policy.
4. The mapper eagerly registers included schemas, objects, every included table
   column, constraints, indexes, triggers, module fields and type fields in
   deterministic order.
5. Source identity is recorded as `SourceIdentifierKey`, using parent and
   object IDs when available.
6. The conversion engine verifies every included object and column against the
   completed map before it can return a `ConversionRun`.
7. The immutable mapping snapshot is published with the conversion run and
   consumed by Data Migration, deployment, validation and reporting.
8. Data Migration treats an unexpectedly absent active entry as recoverable:
   it resolves the discovered object by ID, regenerates the identifier through
   the same mapper, records `AutoRecovered`, updates the active report, and
   continues.

## Naming and namespace policy

- Unquoted automatic names are normalized to lower case.
- Unicode letters, digits and meaningful underscores are retained.
- Unsupported character runs and whitespace become one underscore.
- Empty names become `unnamed`; a leading digit is prefixed with `_`.
- Reserved PostgreSQL words are quoted according to the selected target
  version.
- Identifiers are limited to 63 UTF-8 bytes without splitting a Unicode rune.
- Collisions are allocated deterministically from stable source identity,
  independent of inventory enumeration order.
- Schema, schema-object, table-column, table-trigger, schema-index and
  constraint namespaces are allocated separately.
- SQL Server collation selects case-sensitive or case-insensitive source-name
  lookup; stable object IDs remain authoritative.

## Completeness, persistence and invalidation

The map is constructed privately by the mapper and copied into
`ConversionRun.IdentifierMappings` only after conversion and completeness
validation succeed. An incomplete map raises one aggregate failure listing all
unresolved included identifiers; it cannot produce a Completed operation.

The active conversion run persists the map in run history and report exports.
Rediscovery, inventory loading, scope/Excel selection changes, target version,
naming policy, schema policy and conversion-option changes clear the active
conversion session and require the map to be rebuilt before Data Migration can
run. A failed rebuild does not expose a partially constructed map.

## Files changed

- `SqlServerInventoryDiscoveryService.CoreMappings.cs`
- `ConversionModels.cs`
- `ConversionContracts.cs`
- `ConversionSession.cs`
- `PostgreSqlIdentifierMappingService.cs`
- `ConversionEngine.cs`
- `DataMigrationModels.cs`
- `DataMigrationPlanner.cs`
- `WorkspaceViewModel.cs`
- `WorkspaceView.xaml`
- `ConversionReportWriter.cs`
- identifier conversion, engine, planner and scale tests

