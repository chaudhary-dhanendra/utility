# Data migration engine

## Execution model

`IDataMigrationPlanner` converts the immutable discovery snapshot and conversion run into a
`DataMigrationPlan`. The plan fixes source/target identifiers, selected columns, generated-column
behavior, sensitivity classification, transfer strategy, stable resume key, dependency order,
target preparation, source predicate, and metadata hash before execution begins.

`IDataMigrationEngine` executes tables through `Parallel.ForEachAsync` with an explicit table limit.
Independent semaphores cap SQL Server readers and PostgreSQL writers. A source reader feeds one
bounded row/byte batch at a time to the target writer; the writer must finish before the reader can
produce another batch. This direct bounded pipeline provides backpressure and never creates one
task per row or preloads a table.

SQL Server commands use `SequentialAccess`, asynchronous reads, explicit transactions, cancellation
tokens, and async disposal. A batch is bounded by both `BatchRowCount` and `BatchByteSize`; a single
row is bounded by `MaximumRowSize`. Large values can therefore occupy at most the current bounded
batch rather than table-sized memory.

## Strategies

- PostgreSQL binary `COPY` is selected when all included column transports are supported.
- PostgreSQL text `COPY` uses invariant formatting and COPY escaping.
- Parameterized Npgsql batches use placeholders for every row value.
- A failed multi-row batch is retried for classified transient failures, bisected, and ultimately
  sent through a parameterized single-row fallback.
- `IDataValueTransformer` is invoked only for mappings it accepts. Custom transformation never
  authorizes arbitrary scripts.

No SQL statement contains a raw row value. Administrator predicates are the one intentional SQL
fragment; they reject terminators and comments and are stored in the hashed plan.

## Datatype transport

The built-in transport covers booleans, signed integer sizes, exact numerics, money, real/double,
date, time, datetime variants, datetimeoffset, Unicode and non-Unicode text, binary/image/rowversion,
UUID, XML, nulls, and alias types exposed through their provider base value. Text fallback uses
invariant culture. Binary values remain byte arrays and are never decoded.

JSON and spatial values require an explicit compatible target mapping or transformer. PostGIS
semantic conversion is not inferred from SQL Server spatial serialization.

Computed/generated columns marked as PostgreSQL-generated are excluded from the source projection
and target COPY column list. Ordinary populated and trigger-maintained columns can be represented
by plan mappings. A manual column stops or skips according to policy.

## Ordering and consistency

The planner derives parent dependencies from discovered dependencies and foreign keys. The default
workflow assumes deployment created tables without foreign keys, loads data, resets identity
sequences, then lets deployment add and validate foreign keys.

Parallelism is never applied within a table by default. A table is only marked partition-capable
when it has an explicit stable numeric or UUID key; partition execution is deliberately opt-in.

`SnapshotWhereAvailable` attempts a SQL Server snapshot transaction and falls back to read committed.
The UI and result must be interpreted as potentially changing source data after that fallback.
`SourceQuiesced` and externally configured database snapshots are administrator assertions, not
conditions the application silently claims to have established.

## Identity values

Identity columns are included in the explicit target column list, preserving migrated values.
After successful loads, the reset service reads the source and target boundary, respects positive
or negative increments, handles empty tables and non-default seeds, executes `setval`, and records
the exact reset script and selected boundary.

## Validation and reports

Row counts are always available. Optional validation compares per-column null counts and canonical
logical checksums. Checksum input has an explicit type tag, length framing, invariant numeric and
temporal representation, UTC normalization for offsets, UTF-8/base64 text framing, and hexadecimal
binary representation. Rows are ordered by a stable key; checksum validation is inconclusive if no
stable order exists. Sample validation uses the first configured count in stable-key order.

Excel and HTML data reports contain table metrics, connection peaks, effective parallelism,
validation outcomes, sequence resets, and redacted failures. They never contain row values.

## Manual scenarios

- Always Encrypted data that the SQL Server driver cannot expose without an administrator-provided
  column master key configuration.
- `EncryptByKey`, certificate, or symmetric-key values that must be decrypted and re-encrypted.
- Application ciphertext that must remain decryptable by a changed application key scheme.
- SQL Server spatial-to-PostGIS semantic conversion without a configured transformer.
- Tables without a single stable configured key after interruption; restart the table.
- Upsert without a discovered or explicitly configured target key.
- Table recreation when the converted table artifact itself requires manual review.
- Trigger-maintained generated values whose trigger enable/disable behavior is not explicitly
  configured.

TDE is not a row transformation. SQL Server decrypts database pages during ordinary authorized
reads, so transferred row values follow their normal column semantics.
