# PostgreSQL deployment engine

## Workflow

Deployment is a gated pipeline:

1. Read and hash-verify the versioned migration package.
2. Assess the maintenance or target connection, server version, role capabilities, installed and
   available extensions, package blockers, dependency order, identifier collisions, and conflicts.
3. Apply the configured pre-deployment policy. Administrator overrides require a confirmation and
   reason and are written to the audit journal.
4. Optionally create, reuse, rename, or explicitly drop and recreate the target database through a
   maintenance connection.
5. Execute selected structured package artifacts in phase and dependency order.
6. Invoke the data migration engine at phase 10 when a live data request is attached.
7. Run explicitly selected post-deployment maintenance and finalize the journal.

`GenerateOnly` verifies the generated package without connecting. `ValidateOnly` performs package
and target assessment without executing SQL. Existing packages use the same reader and executor as
packages created in the current workspace.

## Connection and capability assessment

`PostgreSqlConnectionOptions` supports host, port, maintenance and target databases, user/password,
SSL mode, root/client certificates, timeouts, keepalive, pooling, application name, and search path.
Passwords are passed only to Npgsql and are replaced with `***` in user-visible connection text.

The connection service reads the server version, current identity, `CREATEDB`, `CREATEROLE`,
superuser status, database `CREATE` privilege, role memberships, installed extension versions, and
available extensions. All operations accept cancellation tokens.

## Execution order

The phase order is 00 pre-deployment through 21 validation. Inside a phase, artifacts are
topologically ordered using manifest dependencies. A failed or rolled-back prerequisite blocks
dependent objects. Cross-phase dependencies that point to a later phase are preflight errors.
Strongly connected components remain explicit manual blockers unless the conversion package
contains a semantically valid resolution; the executor does not invent stubs.

Required extensions are checked before deployment and installed with guarded `CREATE EXTENSION`
when configured. Unavailable extensions block dependent deployment.

## SQL parsing

Structured artifact SQL embedded in the manifest is preferred. Externally edited script files are
still hash checked and can be parsed by `PostgreSqlScriptParser`. The parser recognizes:

- single and escape strings, including doubled quotes and backslash escapes;
- quoted identifiers;
- line comments and nested block comments;
- tagged and untagged dollar-quoted bodies;
- `DO`, function, procedure, and trigger-function bodies.

Semicolons inside those constructs do not split statements. Unterminated constructs fail parsing.

## Transactions and errors

The default is transaction per object. Transaction-per-phase and safe single-transaction selection
are supported when every statement in the boundary is transaction-safe. No-wrapping mode records
each successful statement/object as nontransactional.

Failures record SQLSTATE, severity, hint, position, schema/table/column/constraint/datatype metadata,
timestamps, retries, script and SQL hash. Provider detail is redacted when it could echo application
data. SQL row values, passwords, and statement text are not written to reports.

Only connection, serialization, deadlock, resource, and server-shutdown SQLSTATEs are retryable.
Retry uses a bounded exponential delay. Syntax, missing-object, missing-column, and permission
failures are permanent.

## Conflicts and idempotency

Schemas can be reused under `SkipWhenEquivalent`. Guarded extensions, grants, comments, and
`CREATE OR REPLACE` artifacts are recognized as idempotent. Tables containing data cannot be
dropped without the explicit destructive confirmation. Programmable objects may use
`ReplaceWhenSafe`; rename decisions require regenerated identifier mappings rather than unsafe SQL
text replacement.

For tables, types, and overloaded routines where equivalence cannot be proven from portable catalog
metadata, the engine deliberately treats equivalence as unknown and follows the configured fail,
manual, or destructive policy.

## Post-deployment

Configured post-deployment work includes sequence alignment through the data engine, constraint
deployment/validation phases, optional `ANALYZE`, separately confirmed `VACUUM ANALYZE`, extension
verification through assessment, role/grant execution, and journal finalization. Expensive vacuum
work is disabled by default and visible in both options and the journal.
