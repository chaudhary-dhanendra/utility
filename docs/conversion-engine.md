# Conversion engine

## Scope

The conversion engine transforms an immutable SQL Server `InventorySnapshot` into an immutable `ConversionRun`. It does not connect to SQL Server and does not mutate PostgreSQL during normal conversion.

Supported target majors are PostgreSQL 14 through 18. Every included, non-system inventory object produces at least one artifact. Unsupported objects are represented by manual-review or unsupported artifacts containing the preserved source definition; an absent converter cannot silently drop an object.

## Pipeline

```text
Inventory
  → resolved inventory scope
  → dependency graph and cycle findings
  → deterministic identifier allocation
  → centralized datatype mapping
  → converter registry
  → offline SQL structural validation
  → dependency-aware phase ordering
  → deployment package
  → Excel and HTML reports
```

`IObjectConverter<InventoryObject,string>` implementations are registered in deterministic strategy order. The fallback converter must be last and treats unknown objects as manual or unsupported. The engine fails composition if this invariant is broken.

## Artifact contract

Each `ConversionArtifact` records:

- stable source and target identifiers;
- preserved source definition and generated PostgreSQL;
- classification, deterministic rule ID, confidence, and content hash;
- findings and unsupported constructs;
- source and mapped target dependencies;
- required extensions;
- manual-review state;
- offline/live validation evidence;
- deployment phase and script file.

An edited SQL artifact in the UI is reclassified under `USER.MANUAL_EDIT` and marked unvalidated until it is validated again.

## Identifier allocation

All schemas, objects, columns, constraints, indexes, sequences, routine parameters, trigger helper functions, and generated identity sequences use `IIdentifierMapper`.

The complete identifier policy, UTF-8 shortening rules, PostgreSQL namespaces, reporting statuses,
and deployment gates are documented in [identifier-conversion.md](identifier-conversion.md).

The mapper:

- measures UTF-8 bytes and enforces PostgreSQL's 63-byte default;
- preserves names within the limit unless normalization or collision handling is required;
- uses an eight-hex-character SHA-256 suffix;
- allocates in stable source-ID order;
- detects lowercase-normalization collisions and resolves them deterministically;
- quotes reserved words and unsafe identifiers;
- supports lowercase-unquoted and preserve-quoted modes;
- applies preserve, `dbo`→`public`, consolidated, or custom schema policies.

Mappings are exported to `Identifier_Mapping.xlsx`, `Identifier_Mapping.csv`, and the main report.

## Object families

Converters exist for schemas; tables/columns/defaults/identity/computed columns; constraints; indexes; sequences; user-defined types; views/functions/procedures/triggers; security; and a mandatory fallback.

Primary/unique/check/foreign-key constraints are emitted separately. Foreign keys default to the post-data phase. Constraint/index storage properties that have no PostgreSQL semantic equivalent become findings. Primary-key and unique-constraint backing indexes are not duplicated.

Extended `MS_Description` properties produce `COMMENT ON` artifacts for supported objects and columns. Other properties remain report findings.

## Validation

Offline validation scans generated SQL while respecting string, quoted-identifier, dollar-quoted, line-comment, and block-comment states. It detects unbalanced delimiters, unterminated regions, empty SQL, and SQL Server `GO` separators.

Optional live validation uses Npgsql. It executes artifacts in order inside an always-rolled-back transaction and captures PostgreSQL SQLSTATE, error text, and position. Manual-review artifacts are not presented as live validated.

## Package layout

`MigrationPackageWriter` creates a uniquely named directory containing `manifest.json`, phases `00` through `20`, `10_Data`, `ManualReview`, `Reports`, and `Logs`. Scripts include source database, target version, generation time, engine version, object context, classification, rule ID, and content hash.

The manifest is the machine-readable provenance record. Manual-review files contain findings, generated skeleton or proposed SQL, unsupported constructs, and source T-SQL.

## Determinism

For identical inventory and options:

- stable source IDs drive identifier allocation;
- artifact SQL and hashes are identical;
- deployment ordering is stable;
- cycle fallback order is stable and visible through findings;
- the conversion run ID is derived from snapshot identity and serialized options;
- report row order is stable.

## Current limitations

- The tokenizer/transformer is not a complete T-SQL grammar implementation.
- Complex procedural control flow, dynamic SQL, multiple result sets, table variables, cursors, `MERGE`, `OUTPUT`, and SQL Server error/transaction edge cases remain manual.
- Multi-statement TVFs are emitted as manual-review skeletons.
- `TOP PERCENT`, `WITH TIES`, complex `APPLY`, `FOR XML PATH`, and unsupported cross-server queries remain manual.
- SQL Server partition/filegroup placement is reported, not recreated.
- Generated-column immutability is assessed by deterministic rules; live PostgreSQL validation is still recommended.
- Live validation requires an explicitly configured non-production PostgreSQL instance.
