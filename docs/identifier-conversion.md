# Identifier conversion and reporting

## Single source of truth

`IIdentifierMappingService` and the mapper it creates are the authoritative registry for every
source-to-target identifier. Conversion registers schemas, relations, routines, columns,
constraints, indexes, sequences, routine parameters, result fields, triggers, generated trigger
functions, and helper objects before their names are consumed by data migration, deployment,
validation, or reporting.

Consumers must use the stored `IdentifierMappingEntry`. They must not independently lowercase,
shorten, or reconstruct a missing mapping. Data migration treats a missing column mapping as a
blocking configuration error. PostgreSQL double-quote escaping is centralized in
`PostgreSqlIdentifierQuoter`.

## Supported policies

| Policy | Behavior |
|---|---|
| `LowercaseUnquoted` | Normalizes to lowercase and quotes unsafe or restricted names. |
| `PreserveQuoted` | Preserves source case and quotes the identifier. |
| `QuoteOnlyWhenRequired` | Recommended default; normalizes ordinary names and quotes restricted or syntactically unsafe names. |
| `QuoteEveryIdentifier` | Preserves source spelling and double-quotes every identifier. |

Quoted mixed-case PostgreSQL identifiers are case-sensitive. Every later SQL reference must use
the same spelling and quoting from the registry.

## Restricted keywords and quoting

The registry contains an explicit keyword set for each supported PostgreSQL major version,
14 through 18. Reserved and context-sensitive keywords such as `freeze`, `user`, and `order` are
emitted as `"freeze"`, `"user"`, and `"order"`. Embedded double quotes are escaped by doubling
them. Single quotes are never used for identifiers.

## UTF-8 length and deterministic shortening

PostgreSQL's 63-byte identifier limit is measured with `Encoding.UTF8.GetByteCount`, never .NET
character count. A long name becomes:

`<longest complete-rune prefix>_<eight-character SHA-256 suffix>`

The suffix input includes the source database, fully qualified source identity, object kind, and
stable inventory identity. The algorithm never splits a Unicode rune and rechecks the final byte
length. PostgreSQL's silent truncation is not relied upon.

## Namespace and collision rules

Mappings model PostgreSQL namespaces:

- tables, views, sequences, indexes, types, and generated helper relations are schema-scoped;
- functions and procedures share a routine scope;
- columns, constraints, parameters, result fields, and triggers are owner-scoped;
- explicit schema consolidation maps source schemas to one target schema, after which relation
  collisions are resolved in that target namespace.

Case-normalization and schema-consolidation collisions receive a deterministic suffix and Warning
severity. A mapping that remains over 63 bytes, is a restricted word without required quoting, has
an unresolved collision, or requires manual correction is a blocking Error.

## Status and presentation

| Status | Severity | Presentation |
|---|---|---|
| Safe | Information | Green |
| Reserved word — safely quoted | Information | Yellow |
| Long identifier — automatically shortened | Warning | Amber/orange |
| Collision — automatically resolved | Warning | Amber/orange |
| Blocking identifier conflict | Error | Red with contrasting text |

Textual status is always exported for accessibility. `Identifier_Mapping.xlsx` contains filters,
frozen headers, a legend, byte and character lengths, quoting and collision flags, reason, suffix,
severity, and manual-review status. CSV, JSON, HTML, the desktop preview, and the consolidated
report use the same domain status.

## Deployment and validation gates

Pre-deployment assessment blocks:

- target names above 63 UTF-8 bytes;
- restricted keywords that were not quoted;
- unresolved or explicitly blocking mappings;
- duplicate names in the same PostgreSQL namespace.

Safely quoted keywords and deterministically shortened or collision-resolved mappings do not block.
Post-deployment validation uses the registry's exact schema, relation, and child names when
checking catalog objects, data, sequences, foreign keys, indexes, routines, permissions, and
comments. A missing or different deployed name is a blocking validation finding.

## Live verification

The opt-in `ReservedIdentifiers_AreCreatedCopiedIndexedReferencedViewedAndValidated` integration
test uses `MIGRATIONSTUDIO_POSTGRES_INTEGRATION`. It creates reserved-word identifiers, loads them
with PostgreSQL binary COPY, creates an index and foreign key, queries them through a view, checks
the exact catalog name, and rolls the transaction back. If no disposable PostgreSQL endpoint is
configured, the test is reported as skipped rather than passed.
