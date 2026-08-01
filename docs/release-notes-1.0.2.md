# SQL Server to PostgreSQL Migration Studio 1.0.2

## Reliability fixes

- Corrected SQL Server 2022 catalog discovery queries for external tables,
  server-level triggers, column encryption key values, and external file formats.
- Corrected the conversion result Source SQL binding so immutable SQL is displayed
  read-only without a WPF TwoWay binding failure.
- Preserved successful conversion artifacts and operation completion when result
  presentation fails.

## Data migration connection experience

- Replaced raw PostgreSQL connection-string entry with structured host, port,
  database, username, and masked password fields.
- Added connection validation, cancellable live connection testing, SSL and
  timeout settings, pooling control, and sanitized status reporting.
- Data migration Preview and Start now build their connection string internally
  with `NpgsqlConnectionStringBuilder`.
