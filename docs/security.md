# Security

## Credentials and secrets

Source and target passwords are operation inputs and are not stored in application settings,
inventory snapshots, migration packages, run history, reports, or exported logs. Connection-string
persistence and SQL Server MARS are disabled. Logs use structured redaction for password, secret,
token, and connection-string patterns.

The application does not intentionally create process-memory crash dumps. Fatal diagnostics are
sanitized text logs. Administrators should also configure Windows Error Reporting and endpoint
diagnostic tooling so full-memory dumps are not collected from database migration workstations.

## Files and exports

Settings, inventories, checkpoints, validation runs, history, and journals use atomic replacement
patterns. Migration package files and structured SQL are verified with SHA-256 before deployment,
and manifest paths are required to remain inside the package root.

Failed-row export is opt-in. Sensitive values are masked by default; unmasked export is an explicit
operator action and must be stored in an access-controlled location. Ordinary password/hash table
columns are migrated byte-for-byte and are never treated as login credentials.

Excel and CSV text beginning with spreadsheet formula prefixes is escaped. HTML report content is
encoded and the self-contained report does not load remote scripts.

## Plugins and updates

External plugins are disabled by default. When enabled, production configuration requires a valid
Windows Authenticode signature; an optional publisher-thumbprint allowlist can further restrict
trust. Plugin assemblies and manifest paths must remain under the plugin directory.

Version 1.0.0 does not implement automatic download or installation of updates. This avoids an
unsigned or rollback-unsafe partial updater. Operators manually obtain a release from the
publisher, verify its signature when present and its SHA-256 checksum, then use MSI major upgrade
or replace the portable directory.

## Signing

`build/release.ps1` signs the executable and MSI only when a real certificate thumbprint is
supplied and Windows `signtool.exe` is available. A release without those conditions is explicitly
reported as unsigned.

