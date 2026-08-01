# SQL Server to PostgreSQL Migration Studio 1.0.6

## Complete pre-conversion identifier map

- Builds mappings for every included source object before converting the first
  artifact.
- Uses the authoritative inventory object graph when an optional facet
  collection is incomplete.
- Represents table triggers with a typed key containing trigger object ID,
  parent table object ID, source schema ID, and source name.
- Associates PostgreSQL trigger-name allocation with the mapped parent table.
- Upgrades early child-name reservations with authoritative object IDs instead
  of losing identity when constraint metadata arrives later.
- Resolves duplicate target names without replacing distinct source-key
  entries.
- Publishes mapping schema version 3 with per-object-type coverage,
  auto-recovery count, and unresolved-required count.
- Rejects conversion-session publication unless all required coverage counts
  match.

## Production verification

The persisted `vbgramg` inventory produced:

- 238,453 identifier mappings
- 35,767 conversion artifacts
- 156,617/156,617 columns mapped
- 24/24 table triggers mapped
- 1,074/1,074 default constraints mapped
- 24 deterministic auto-recoveries
- zero unresolved required mappings

`[nrega_SK].[TRG_DigiPay_TrainerDetailsHistory_Del]` is mapped as:

`Trigger|objectId=c6bd26f1-05e2-5f2a-b850-d4475f3f8bbf|parentId=2cba4e36-c9a6-5084-b09c-477c61fd42d1|schemaId=dc58909f-7b77-5de2-9e2e-2ba4fc796a6d|name=TRG_DigiPay_TrainerDetailsHistory_Del`

to:

`nrega_sk.trg_digipay_trainerdetailshistory_del`
