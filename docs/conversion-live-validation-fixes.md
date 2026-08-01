# Conversion fixes for PostgreSQL live validation

## Root causes

### SQL Server current-time functions

`StructuredSqlExpressionTranslator` recognized `GETDATE`, `GETUTCDATE`, and
`SYSDATETIME` as non-immutable functions, but omitted `SYSUTCDATETIME`.
Consequently, the tokenizer preserved `SYSUTCDATETIME()` in generated
PostgreSQL SQL. The existing `GETUTCDATE` mapping also used a different
expression from the required engine convention.

The translator now recognizes all four functions without modifying occurrences
inside string literals or comments:

| SQL Server | PostgreSQL |
|---|---|
| `GETDATE()` | `CURRENT_TIMESTAMP` |
| `GETUTCDATE()` | `timezone('UTC', now())` |
| `SYSDATETIME()` | `CURRENT_TIMESTAMP` |
| `SYSUTCDATETIME()` | `timezone('UTC', now())` |

### Identifier mapping

Primary-key, unique, foreign-key, and index names and key columns already called
the central `IIdentifierMapper`. Two gaps remained:

1. CHECK translation built its column map only from
   `ConstraintInventory.Columns`. SQL Server discovery can return an empty or
   partial list for a CHECK expression, so identifiers in its definition fell
   back to source spelling.
2. Programmable-object rewriting handled qualified `[schema].[object]`
   references, but not unqualified objects, `[table].[column]`, aliases, or
   three-part names consistently. This allowed source names to survive in
   views and procedures when the final target was normalized, shortened, or
   collision-resolved.

CHECK translation now receives every column of its owning table and maps each
through the published identifier mapper. Programmable-object rewriting now
resolves three-part and two-part objects, table-qualified columns, alias-qualified
columns when unambiguous, and object references following SQL object-position
keywords. Ambiguous identifiers are not guessed.

### Empty index definitions

`IndexConverter` previously accepted `IndexKind.Heap` and joined whatever
non-included columns were present. A heap or malformed/included-only index
therefore produced `USING btree ()`, which PostgreSQL rejects with syntax error
SQLSTATE `42601`.

A heap is now emitted as a non-executable informational artifact because it is
not an index object. A real index with no positive-ordinal key columns becomes
an explicit manual-review artifact with finding `empty index key list`; no
invalid `CREATE INDEX` is generated.

### Ordering

Ordering previously depended on the numeric enum value, which placed standalone
sequences before tables. A single `DeploymentPhaseOrdering` policy is now used
by conversion, live validation, and deployment:

```text
Schemas
Types
Tables
Defaults/generated columns
Primary keys
Unique constraints
Check constraints
Sequences
Data / sequence reset
Foreign keys
Indexes
Functions
Procedures
Views
Triggers
Security
Comments
```

Generated identity sequences are the only deliberate pre-table exception:
PostgreSQL resolves a `regclass` referenced by a column default while
`CREATE TABLE` is parsed, so that sequence is a genuine prerequisite. It is
written to `04_IdentitySequences.sql`; standalone sequences are written to
`10_Sequences.sql`.

Object dependencies remain authoritative within this nominal order. This is
required for valid SQL when an earlier-phase object genuinely references a
later-phase object.

### Incremental validation after reconversion

Reconversion previously replaced the active `ConversionRun` with offline-only
results, losing successful live results even when generated SQL was byte-for-byte
unchanged. `ConversionValidationResultReuse` now carries forward only results
that are:

- `Outcome = Passed`;
- `WasLiveValidated = true`;
- structurally valid; and
- keyed by an unchanged generated-SQL SHA-256 hash.

Failed, blocked, offline-only, cancelled, removed, and hash-changed artifacts
are never reused. The existing validator then executes only changed artifacts
and the dependency closure required to construct their validation environment.

## Before and after SQL

### Temporal default

Before:

```sql
CREATE TABLE nrega_sk.audit_log
(
    modified_at timestamp NOT NULL DEFAULT SYSUTCDATETIME()
);
```

After:

```sql
CREATE TABLE nrega_sk.audit_log
(
    modified_at timestamp NOT NULL DEFAULT timezone('UTC', now())
);
```

### Mapped CHECK constraint

Before:

```sql
ALTER TABLE nrega_sk.long_source_table
ADD CONSTRAINT CK_Source_Name CHECK ([OriginalColumn] <> N'');
```

After:

```sql
ALTER TABLE nrega_sk.long_source_table
ADD CONSTRAINT ck_source_name CHECK (originalcolumn <> '');
```

The actual spelling and quoting are taken from the immutable identifier mapping
set; the example shows lowercase-unquoted policy.

### Empty index

Before:

```sql
CREATE INDEX ix_empty ON nrega_sk.source_table USING btree ();
```

After:

```sql
-- Index ix_empty on nrega_sk.source_table has no key columns and was not emitted.
```

The artifact is marked for manual review with a specific metadata error rather
than sent to PostgreSQL.

### Mapped programmable-object reference

Before:

```sql
CREATE VIEW nrega_sk.current_items AS
SELECT 1 FROM [SourceTableNameThatWasShortened];
```

After:

```sql
CREATE VIEW nrega_sk.current_items AS
SELECT 1 FROM nrega_sk.sourcetablenamethatwasshorten_6f8b2e;
```

The target comes from `IIdentifierMapper`; the suffix is illustrative.

## Validation report

### Automated verification

Release tests:

```text
Passed: 309
Failed: 0
Skipped: 15
Total: 324
```

New regression coverage verifies:

- all four temporal mappings and literal/comment preservation;
- table, PK, CHECK, FK, index, view, and procedure identifier mapping;
- empty-key indexes never produce `()`;
- the required phase sequence in deployment;
- validation reuse only for unchanged successful SQL hashes;
- a live PostgreSQL sequence containing schemas, tables, PK, unique, CHECK,
  sequence, FK, index, function, procedure, and view.

### Live status

`PostgreSqlValidationIntegrationTests.ConversionRegressionSql_ValidatesWithoutPreviousSqlStates`
is the live regression. It asserts every result is `Passed`, live validated,
structurally valid, and has no SQLSTATE.

The local PostgreSQL 17 service is running, but no
`MIGRATIONSTUDIO_POSTGRES_INTEGRATION` connection is configured and a
passwordless connection was rejected with `no password supplied`. Therefore the
live regression was skipped in this run. No claim of zero production SQLSTATE
errors is made without executing that test against the designated validation
database.

No validation or deployment check was disabled or suppressed.

## Modified files

- `src/MigrationStudio.Application/Conversion/ConversionValidationResultReuse.cs`
- `src/MigrationStudio.Domain/Conversion/DeploymentPhaseOrdering.cs`
- `src/MigrationStudio.Infrastructure/Conversion/StructuredSqlExpressionTranslator.cs`
- `src/MigrationStudio.Infrastructure/Conversion/Converters/ConstraintConverter.cs`
- `src/MigrationStudio.Infrastructure/Conversion/Converters/IndexConverter.cs`
- `src/MigrationStudio.Infrastructure/Conversion/Converters/ProgrammableObjectConverter.cs`
- `src/MigrationStudio.Infrastructure/Conversion/ConversionEngine.cs`
- `src/MigrationStudio.Validation/GeneratedSqlValidator.cs`
- `src/MigrationStudio.Deployment/MigrationPackageWriter.cs`
- `src/MigrationStudio.Deployment/PostgreSqlDeploymentEngine.cs`
- `src/MigrationStudio.Desktop/ViewModels/WorkspaceViewModel.cs`
- `tests/MigrationStudio.Tests/Conversion/ConversionEngineTests.cs`
- `tests/MigrationStudio.Tests/Conversion/ConversionValidationResultReuseTests.cs`
- `tests/MigrationStudio.Tests/Conversion/ExpressionTranslationTests.cs`
- `tests/MigrationStudio.Tests/Conversion/ProgrammableObjectConverterTests.cs`
- `tests/MigrationStudio.Tests/Deployment/DeploymentPackageAndRecoveryTests.cs`
- `tests/MigrationStudio.Tests/Integration/PostgreSqlValidationIntegrationTests.cs`
