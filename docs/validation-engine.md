# Post-migration validation engine

## Purpose and trust boundary

Post-migration validation is an independent reconciliation phase. A successful deployment only proves that PostgreSQL accepted the package; it does not prove semantic equivalence. Validation therefore consumes three immutable inputs:

1. the SQL Server inventory snapshot;
2. the conversion run, including the identifier and datatype mapping registries;
3. a fresh PostgreSQL catalog snapshot.

The engine never compares an original SQL Server name directly with a PostgreSQL name. Every source object and child identifier must resolve through the conversion mapping registry. Missing mappings produce `NotComparable`, not a guessed match.

Connection strings are execution inputs and are not stored in `ValidationRun`. Persisted configuration contains scope and policies only. Findings, reports, and query journals never contain sampled row values. Sensitive canonical values are replaced with one-way SHA-256 digests before they enter comparison state.

## Levels and scope

`ValidationLevel` supports inventory, structural, count, sampling, comprehensive, programmable-object, and full runs. Scope is an intersection of selected schemas, object types, and tables. The WPF workspace reuses its selected schema/table model, so validation cannot silently expand beyond the operator's migration scope.

Metadata reconciliation covers mapped schemas, tables, columns, views, routines, triggers, types, sequences, constraints, and indexes. PostgreSQL metadata includes:

- declared column type, size, precision, scale, nullability, identity, generated expression, and default;
- primary, unique, foreign-key, and check constraint validity;
- index validity, key columns, included columns, and predicates;
- routine/view/trigger definitions and ownership;
- sequence bounds, increment, cycle state, current state, and identity alignment;
- role memberships and table privileges.

The engine classifies every applicable comparison as `Equivalent`, `EquivalentWithExpectedTransformation`, `Warning`, `Mismatch`, `Missing`, `Extra`, `NotComparable`, or `ManualReview`.

## Data reconciliation

Count validation uses `COUNT_BIG` on SQL Server and `COUNT` on PostgreSQL. Sampling and comprehensive levels stream both readers; rows are never buffered as business objects. Canonical values feed:

- null counts;
- minimum and maximum canonical representations;
- numeric sums and averages;
- optional distinct counts;
- ordered SHA-256 reconciliation for primary-key tables;
- commutative multiset reconciliation, including duplicate multiplicity, for keyless tables.

Sampling is deterministic: primary-key tables order by mapped key columns. Keyless tables order by the selected columns when a sample limit is used. Comprehensive keyless validation uses a commutative multiset digest. Exact duplicate reconciliation can still be expensive and ambiguous operationally; select configured keys where a stable business key exists.

Foreign-key orphan checks execute only when configured. They retain the orphan count, never the offending values. Administrator-provided custom queries must be marked read-only and begin with `SELECT` or `WITH`. Only their canonical scalar result is compared; the journal stores hashes of the SQL text, duration, status, and a redacted error category.

## Constraints and sequences

PostgreSQL `convalidated` and `indisvalid` are checked independently from object existence. An existing but unvalidated foreign key or check constraint is not reported as fully equivalent.

For identity-backed sequences the engine resolves `pg_get_serial_sequence`, reads the maximum target key, and calculates the expected next value using the actual increment. A positive sequence at or below the maximum key, or a negative sequence at or above the minimum direction boundary, is a critical duplicate risk.

## Programmable objects and safe execution

Creation and definition presence are catalog checks only. Views, functions, procedures, and triggers remain `ManualReview` unless an administrator-approved semantic test case is associated with the object. The test-case model records parameters, expected shape/scalar/output values, source/target permissions, rollback policy, timeout, and sensitive parameters.

The engine does not execute arbitrary procedures. Administrator-approved read-only function tests can run on the authorized source and/or target inside transactions that are always rolled back; result shape and the first canonical scalar value are compared without retaining raw values. State-changing routine and trigger tests remain disabled. They require a separately approved isolated harness. This rule intentionally prevents a syntactically valid routine from making the database appear semantically validated.

Large views are not queried automatically. Use a bounded read-only validation query with deterministic ordering and an explicit limit.

## Security

The catalog reader inventories role memberships, ownership, and table privileges. SQL Server `DENY` entries are reported separately because PostgreSQL has no equivalent negative grant. Passwords are never expected, copied, queried, or reported.

Security review must also account for schema usage, sequence privileges, routine execution, default privileges, external identity providers, and ownership policies that are environment-specific.

## Readiness

Readiness is not an opaque percentage. Each category shows applicable, passed, warning, and blocker counts plus its configured weight and explanation:

- structural completeness;
- data reconciliation;
- constraints;
- programmable objects;
- security;
- unsupported features;
- manual-review completion.

Only evaluated categories participate in the weighted score. An unevaluated category makes the overall result `Incomplete`. Any error makes it `NotReady`; a critical blocker remains separately visible and prevents readiness regardless of a high weighted score.

## Persistence, UI, and reporting

Runs are written atomically to the application data `validation-runs` directory and link to migration/deployment run IDs when available. The WPF validation workspace contains configuration, progress, category scorecards, object and data grids, sequence state, constraints/routines, blockers, and manual-review findings.

Exports produce Markdown and Excel files. Reports contain hashes and aggregate evidence, not connection strings or sensitive row values.

## Operational limitations and mandatory manual checks

- Hashes have a non-zero collision probability and are evidence, not a formal proof.
- Case-insensitive SQL Server collation cannot be inferred per expression; configure canonical case folding only when the comparison contract requires it.
- XML normalization is structural formatting normalization, not XML-schema semantic equivalence.
- JSON property sorting is optional and does not make numerically or culturally different JSON values equivalent.
- Spatial values require a common SRID and representation policy; otherwise classify them `NotComparable`.
- Very large exact distinct counts and comprehensive keyless scans are expensive.
- Dynamic SQL, external calls, nondeterministic routines, CLR objects, jobs, and environment-dependent security require manual semantic validation.
- Procedures and triggers must not be called against production solely to obtain a validation result.

The product must never label a migration fully validated while an applicable category is incomplete or a programmable object has only passed creation/syntax checks.
