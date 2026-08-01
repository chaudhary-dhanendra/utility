# Developer guide

The solution targets .NET 8 with nullable reference types, recommended analyzers, deterministic
builds, central package versions, package lock files, and warnings treated as errors.

Build and test:

```powershell
dotnet restore MigrationStudio.sln
dotnet build MigrationStudio.sln -c Release --no-restore
dotnet test MigrationStudio.sln -c Release --no-build
```

Database integration tests are opt-in through the environment variables documented in `README.md`
and must target disposable databases.

## Scale qualification

Generate a deterministic SQL Server fixture script without allocating the target data volume in
client memory:

```powershell
dotnet run --project tools/ScaleFixtureGenerator -c Release -- `
  --preset Catalog6000 `
  --output artifacts/scale-fixtures/Catalog6000
```

Other presets are `Catalog6000WithDependencies`, `Data10GBApproximate`, `WideTableStress`,
`ReportStress`, and `CancellationStress`. The manifest records expected counts, and the cleanup
script drops only the configured fixture database.

Generate deterministic JSON, HTML, and Markdown scale evidence:

```powershell
dotnet run --project tests/MigrationStudio.ScaleTests -c Release -- `
  --output artifacts/benchmarks
```

Run isolated BenchmarkDotNet measurements:

```powershell
dotnet run --project tests/MigrationStudio.Benchmarks -c Release -- `
  --filter "*" `
  --artifacts artifacts/benchmarks/BenchmarkDotNet.Artifacts
```

Live database scale tests remain opt-in and must use disposable endpoints. A synthetic transform
benchmark must never be presented as SQL Server-to-PostgreSQL migration throughput.

Release:

```powershell
.\build\release.ps1 -Version 1.0.0
```

The script refuses to overwrite an existing version directory. It publishes self-contained and
framework-dependent x64 builds, builds the WiX MSI, creates the portable ZIP, copies sanitized
samples/notices/release notes, optionally signs with a supplied certificate, and writes SHA-256
checksums.

Architecture dependencies are enforced by tests: Domain is independent, Application depends on
Domain, infrastructure engines implement Application contracts, and Desktop is the composition
root. Add business invariants to Domain, orchestration contracts to Application, external I/O to
Infrastructure/Deployment/Validation/Reporting, and presentation concerns to Desktop.

Never add credentials to settings or test fixtures. Use generated SQL parameters for values,
validate externally edited paths against a trusted root, bound every concurrent pipeline, make
cancellation explicit, and add deterministic tests for every security boundary.
