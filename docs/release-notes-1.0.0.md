# SQL Server to PostgreSQL Migration Studio 1.0.0

Release date: 2026-07-24

## Release scope

Version 1.0.0 provides SQL Server catalog discovery, scoped selection, PostgreSQL conversion,
streamed data migration, migration package generation and integrity verification, PostgreSQL
deployment, post-migration validation, manual-review workflow, run history, and synchronized
HTML/PDF/Excel/JSON/CSV reporting in a .NET 8 WPF desktop application.

## Release hardening

- nullable reference types, recommended analyzers, deterministic builds, and warnings-as-errors;
- central package version management with per-project lock files;
- assembly, file, informational, and product version 1.0.0 metadata;
- bounded operation queues, migration channels, and connection concurrency;
- iterative linear-time dependency-component analysis validated with a 10,000-object chain;
- disabled-by-default external plugins with Authenticode and optional publisher allowlisting;
- SHA-256 migration-package verification and contained-path validation;
- sanitized structured logs and report/history redaction;
- formula-injection escaping for Excel and CSV outputs;
- self-contained HTML encoding and no remote report dependencies;
- correlated crash diagnostics with fatal-condition classification and optional log-folder access;
- asynchronous window-layout persistence during shutdown;
- production configuration validation at startup; and
- WiX-based x64 MSI, self-contained, framework-dependent, and portable packaging.

## Verified automated results

The Release build completes with zero compiler warnings and zero errors. The deterministic suite
passes 194 tests. Seven live-database integration tests are present but skipped unless disposable SQL
Server and PostgreSQL connection variables are supplied.

The reporting package is generated from a representative sanitized in-memory run. Its 39-sheet
workbook, three-page PDF, JSON schema, HTML/CSV outputs, historical regeneration, and secret
redaction are covered by automated and rendered artifact checks.

## Representative end-to-end status

The repository contains a realistic SQL Server fixture under `samples/RepresentativeProject`.
This workstation was not supplied disposable SQL Server/PostgreSQL endpoints, so the full live
Discovery -> Conversion -> Package -> Target creation -> Deployment -> Data migration ->
Programmable deployment -> Validation -> Reporting sequence was not executed for this release
candidate. Accordingly, live connection, deployment, value-preservation, and validation acceptance
items remain environment-dependent and are not claimed as passed.

## Production-scale qualification

On 2026-07-24, the deterministic scale harness ran on an Intel Core i9-14900HX workstation with
32 GB physical RAM, 32 logical processors, Windows 10.0.26200, x64, and .NET 8.0.29.

The application inventory/report pipeline was tested with a synthetic in-memory catalog containing
6,000 tables, 72,000 columns, 18,000 constraints, 6,000 indexes, and 12,000 dependency edges.
Catalog construction completed in 494 ms with 44.5 MiB peak managed memory. Snapshot save completed
in 796 ms, and reload completed in 1,130 ms with 101.7 MiB peak managed memory.

A separate dependency workload of 50,000 object IDs and 200,000 edges completed in 589 ms with
200.8 MiB peak managed memory and produced deterministic results when inputs were reversed. Excel
matching completed in 669 ms. Excel/HTML/PDF generation for the 6,000-table/72,000-column inventory
completed in 9.286 seconds with 1,323.3 MiB peak managed memory. Exact current file sizes are
recorded in the machine-readable scale report.

Graph, Excel, report, and bounded streaming cancellation were acknowledged in 23–234 ms. Atomic
checkpoint save and reload was exercised for 6,000 table states.

These are synthetic application-path measurements, not live database throughput claims. A
three-million-row bounded transform/checksum workload ran locally, but live SQL Server discovery,
SQL execution-plan inspection, PostgreSQL binary/text COPY, deployment, validation, disconnect
recovery, and multi-million-row database migration were skipped because disposable integration
endpoints were not supplied. Rendered WPF interaction automation was also not reproducible in the
headless harness. The release therefore remains unvalidated for the customer's complete live
workload until those gates pass.

Detailed criteria and evidence are in `scale-acceptance-criteria.md`,
`production-sizing-6000-tables.md`, and `artifacts/benchmarks`.

## Identifier hardening

Identifier mapping is now the mandatory registry used by conversion, COPY/INSERT data loading,
deployment checks, post-deployment validation, and reporting. PostgreSQL 14–18 keyword registries,
central double-quote escaping, four explicit case/quoting policies, UTF-8 byte-aware deterministic
shortening, and PostgreSQL namespace-aware collision resolution are covered by automated tests.

The 2026-07-25 identifier scale run generated XLSX, CSV, paged offline HTML, and JSON reports for
102,020 mappings. It contained 299 deterministically shortened identifiers, completed in
27.208 seconds, and reached 1,476.4 MiB peak managed memory. The dedicated workbook was 9,950,604
bytes. A separate BenchmarkDotNet short run mapped 100,000 identifiers in a 257.1 ms mean with
202.43 MB allocated per operation.

Reserved-word and automatic transformations are non-blocking when safely resolved. The deployment
assessment now blocks overlength output, unquoted restricted words, unresolved collisions, and
duplicate names in the same PostgreSQL namespace. The opt-in PostgreSQL test for reserved columns,
binary COPY, indexes, foreign keys, views, and catalog-name verification was added but was skipped
on this workstation because `MIGRATIONSTUDIO_POSTGRES_INTEGRATION` was not configured.

## Packaging and signing

Release output is produced under `artifacts/release/1.0.0`. The packaging script generates SHA-256
checksums and refuses to overwrite an existing version directory. It signs the executable and MSI
only when a real certificate thumbprint is supplied and `signtool.exe` is available.

The artifacts produced in the local verification run are unsigned because no signing credential
was provided. This is explicit and must be resolved before a publisher requiring signed production
distribution ships the files.

## Updates

There is no automatic updater in 1.0.0. Releases are obtained manually from the publisher and
verified by signature, when provided, and SHA-256 checksum. This avoids shipping an incomplete
unsigned update channel.

## Known limitations

See `known-limitations.md`. Dynamic SQL, CLR, external integrations, SQL Agent execution behavior,
server-level security, and other SQL Server-specific features can require manual conversion.
