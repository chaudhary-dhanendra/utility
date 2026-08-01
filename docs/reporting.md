# Reporting and migration dashboard

The reporting subsystem creates a synchronized, sanitized evidence package from the immutable
discovery, conversion, data-migration, deployment, validation, and manual-review run models. It
does not reconnect to either database and does not reconstruct results from UI state.

## Package layout

Every generation writes a `Reports` directory containing:

- `MigrationExecutiveSummary.html` - self-contained offline dashboard with navigation, search,
  filtering, expandable SQL, scorecards, timelines, blockers, reconciliation, and analytical
  charts.
- `MigrationExecutiveSummary.pdf` - management-oriented cover, summary, scope, architecture,
  results, risks, recommendations, and sign-off report.
- `MigrationReport.xlsx` - detailed operational workbook with an executive summary and at least
  39 inventory, mapping, migration, deployment, validation, and review worksheets.
- `MigrationReport.json` - the complete machine-readable report envelope.
- `ObjectInventory.csv`, `IdentifierMapping.csv`, `ManualReview.csv`,
  `UnsupportedFeatures.csv`, `DeploymentFailures.csv`, and `DataReconciliation.csv` - focused
  UTF-8 CSV extracts for downstream tooling.

The formats share one `MigrationReportDocument`, so headline totals and detailed results are
consistent. Missing optional phases produce explicit empty sections rather than preventing report
generation.

## Architecture

`IMigrationReportEngine` is the application boundary. `MigrationReportDocumentBuilder` projects
real session results into the versioned domain report. Format writers are isolated in
`MigrationStudio.Reporting`; UI code only supplies a validated template, an output directory, and
progress handling.

Generation is asynchronous. CPU-bound Excel and PDF work runs away from the WPF dispatcher, while
text artifacts use asynchronous file I/O. A report-generation history record is persisted only
after all ten artifacts are complete. Historical report payloads can be reopened and regenerated
without requiring the original live sessions; regeneration receives a new run identifier and
retains a reference to its source report run.

## Excel behavior

The workbook uses ClosedXML and never uses Excel COM automation. Detail ranges are emitted as
filterable tables with frozen headers, wrapped definitions, bounded readable column widths,
severity-based conditional formatting, and summary hyperlinks. Worksheet names are sanitized for
Excel restrictions and kept within 31 characters.

The writer honors Excel's 1,048,576-row limit. When a dataset would exceed the configured sheet
capacity, it creates numbered continuation sheets and repeats the header. Tests use a small
capacity to exercise this behavior without allocating a production-sized inventory.

## HTML and charts

The HTML file embeds all CSS and JavaScript and has no CDN, font, analytics, or network dependency.
All model text is HTML encoded. Its charts cover objects by type, conversion classification,
deployment and validation outcomes, findings by severity, rows migrated by schema, phase
durations, slow tables, throughput, manual-review status, and unsupported-feature categories.

## Templates

`ReportTemplate` supports the built-in `professional-light` and `dashboard-dark` templates plus
organization, logo path, project, title, preparer, reviewer, classification, footer, page-number,
and date/time settings. `IReportTemplateValidator` validates identifiers, length limits, date
formats, and logo existence. Templates are data only; they cannot contain scripts, expressions,
assemblies, or arbitrary executable code.

## Manual review and run history

Manual-review items use the statuses `Open`, `InProgress`, `Resolved`, `AcceptedRisk`, and
`NotApplicable`. Resolution-like states require a recorded resolution and reviewer. Items may be
assigned, commented on, supplied with target SQL, marked reviewed, filtered by unresolved blocker,
and explicitly reopened.

Run history stores discovery, conversion, data migration, deployment, validation, and report
generation metadata with versioned JSON payloads. Writes are atomic. The dashboard presents recent
runs, opens their persisted metadata, and can regenerate a selected report-generation run.

## Sensitive-data controls

All report models pass through the centralized sensitive-data redactor before writing. The same
redactor protects history payloads and exported JSON-lines logs. Passwords and connection-string
secrets are replaced, while run/correlation identifiers and diagnostic stack traces remain useful.
Report writers do not emit source or target connection strings or failed-row values. A generated
package should still be handled according to its configured classification marking.

## JSON compatibility

`MigrationReport.json` has a top-level `reportSchemaVersion`. Version 1 includes run metadata,
summary, inventory and object results, findings, mappings, data results, deployment results,
validation scorecards, metrics, manual-review decisions, and template metadata. Consumers must
reject unsupported major schema versions rather than guessing field semantics.

## PDF library and licensing

The PDF implementation uses PDFsharp/MigraDoc 6.2.4. The library supports .NET 8 and is distributed
under the permissive MIT License, allowing commercial use and redistribution without royalties.
Commercial distributions must retain the library's copyright and MIT permission notice in their
third-party notices. See the repository's `THIRD-PARTY-NOTICES.md`; legal review remains part of
the product release process.

## Verification and sample

Reporting tests cover exact package contents, workbook sheets and continuation, worksheet-name
sanitization, HTML encoding, PDF signatures, JSON schema version, cross-format data consistency,
severity rules, secret redaction, partial runs, manual-review transitions, log export, persisted
history, and historical regeneration.

A representative sanitized package is generated into `samples/SanitizedReportPackage/Reports`.
It contains intentionally synthetic server and database names and no usable credentials.
Identifier mapping exports use the shared domain status model described in
[identifier-conversion.md](identifier-conversion.md). Safe mappings are green, safely quoted
keywords yellow, automatic shortening/collision resolution amber, and only blocking mappings red.
Every color has a corresponding textual status.
