using System.IO;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Platform;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Tests.Deployment;

public sealed class DeploymentPackageAndRecoveryTests
{
    [Fact]
    public async Task PackageManifest_VerifiesEveryFileAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(CreateRun(), root, CancellationToken.None);
            var reader = new MigrationPackageReader();

            var manifest = await reader.ReadAndVerifyAsync(package, false, CancellationToken.None);
            Assert.Equal(MigrationPackageManifest.CurrentFormatVersion, manifest.FormatVersion);
            Assert.NotEmpty(manifest.Files);
            Assert.Single(manifest.Artifacts);
            Assert.True(manifest.Artifacts[0].LiveValidation.WasLiveValidated);
            Assert.True(manifest.Artifacts[0].LiveValidation.IsStructurallyValid);
            Assert.Equal(
                LiveSqlValidationOutcome.Passed,
                manifest.Artifacts[0].LiveValidation.Outcome);
            Assert.Equal(64, reader.ComputePackageFingerprint(manifest).Length);

            await File.AppendAllTextAsync(Path.Combine(package, "05_Tables.sql"), "-- tampered");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                reader.ReadAndVerifyAsync(package, false, CancellationToken.None));
            var diagnostic = await reader.ReadAndVerifyAsync(package, true, CancellationToken.None);
            Assert.Equal(manifest.PackageId, diagnostic.PackageId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PackageManifest_RetainsManualArtifactsAsNonExecutableTraceabilityEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = CreateRun();
            var automatic = seed.Artifacts[0];
            var manual = automatic with
            {
                SourceObjectId = new InventoryObjectId(Guid.NewGuid()),
                TargetObjectId = new TargetObjectIdentifier(
                    "StoredProcedure",
                    "public",
                    "manual_procedure"),
                PostgreSqlDefinition =
                    "DO $$ BEGIN RAISE EXCEPTION 'Manual conversion required'; END $$;",
                Classification = ConversionClassification.ManualConversion,
                RequiresManualReview = true,
                Validation = new SqlValidationResult(true, false, null, null, null)
                {
                    Outcome = LiveSqlValidationOutcome.Manual
                },
                DeploymentPhase = DeploymentPhase.Procedures,
                ScriptFileName = "15_Procedures.sql",
                ContentHash = "manual-artifact"
            };
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(
                    seed with { Artifacts = [automatic, manual] },
                    root,
                    CancellationToken.None);
            var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
                package,
                false,
                CancellationToken.None);

            Assert.Equal(2, manifest.Artifacts.Count);
            Assert.True(Assert.Single(
                manifest.Artifacts,
                item => item.SourceObjectId == automatic.SourceObjectId).IsExecutable);
            Assert.False(Assert.Single(
                manifest.Artifacts,
                item => item.SourceObjectId == manual.SourceObjectId).IsExecutable);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void PhaseOrdering_PlacesDependenciesBeforeDependents()
    {
        var table = Artifact(DeploymentPhase.Tables, []);
        var view = Artifact(DeploymentPhase.Views, [table.SourceObjectId]);

        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts([view, table]);

        Assert.Equal(table.SourceObjectId, ordered[0].SourceObjectId);
        Assert.Equal(view.SourceObjectId, ordered[1].SourceObjectId);
    }

    [Fact]
    public void PhaseOrdering_HonorsDependencyThatIsAssignedToALaterNominalPhase()
    {
        var function = Artifact(DeploymentPhase.Functions, []);
        var table = Artifact(DeploymentPhase.Tables, [function.SourceObjectId]);

        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts([table, function]);

        Assert.Equal(function.SourceObjectId, ordered[0].SourceObjectId);
        Assert.Equal(table.SourceObjectId, ordered[1].SourceObjectId);
    }

    [Fact]
    public void PhaseOrdering_UsesRequiredPostgreSqlGenerationSequence()
    {
        var artifacts = new[]
        {
            Artifact(DeploymentPhase.Views, []),
            Artifact(DeploymentPhase.Sequences, []),
            Artifact(DeploymentPhase.CheckConstraints, []),
            Artifact(DeploymentPhase.Tables, []),
            Artifact(DeploymentPhase.Indexes, []),
            Artifact(DeploymentPhase.ForeignKeys, []),
            Artifact(DeploymentPhase.Procedures, []),
            Artifact(DeploymentPhase.UniqueConstraints, []),
            Artifact(DeploymentPhase.Functions, []),
            Artifact(DeploymentPhase.PrimaryKeys, []),
            Artifact(DeploymentPhase.Schemas, [])
        };

        var ordered = PostgreSqlDeploymentEngine.OrderArtifacts(artifacts);

        Assert.Equal(
            new[]
            {
                DeploymentPhase.Schemas,
                DeploymentPhase.Sequences,
                DeploymentPhase.Tables,
                DeploymentPhase.PrimaryKeys,
                DeploymentPhase.UniqueConstraints,
                DeploymentPhase.CheckConstraints,
                DeploymentPhase.ForeignKeys,
                DeploymentPhase.Indexes,
                DeploymentPhase.Functions,
                DeploymentPhase.Procedures,
                DeploymentPhase.Views
            },
            ordered.Select(item => item.Phase));
    }

    [Fact]
    public async Task PackageExecutionPlan_PlacesFunctionBeforeDependentCheckConstraint()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = CreateRun();
            var seedArtifact = Assert.Single(seed.Artifacts);
            var functionId = new InventoryObjectId(Guid.NewGuid());
            var checkId = new InventoryObjectId(Guid.NewGuid());
            var function = seedArtifact with
            {
                SourceObjectId = functionId,
                TargetObjectId = new TargetObjectIdentifier("Function", "nrega_sk", "fnchksau_dupacc"),
                PostgreSqlDefinition = "CREATE FUNCTION nrega_sk.fnchksau_dupacc(text) RETURNS boolean LANGUAGE sql AS $$ SELECT false; $$;",
                Dependencies = [],
                DeploymentPhase = DeploymentPhase.PreDataFunctions,
                ScriptFileName = "06_PreDataFunctions.sql",
                ContentHash = "function-hash"
            };
            var check = seedArtifact with
            {
                SourceObjectId = checkId,
                TargetObjectId = new TargetObjectIdentifier("CheckConstraint", "nrega_sk", "chksau_dupacc"),
                PostgreSqlDefinition = "ALTER TABLE nrega_sk.t ADD CONSTRAINT chksau_dupacc CHECK (nrega_sk.fnchksau_dupacc(a) = false);",
                Dependencies = [functionId],
                DeploymentPhase = DeploymentPhase.CheckConstraints,
                ScriptFileName = "09_CheckConstraints.sql",
                ContentHash = "check-hash"
            };
            var run = seed with { Artifacts = [check, function] };

            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var executionPlan = await File.ReadAllTextAsync(
                Path.Combine(package, "00_ExecutionPlan.sql"));
            var manifest = await new MigrationPackageReader()
                .ReadAndVerifyAsync(package, false, CancellationToken.None);
            var functionScript = await File.ReadAllTextAsync(
                Path.Combine(package, "06_PreDataFunctions.sql"));
            var checkScript = await File.ReadAllTextAsync(
                Path.Combine(package, "09_CheckConstraints.sql"));
            var (preData, postData) =
                PostgreSqlDeploymentEngine.SplitArtifactsAroundData(manifest.Artifacts);

            Assert.True(
                executionPlan.IndexOf("CREATE FUNCTION", StringComparison.Ordinal) <
                executionPlan.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal));
            Assert.Contains("CREATE FUNCTION", functionScript, StringComparison.Ordinal);
            Assert.Contains("ADD CONSTRAINT", checkScript, StringComparison.Ordinal);
            Assert.Equal(functionId, manifest.Artifacts[0].SourceObjectId);
            Assert.Equal(checkId, manifest.Artifacts[1].SourceObjectId);
            Assert.Contains(preData, item => item.SourceObjectId == functionId);
            Assert.Contains(preData, item => item.SourceObjectId == checkId);
            Assert.DoesNotContain(postData, item =>
                item.SourceObjectId == functionId || item.SourceObjectId == checkId);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PackagePublication_RefusesDependencyCycle()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var seed = CreateRun();
            var firstId = new InventoryObjectId(Guid.NewGuid());
            var secondId = new InventoryObjectId(Guid.NewGuid());
            var first = seed.Artifacts[0] with
            {
                SourceObjectId = firstId,
                TargetObjectId = new TargetObjectIdentifier("View", "public", "first"),
                Dependencies = [secondId],
                ContentHash = "first"
            };
            var second = seed.Artifacts[0] with
            {
                SourceObjectId = secondId,
                TargetObjectId = new TargetObjectIdentifier("View", "public", "second"),
                Dependencies = [firstId],
                ContentHash = "second"
            };

            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                new MigrationPackageWriter(new EmptyConversionReportWriter())
                    .WriteAsync(seed with { Artifacts = [first, second] }, root, CancellationToken.None));

            Assert.Contains("cycle", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateDirectories(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DestructiveDatabasePolicy_RequiresConfirmation()
    {
        var options = new DeploymentOptions
        {
            DatabaseCreation = new DatabaseCreationOptions
            {
                ExistsPolicy = DatabaseExistsPolicy.DropAndRecreate
            }
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
        var confirmed = options with
        {
            DatabaseCreation = options.DatabaseCreation with { DestructiveActionConfirmed = true }
        };
        Assert.Same(confirmed, confirmed.Validate());
    }

    [Fact]
    public void DatabaseCreationSql_QuotesIdentifiersAndLiterals()
    {
        var sql = DatabaseProvisioningService.BuildCreateDatabaseSql(
            "target-db",
            new DatabaseCreationOptions
            {
                Encoding = "UTF8",
                Owner = "migration-owner",
                Locale = "en_US.UTF-8",
                ConnectionLimit = 20
            });

        Assert.Contains("\"target-db\"", sql, StringComparison.Ordinal);
        Assert.Contains("OWNER \"migration-owner\"", sql, StringComparison.Ordinal);
        Assert.Contains("LOCALE 'en_US.UTF-8'", sql, StringComparison.Ordinal);
        Assert.Contains("CONNECTION LIMIT 20", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedConnectionString_DoesNotContainPassword()
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "postgres",
            Username = "migration",
            Password = "NeverLogThisPassword"
        };

        var redacted = PostgreSqlDeploymentConnectionService.Redact(builder);

        Assert.DoesNotContain("NeverLogThisPassword", redacted, StringComparison.Ordinal);
        Assert.Contains("***", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RetryClassifier_DistinguishesTransientFromPermanentStates()
    {
        var transient = new PostgresException("serialization", "ERROR", "ERROR", "40001");

        Assert.True(PostgreSqlDeploymentErrorClassifier.IsTransient(transient));
        Assert.True(PostgreSqlDeploymentErrorClassifier.IsPermanent("42703"));
        Assert.False(PostgreSqlDeploymentErrorClassifier.IsPermanent("40001"));
    }

    [Fact]
    public async Task JournalPersistence_RoundTripsCommittedObject()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new DeploymentJournalStore(new TestPaths(root));
            var journal = Journal();
            var path = await store.SaveAsync(journal, CancellationToken.None);
            var loaded = await store.LoadAsync(journal.DeploymentId, CancellationToken.None);

            Assert.True(File.Exists(path));
            Assert.NotNull(loaded);
            Assert.Equal(journal.DeploymentId, loaded.DeploymentId);
            Assert.Equal(CommitStatus.Committed, loaded.Objects.Single().CommitStatus);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ResumeValidation_RejectsChangedPackageOrTarget()
    {
        var journal = Journal();
        var manifest = new MigrationPackageManifest
        {
            PackageId = journal.PackageId,
            MigrationRunId = journal.MigrationRunId,
            SourceDatabase = "source",
            TargetPostgreSqlVersion = 18,
            ConversionConfigurationHash = "config"
        };
        var request = new MigrationStudio.Application.Deployment.DeploymentRequest(
            journal.PackageDirectory,
            new PostgreSqlConnectionOptions
            {
                Host = "other-host",
                TargetDatabase = journal.TargetDatabase,
                Username = "user"
            },
            new DeploymentOptions());

        Assert.Throws<InvalidOperationException>(() =>
            PostgreSqlDeploymentEngine.ValidateResume(
                journal,
                manifest,
                journal.PackageFingerprint,
                request,
                journal.OptionsHash));
    }

    private static ConversionRun CreateRun()
    {
        var id = new InventoryObjectId(Guid.NewGuid());
        var sql = "CREATE TABLE public.customer(id integer PRIMARY KEY);";
        var artifact = new ConversionArtifact(
            id,
            new TargetObjectIdentifier("Table", "public", "customer"),
            "CREATE TABLE dbo.Customer(Id int);",
            sql,
            ConversionClassification.Automatic,
            "TEST",
            1,
            [],
            [],
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, true, null, null, null)
            {
                Outcome = LiveSqlValidationOutcome.Passed,
                Confidence = LiveSqlValidationConfidence.DisposableDatabase
            },
            DeploymentPhase.Tables,
            "05_Tables.sql",
            "artifact-hash");
        return new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "source",
            new PostgreSqlVersion(18),
            new ConversionOptions(),
            [],
            [],
            [artifact],
            [],
            [],
            "test");
    }

    private static PackageArtifactManifest Artifact(
        DeploymentPhase phase,
        IReadOnlyList<InventoryObjectId> dependencies)
    {
        var id = new InventoryObjectId(Guid.NewGuid());
        return new PackageArtifactManifest(
            id,
            phase.ToString(),
            "public",
            id.ToString(),
            phase,
            $"{(int)phase:00}.sql",
            "SELECT 1;",
            "hash",
            ConversionClassification.Automatic,
            dependencies,
            [],
            false,
            [],
            -1);
    }

    private static DeploymentJournal Journal()
    {
        var objectId = new InventoryObjectId(Guid.NewGuid());
        return new DeploymentJournal(
            DeploymentJournal.CurrentFormatVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            null,
            DeploymentRunStatus.Running,
            "test",
            "machine",
            "user",
            "C:\\package",
            "fingerprint",
            "localhost:5432",
            "target",
            "options",
            [],
            [],
            [
                new DeploymentObjectJournal(
                    objectId,
                    "public.t",
                    DeploymentPhase.Tables,
                    "05_Tables.sql",
                    "hash",
                    DeploymentObjectStatus.Succeeded,
                    CommitStatus.Committed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    [],
                    [],
                    null,
                    false,
                    null)
            ],
            null,
            []);
    }

    private sealed class EmptyConversionReportWriter : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string reportsDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(reportsDirectory);
            return Task.CompletedTask;
        }
    }

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ApplicationDataDirectory { get; } = root;

        public string LogsDirectory { get; } = Path.Combine(root, "Logs");

        public string PluginsDirectory { get; } = Path.Combine(root, "Plugins");

        public string SettingsFilePath { get; } = Path.Combine(root, "settings.json");
    }
}
