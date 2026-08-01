# Installation and distribution

Version 1.0.0 is distributed as:

- a self-contained Windows x64 directory, which includes the .NET 8 runtime;
- a framework-dependent Windows x64 directory, which requires a compatible .NET 8 Desktop Runtime;
- a self-contained portable ZIP;
- an x64 Windows Installer package.

The installer supports per-machine installation, Start menu integration, an optional desktop
shortcut, uninstall, repair, and major upgrades. Administrative approval is normally required for
per-machine installation.

Supported client operating systems are Windows 10 21H2 or later and Windows 11 on x64. Windows
Server 2019, 2022, and 2025 x64 are supported for administrative workstation use. The application
is DPI-aware and does not support Windows on Arm or 32-bit Windows in this release.

The self-contained and portable packages do not require Excel, SQL Server Management Objects,
PowerShell modules, or a separately installed .NET runtime. Excel is required only if an operator
wants to open generated `.xlsx` files.

Application data and logs are stored under the current user's local application-data directory.
Database payloads, packages, reports, and exports are written only to operator-selected locations.

Before installation, verify `checksums/SHA256SUMS.txt`. Version 1.0.0 artifacts are unsigned unless
the release process was supplied a real code-signing certificate; do not infer signing from the
presence of an installer.

Disk space must cover the application (approximately the published artifact size), inventory and
package metadata, reports, and temporary/checkpoint data. Data migration is streamed and does not
stage the entire database, but allow working space for logs, checkpoints, failed-row exports, and
generated packages.

