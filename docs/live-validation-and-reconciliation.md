# Live PostgreSQL validation and final reconciliation

## Validation boundary

`IGeneratedSqlValidator` remains the single generated-SQL validation service.
Its live path now attempts to create a uniquely named disposable PostgreSQL
database through the configured maintenance database. It deploys executable
artifacts in deterministic dependency order, records every PostgreSQL error,
continues with independent artifacts, and marks dependent artifacts
`BlockedByDependency`.

If the role cannot create databases, the validator can use a rollback-only
transaction in the explicitly configured target. Every result records
`RollbackTransaction` confidence so this fallback is never represented as
equivalent to disposable-database validation.

The connection string and password are transient inputs. They are not fields
of a validation result, conversion artifact, package manifest, deployment
journal, or report.

## Production deployment gate

Desktop production deployment enables
`DeploymentOptions.RequireLivePostgreSqlValidation`. A package can deploy only
when every selected executable, non-manual artifact contains a successful live
validation result. Because packages are immutable, run validation and export a
fresh package afterward. Package manifest format 5 stores the validation
evidence.

Pre-deployment policy interpretation is centralized in
`DeploymentBlockingPolicy`. `CanDeploy`, blocker counts, and the Simple Mode
message use the same decision.

## Object reconciliation

Report schema version 2 contains one `SourceObjectReconciliation` record for
every discovered inventory object. Each record has exactly one status:

- `ConvertedDeployedValidated`
- `ConvertedValidationFailed`
- `ManualConversionRequired`
- `Unsupported`
- `ExcludedExplicitly`
- `NotApplicableToPostgreSql`
- `Unreconciled`

`Unreconciled` is an audit state, not a successful final category. If any
record is unreconciled, `ReconciliationSummary.IsBalanced` is false and report
readiness is forced to `Incomplete - object totals do not reconcile`.
Child objects implemented by a parent artifact (for example, inline table
facets) follow the nearest parent artifact for deployment and validation while
retaining their own source identity in the ledger.

The report package includes `ObjectReconciliation.csv`; the same records and
summary are present in `MigrationReport.json` and the HTML executive report.
Row reconciliation separately proves:

`RowsRead = RowsWritten + RowsRejected`

## Manual verification

1. Configure `MIGRATIONSTUDIO_POSTGRES_INTEGRATION` for a disposable test
   cluster and run the integration suite.
2. Validate a package with a role that has `CREATEDB`; confirm every executable
   artifact reports `DisposableDatabase` confidence and the temporary database
   is removed.
3. Repeat with a role without `CREATEDB`; confirm
   `RollbackTransaction` confidence is visible.
4. Introduce one invalid independent artifact and one dependent artifact;
   confirm the former is `Failed`, the latter is `BlockedByDependency`, and
   an unrelated artifact still passes.
5. Export a new package after validation and run pre-deployment assessment.
6. Generate reports and verify selected object totals and row totals reconcile
   to zero remainder before approving go-live.
