# SQL Server to PostgreSQL Migration Studio 1.0.7

## WPF data-migration presentation correction

- Makes the computed `IsResumable` and `IsSensitive` table-plan indicators
  explicit OneWay bindings.
- Keeps the indicators non-editable without adding setters to their ViewModel.
- Audits other informational `DataGridCheckBoxColumn` bindings and marks them
  OneWay and read-only.
- Retains TwoWay behavior only for intentionally editable selection and option
  properties.
- Enables WPF binding-error tracing in Debug builds.
- Adds an STA UI smoke test that renders the actual Data Migration table-plan
  grid with a populated `DataMigrationTableRowViewModel` and requires zero
  `PresentationTraceSources` errors.

The identifier mapping pipeline remains unchanged from version 1.0.6.
