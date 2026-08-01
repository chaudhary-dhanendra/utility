# Known limitations

Version 1.0.0 has the following explicit limitations:

- SQL Server 2017-2025 are the primary supported sources. SQL Server 2016 catalog compatibility is
  best effort and requires a supported service level; older releases are not supported.
- PostgreSQL targets are limited to versions 14-18.
- Dynamic SQL, CLR objects, Service Broker, replication topology, linked-server behavior, SQL
  Agent execution semantics, SSIS/SSRS/SSAS assets, and external provider behavior require manual
  redesign or exclusion.
- SQL Server security is reported and converted conservatively; Windows principals, server roles,
  credentials, keys, certificates, and equivalent authentication policy require manual work.
- T-SQL translation is structural and token-aware but is not a complete SQL Server runtime
  emulator. Complex procedural behavior requires target testing.
- Automatic updates are intentionally not implemented. Updates are manual and checksum/signature
  verified.
- External plugins are disabled by default and require Authenticode trust when enabled.
- The release is Windows x64 only. There is no macOS, Linux, Arm64, or 32-bit desktop build.
- Installer artifacts are unsigned unless the release build receives a valid signing certificate.
- Live SQL Server/PostgreSQL integration and end-to-end results depend on externally supplied,
  disposable test instances and cannot be inferred from unit tests.

