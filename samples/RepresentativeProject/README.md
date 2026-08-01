# Representative release fixture

This fixture is intended for an isolated SQL Server test database only. Run
`sqlserver-fixture.sql` in a newly created disposable database as a principal with DDL permission.
Run `sql-agent-fixture.sql` separately only when SQL Agent inventory is being tested and the
operator has the required `msdb` permissions.

The fixture covers multiple schemas, long identifiers, identity and computed columns, constraints,
indexes, filtered indexes, sequences, views, scalar and table-valued functions, procedures,
triggers, user-defined types, synonyms, extended properties, roles, temporal tables, partitioning,
sensitive-looking ordinary table columns, binary/large-text/Unicode data, and deliberately
unsupported or manual-review constructs.

No usable credential is included. The values in password/hash columns are deterministic test
payloads that must be preserved in the target table while remaining absent from diagnostics and
reports.

