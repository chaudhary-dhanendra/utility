# Troubleshooting

## Startup configuration error

Correct the named value in `appsettings.json`. Startup validation rejects unsafe queue capacity,
timeouts, parallelism, batch sizes, checkpoint frequency, PostgreSQL version, update channel, and
plugin trust combinations.

## SQL Server connection fails

Verify DNS, TCP port, firewall, encryption policy, certificate trust, authentication mode, database
visibility, and login permissions. Do not disable encryption or trust an unknown certificate in
production merely to suppress an error.

## PostgreSQL connection or deployment fails

Verify host/port, SSL mode, certificate files, maintenance database, database ownership, schema
privileges, extension permissions, target version, and package integrity. Use SQLSTATE and the
deployment journal to distinguish authentication, transient, conflict, and script failures.

## Migration pauses or stops

Review the operation status and logs, preserve the checkpoint, correct the underlying error, and
resume only when source/target identities and plan hashes still match. Do not edit checkpoints.

## Reports or Excel files fail to open

Confirm the report directory is writable and has adequate disk space. Generated HTML is offline.
Excel is not required for generation. PDF font resolution requires normal Windows system fonts.

## Logs

Logs are under the per-user local application-data directory. Use the dashboard's sanitized-log
export when sharing diagnostics. Retain correlation and run IDs.

