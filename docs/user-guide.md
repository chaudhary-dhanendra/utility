# User guide

SQL Server to PostgreSQL Migration Studio 1.0.0 guides a migration through discovery, selection,
conversion, package generation, target deployment, data transfer, validation, and reporting.

The main workspace is organized by phase. Complete each phase from left to right and retain the
generated inventory, package, checkpoint, deployment journal, validation run, and report package
as the audit trail for the migration.

Credentials are entered for the current operation and are not written to settings, inventories,
packages, reports, or run history. Connection strings shown in diagnostics are redacted.

Use the operation status area to monitor long-running work. Pause and resume are available during
data transfer; cancellation is cooperative and may wait for the current database command or
transaction boundary. Destructive target preparation and overwrite behavior require explicit
confirmation.

The Reports area combines current run state with persisted history. It supports report generation,
manual-review ownership and resolution, sanitized log export, and regeneration of historical
report runs.

Theme choices are System, Light, and Dark and are persisted per Windows user. Keyboard navigation,
standard access keys, high-contrast-compatible dynamic resources, and DPI-aware layout are used
throughout the WPF shell.

See the focused workflows in `quick-start.md`, `complete-database-migration.md`,
`excel-selected-table-migration.md`, and `manual-selection.md`.

