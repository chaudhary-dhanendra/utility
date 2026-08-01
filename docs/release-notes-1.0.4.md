# SQL Server to PostgreSQL Migration Studio 1.0.4

## Identifier mapping correctness

- Fixes SQL Server `char(2)` catalog discriminator padding that could classify
  user tables as `Unknown` and omit ordinary table-owned column mappings.
- Generates the canonical PostgreSQL identifier map eagerly for every included
  schema, object, column, constraint, index, trigger, module field and type
  field.
- Adds typed source keys backed by stable SQL Server object and parent IDs.
- Enforces PostgreSQL-safe normalization, reserved-word handling, deterministic
  namespace-aware collision resolution, and the 63-byte UTF-8 limit.
- Prevents conversion from completing until all included objects and columns
  have valid mappings.
- Automatically regenerates an unexpectedly missing Data Migration mapping and
  records the recovery without blocking Preview Plan.

## Reporting and UI

- Adds mapping totals, actions, source IDs, collision details, warnings and
  auto-recovery information to Excel, CSV, JSON, HTML and the Convert grid.
- Adds mapping report filters and Convert-page commands to view or export the
  complete identifier map.
- Uses streaming Excel output for large mapping inventories.
- Invalidates an active map when discovery, scope, naming or conversion options
  change, preventing stale-map use.

## Qualification

- Adds regression coverage for the production
  `[nrega_SK].[verify_observe1819].discre_obsrv` case.
- Adds deterministic, collation, normalization, completeness, recovery and
  report tests.
- Expands the release scale gate to 6,000 tables, 180,000 columns and more than
  191,000 canonical identifier mappings.

