# SQL Server to PostgreSQL Migration Studio 1.0.5

## Runtime identifier-map correction

- Uses `ColumnIdentifierKey(TableObjectId, ColumnId)` as the shared producer and
  Preview Plan lookup identity for SQL Server columns.
- Preserves the discovered column object ID as supporting identity and records
  both IDs in the mapping report.
- Publishes mapping schema version 2 with a unique mapping-set ID, publication
  timestamp, map counts, and cache provenance.
- Rejects legacy mapping snapshots and automatically reconverts an active
  inventory before Preview Plan when its mapping schema is stale.
- Requires included-column and mapped-column counts to match before the
  conversion map can be published.
- Adds sanitized lifecycle diagnostics for the production
  `[nrega_SK].[verify_observe1819].discre_obsrv` case at discovery, mapping
  creation, conversion completion, publication, rebuild, and Preview Plan.

## Production evidence

The previous 1.0.3 runtime created a top-level mapping keyed by column object ID
`09480727-c89a-5afb-bc67-0d95ca5177ce`, while Preview Plan requested parent
table object ID `e20dc7da-e0b9-5230-82a4-a8b16d0002a0` plus a column name.
Version 1.0.5 uses the same stable key at both boundaries:

`Column|tableObjectId=e20dc7da-e0b9-5230-82a4-a8b16d0002a0|columnId=4`

