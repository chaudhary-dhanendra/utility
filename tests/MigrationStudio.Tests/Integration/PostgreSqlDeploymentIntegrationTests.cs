using System.IO;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Platform;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Validation;
using Npgsql;

namespace MigrationStudio.Tests.Integration;

public sealed class PostgreSqlDeploymentIntegrationTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task DeploysScalarFunctionBeforeDependentCheckConstraint()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"function_check_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-FunctionCheck-{suffix}");
        Directory.CreateDirectory(root);
        try
        {
            var schemaId = Id(InventoryObjectType.Schema, schema, schema);
            var tableId = Id(InventoryObjectType.Table, schema, "sau_details1617");
            var functionId = Id(InventoryObjectType.Function, schema, "fnchksau_dupacc");
            var checkId = Id(InventoryObjectType.CheckConstraint, schema, "chksau_dupacc");
            var run = new ConversionRun(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "integration_source",
                new PostgreSqlVersion(17),
                new ConversionOptions(),
                [],
                [],
                [
                    Artifact(schemaId, "Schema", schema, schema, DeploymentPhase.Schemas,
                        $"CREATE SCHEMA \"{schema}\";", []),
                    Artifact(tableId, "Table", schema, "sau_details1617",
                        DeploymentPhase.Tables,
                        $"CREATE TABLE \"{schema}\".sau_details1617(acc_no varchar(18));",
                        [schemaId]),
                    Artifact(functionId, "Function", schema, "fnchksau_dupacc",
                        DeploymentPhase.PreDataFunctions,
                        $"CREATE FUNCTION \"{schema}\".fnchksau_dupacc(p_acc_no varchar(18)) " +
                        "RETURNS boolean LANGUAGE sql IMMUTABLE " +
                        "AS $$ SELECT p_acc_no IS NOT NULL; $$;",
                        [tableId]),
                    Artifact(checkId, "CheckConstraint", schema, "chksau_dupacc",
                        DeploymentPhase.CheckConstraints,
                        $"ALTER TABLE \"{schema}\".sau_details1617 " +
                        "ADD CONSTRAINT chksau_dupacc " +
                        $"CHECK (\"{schema}\".fnchksau_dupacc(acc_no));",
                        [tableId, functionId])
                ],
                [],
                [],
                "integration");
            var validationResults = await new GeneratedSqlValidator().ValidateLiveAsync(
                run.Artifacts,
                new PostgreSqlValidationOptions(connectionString)
                {
                    PreferDisposableDatabase = false
                },
                CancellationToken.None);
            Assert.All(
                validationResults.Values,
                result => Assert.Equal(LiveSqlValidationOutcome.Passed, result.Outcome));
            run = run with
            {
                Artifacts = run.Artifacts.Select(item =>
                    item with { Validation = validationResults[item.ContentHash] }).ToArray()
            };
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
                package,
                false,
                CancellationToken.None);
            Assert.Contains(manifest.Artifacts, item => item.SourceObjectId == functionId);
            Assert.Contains(manifest.Artifacts, item => item.SourceObjectId == checkId);

            using var journals = new DeploymentJournalStore(new TestPaths(root));
            var packageReader = new MigrationPackageReader();
            var connections = new PostgreSqlDeploymentConnectionService();
            var engine = new PostgreSqlDeploymentEngine(
                new PreDeploymentAssessmentService(packageReader, connections),
                packageReader,
                new PostgreSqlScriptParser(),
                new DatabaseProvisioningService(),
                journals,
                new UnusedDataMigrationEngine(),
                new DeploymentSession());
            var result = await engine.DeployAsync(
                new MigrationStudio.Application.Deployment.DeploymentRequest(
                    package,
                    ToOptions(builder),
                    new DeploymentOptions
                    {
                        Scope = DeploymentScope.CompletePackage,
                        AnalyzeTables = false,
                        RequireLivePostgreSqlValidation = true
                    }),
                null,
                CancellationToken.None);

            Assert.Equal(DeploymentRunStatus.Succeeded, result.Status);
            var entries = result.Objects.ToList();
            Assert.True(
                entries.FindIndex(item => item.SourceObjectId == functionId) <
                entries.FindIndex(item => item.SourceObjectId == checkId));
            Assert.DoesNotContain(result.Objects, item =>
                item.Status == DeploymentObjectStatus.BlockedByDependency);
            Assert.DoesNotContain(result.Failures, item => item.SqlState == "42883");
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
                cleanup);
            await command.ExecuteNonQueryAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task CompletePackage_LoadsSelfReferencingDataOnceBeforeCreatingForeignKey()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"data_order_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-DataOrder-{suffix}");
        Directory.CreateDirectory(root);
        try
        {
            var schemaId = Id(InventoryObjectType.Schema, schema, schema);
            var tableId = Id(InventoryObjectType.Table, schema, "node");
            var primaryKeyId = Id(InventoryObjectType.PrimaryKey, schema, "pk_node");
            var uniqueId = Id(InventoryObjectType.UniqueConstraint, schema, "uq_node_code");
            var foreignKeyId = Id(InventoryObjectType.ForeignKey, schema, "fk_node_parent");
            var run = new ConversionRun(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "integration_source",
                new PostgreSqlVersion(17),
                new ConversionOptions(),
                [],
                [],
                [
                    Artifact(schemaId, "Schema", schema, schema, DeploymentPhase.Schemas,
                        $"CREATE SCHEMA \"{schema}\";", []),
                    Artifact(tableId, "Table", schema, "node", DeploymentPhase.Tables,
                        $"CREATE TABLE \"{schema}\".node(" +
                        "id integer NOT NULL, parent_id integer NULL, code text NOT NULL);",
                        [schemaId]),
                    Artifact(primaryKeyId, "PrimaryKey", schema, "pk_node",
                        DeploymentPhase.PrimaryKeys,
                        $"ALTER TABLE \"{schema}\".node ADD CONSTRAINT pk_node PRIMARY KEY(id);",
                        [tableId]),
                    Artifact(uniqueId, "UniqueConstraint", schema, "uq_node_code",
                        DeploymentPhase.UniqueConstraints,
                        $"ALTER TABLE \"{schema}\".node ADD CONSTRAINT uq_node_code UNIQUE(code);",
                        [tableId]),
                    Artifact(foreignKeyId, "ForeignKey", schema, "fk_node_parent",
                        DeploymentPhase.ForeignKeys,
                        $"ALTER TABLE \"{schema}\".node ADD CONSTRAINT fk_node_parent " +
                        $"FOREIGN KEY(parent_id) REFERENCES \"{schema}\".node(id);",
                        [tableId, primaryKeyId])
                ],
                [],
                [],
                "integration");
            var validationResults = await new GeneratedSqlValidator().ValidateLiveAsync(
                run.Artifacts,
                new PostgreSqlValidationOptions(connectionString)
                {
                    PreferDisposableDatabase = false
                },
                CancellationToken.None);
            run = run with
            {
                Artifacts = run.Artifacts.Select(item =>
                    item with { Validation = validationResults[item.ContentHash] }).ToArray()
            };
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            using var journals = new DeploymentJournalStore(new TestPaths(root));
            var packageReader = new MigrationPackageReader();
            var connections = new PostgreSqlDeploymentConnectionService();
            var dataMigration = new SelfReferencingDataMigrationEngine(schema);
            var engine = new PostgreSqlDeploymentEngine(
                new PreDeploymentAssessmentService(packageReader, connections),
                packageReader,
                new PostgreSqlScriptParser(),
                new DatabaseProvisioningService(),
                journals,
                dataMigration,
                new DeploymentSession());
            var request = new MigrationStudio.Application.Deployment.DeploymentRequest(
                package,
                ToOptions(builder),
                new DeploymentOptions
                {
                    Scope = DeploymentScope.CompletePackage,
                    AnalyzeTables = false,
                    ConstraintStrategy =
                        ConstraintDeploymentStrategy.AddNotValidThenValidate,
                    ValidateConstraints = true,
                    RequireLivePostgreSqlValidation = true
                },
                new DataMigrationRequest(
                    new InventorySnapshot(),
                    run,
                    new SqlServerConnectionOptions(),
                    "Host=pending;Database=pending;Username=pending",
                    new DataMigrationOptions()));

            var result = await engine.DeployAsync(request, null, CancellationToken.None);

            Assert.Equal(DeploymentRunStatus.Succeeded, result.Status);
            Assert.Equal(1, dataMigration.ExecutionCount);
            Assert.True(dataMigration.TableAndKeysExisted);
            Assert.False(dataMigration.ForeignKeyExisted);
            var dataIndex = result.Objects.ToList().FindIndex(item =>
                item.Phase == DeploymentPhase.Data);
            var foreignKeyIndex = result.Objects.ToList().FindIndex(item =>
                item.SourceObjectId == foreignKeyId);
            var validationIndex = result.Objects.ToList().FindIndex(item =>
                item.Phase == DeploymentPhase.Validation &&
                item.TargetObject.Contains("validate:", StringComparison.Ordinal));
            Assert.True(dataIndex >= 0);
            Assert.True(foreignKeyIndex > dataIndex);
            Assert.True(validationIndex > foreignKeyIndex);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
                cleanup);
            await command.ExecuteNonQueryAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ExistingPublicSchema_IsRetainedUnderFailConflictPolicy()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var root = Path.Combine(
            Path.GetTempPath(),
            $"MigrationStudio-PublicSchema-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var schemaId = Id(InventoryObjectType.Schema, "public", "public");
            var artifact = Artifact(
                schemaId,
                "Schema",
                "public",
                "public",
                DeploymentPhase.Schemas,
                "CREATE SCHEMA IF NOT EXISTS public;",
                []) with
            {
                Validation = new SqlValidationResult(true, true, null, null, null)
                {
                    Outcome = LiveSqlValidationOutcome.Passed,
                    Confidence = LiveSqlValidationConfidence.DisposableDatabase
                }
            };
            var run = new ConversionRun(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                "source",
                new PostgreSqlVersion(14),
                new ConversionOptions(),
                [],
                [],
                [artifact],
                [],
                [],
                "integration-test");
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(run, root, CancellationToken.None);
            var reader = new MigrationPackageReader();
            var connections = new PostgreSqlDeploymentConnectionService();
            var assessmentService = new PreDeploymentAssessmentService(reader, connections);
            using var journals = new DeploymentJournalStore(new TestPaths(root));
            var engine = new PostgreSqlDeploymentEngine(
                assessmentService,
                reader,
                new PostgreSqlScriptParser(),
                new DatabaseProvisioningService(),
                journals,
                new UnusedDataMigrationEngine(),
                new DeploymentSession());
            var request = new MigrationStudio.Application.Deployment.DeploymentRequest(
                package,
                ToOptions(builder),
                new DeploymentOptions
                {
                    ConflictPolicy = ExistingObjectConflictPolicy.Fail,
                    AnalyzeTables = false,
                    RequireLivePostgreSqlValidation = true
                });

            var assessment = await assessmentService.AssessAsync(
                request,
                CancellationToken.None);
            var result = await engine.DeployAsync(
                request,
                null,
                CancellationToken.None);

            Assert.DoesNotContain(assessment.Findings, item => item.Code == "TARGET.CONFLICT");
            var conflict = Assert.Single(assessment.Conflicts);
            Assert.Equal("public", conflict.TargetObject);
            Assert.True(conflict.IsEquivalent);
            Assert.Equal(DeploymentRunStatus.Succeeded, result.Status);
            Assert.Equal(
                DeploymentObjectStatus.SkippedEquivalent,
                Assert.Single(result.Objects).Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task DeploysRepresentativePackageAndWritesAccurateJournal()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"deploy_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-Deploy-{suffix}");
        Directory.CreateDirectory(root);
        try
        {
            var conversionRun = CreateRun(schema);
            var validationResults = await new GeneratedSqlValidator().ValidateLiveAsync(
                conversionRun.Artifacts,
                new PostgreSqlValidationOptions(connectionString)
                {
                    PreferDisposableDatabase = false
                },
                CancellationToken.None);
            conversionRun = conversionRun with
            {
                Artifacts = conversionRun.Artifacts.Select(item =>
                    item with { Validation = validationResults[item.ContentHash] }).ToArray()
            };
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(conversionRun, root, CancellationToken.None);
            var paths = new TestPaths(root);
            using var journals = new DeploymentJournalStore(paths);
            var packageReader = new MigrationPackageReader();
            var connections = new PostgreSqlDeploymentConnectionService();
            var assessment = new PreDeploymentAssessmentService(packageReader, connections);
            var engine = new PostgreSqlDeploymentEngine(
                assessment,
                packageReader,
                new PostgreSqlScriptParser(),
                new DatabaseProvisioningService(),
                journals,
                new UnusedDataMigrationEngine(),
                new DeploymentSession());
            var request = new MigrationStudio.Application.Deployment.DeploymentRequest(
                package,
                ToOptions(builder),
                new DeploymentOptions
                {
                    Mode = DeploymentMode.DeployToExistingDatabase,
                    Scope = DeploymentScope.CompletePackage,
                    AnalyzeTables = false,
                    ConflictPolicy = ExistingObjectConflictPolicy.Fail,
                    RequireLivePostgreSqlValidation = true
                });

            var result = await engine.DeployAsync(request, null, CancellationToken.None);

            Assert.Equal(DeploymentRunStatus.Succeeded, result.Status);
            Assert.All(result.Objects, item =>
                Assert.Contains(
                    item.Status,
                    new[] { DeploymentObjectStatus.Succeeded, DeploymentObjectStatus.Skipped }));
            Assert.DoesNotContain(result.Objects, item => item.CommitStatus == CommitStatus.Pending);
            Assert.True(File.Exists(result.JournalPath));
            var journal = await journals.LoadAsync(result.DeploymentId, CancellationToken.None);
            Assert.Equal(DeploymentRunStatus.Succeeded, journal!.Status);
            Assert.Equal(result.Objects.Count, journal.Objects.Count);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"INSERT INTO \"{schema}\".customer(name) VALUES ('Ada'); " +
                $"SELECT name_upper FROM \"{schema}\".customer_view;",
                connection);
            Assert.Equal("ADA", await command.ExecuteScalarAsync());
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
                cleanup);
            await command.ExecuteNonQueryAsync();
            Directory.Delete(root, true);
        }
    }

    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task ObjectTransaction_RollsBackFailedObjectAndRecordsFailure()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var schema = $"rollback_{suffix}";
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-Rollback-{suffix}");
        Directory.CreateDirectory(root);
        try
        {
            var package = await new MigrationPackageWriter(new EmptyConversionReportWriter())
                .WriteAsync(CreateFailingRun(schema), root, CancellationToken.None);
            using var journals = new DeploymentJournalStore(new TestPaths(root));
            var packageReader = new MigrationPackageReader();
            var connections = new PostgreSqlDeploymentConnectionService();
            var engine = new PostgreSqlDeploymentEngine(
                new PreDeploymentAssessmentService(packageReader, connections),
                packageReader,
                new PostgreSqlScriptParser(),
                new DatabaseProvisioningService(),
                journals,
                new UnusedDataMigrationEngine(),
                new DeploymentSession());
            var result = await engine.DeployAsync(
                new MigrationStudio.Application.Deployment.DeploymentRequest(
                    package,
                    ToOptions(builder),
                    new DeploymentOptions
                    {
                        TransactionMode = DeploymentTransactionMode.TransactionPerObject,
                        AnalyzeTables = false
                    }),
                null,
                CancellationToken.None);

            Assert.Equal(DeploymentRunStatus.Failed, result.Status);
            var failed = Assert.Single(result.Objects, item =>
                item.Status == DeploymentObjectStatus.Failed);
            Assert.Equal(CommitStatus.RolledBack, failed.CommitStatus);
            Assert.NotNull(failed.Failure);
            Assert.Equal("42703", failed.Failure.SqlState);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT to_regclass('\"{schema}\".failed_table') IS NULL",
                connection);
            Assert.True(Convert.ToBoolean(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(connectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE",
                cleanup);
            await command.ExecuteNonQueryAsync();
            Directory.Delete(root, true);
        }
    }

    private static ConversionRun CreateRun(string schema)
    {
        var schemaId = Id(InventoryObjectType.Schema, schema, schema);
        var tableId = Id(InventoryObjectType.Table, schema, "customer");
        var keyId = Id(InventoryObjectType.PrimaryKey, schema, "pk_customer");
        var functionId = Id(InventoryObjectType.Function, schema, "upper_name");
        var procedureId = Id(InventoryObjectType.StoredProcedure, schema, "noop");
        var viewId = Id(InventoryObjectType.View, schema, "customer_view");
        var triggerId = Id(InventoryObjectType.Trigger, schema, "customer_touch");
        var securityId = Id(InventoryObjectType.Permission, schema, "grant_customer");
        ConversionArtifact[] artifacts =
        [
            Artifact(schemaId, "Schema", schema, schema, DeploymentPhase.Schemas,
                $"CREATE SCHEMA \"{schema}\";", []),
            Artifact(tableId, "Table", schema, "customer", DeploymentPhase.Tables,
                $"CREATE TABLE \"{schema}\".customer(id integer GENERATED BY DEFAULT AS IDENTITY, name text NOT NULL, touched boolean NOT NULL DEFAULT false);",
                [schemaId]),
            Artifact(keyId, "PrimaryKey", schema, "pk_customer", DeploymentPhase.PrimaryKeys,
                $"ALTER TABLE \"{schema}\".customer ADD CONSTRAINT pk_customer PRIMARY KEY(id);", [tableId]),
            Artifact(functionId, "Function", schema, "upper_name", DeploymentPhase.Functions,
                $"CREATE FUNCTION \"{schema}\".upper_name(value text) RETURNS text LANGUAGE sql IMMUTABLE AS $$ SELECT upper(value); $$;",
                [schemaId]),
            Artifact(procedureId, "Procedure", schema, "noop", DeploymentPhase.Procedures,
                $"CREATE PROCEDURE \"{schema}\".noop() LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;", [schemaId]),
            Artifact(viewId, "View", schema, "customer_view", DeploymentPhase.Views,
                $"CREATE VIEW \"{schema}\".customer_view AS SELECT id, upper(name) AS name_upper FROM \"{schema}\".customer;",
                [tableId]),
            Artifact(triggerId, "Trigger", schema, "customer_touch", DeploymentPhase.Triggers,
                $"CREATE FUNCTION \"{schema}\".touch_customer() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN NEW.touched := true; RETURN NEW; END; $$;" +
                $" CREATE TRIGGER customer_touch BEFORE INSERT ON \"{schema}\".customer FOR EACH ROW EXECUTE FUNCTION \"{schema}\".touch_customer();",
                [tableId]),
            Artifact(securityId, "Permission", schema, "grant_customer", DeploymentPhase.Security,
                $"GRANT SELECT ON \"{schema}\".customer TO CURRENT_USER;", [tableId])
        ];
        return new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "integration_source",
            new PostgreSqlVersion(14),
            new ConversionOptions { TargetVersion = new PostgreSqlVersion(14) },
            [],
            [],
            artifacts,
            [],
            [],
            "integration");
    }

    private static ConversionRun CreateFailingRun(string schema)
    {
        var schemaId = Id(InventoryObjectType.Schema, schema, schema);
        var tableId = Id(InventoryObjectType.Table, schema, "failed_table");
        return new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "integration_source",
            new PostgreSqlVersion(14),
            new ConversionOptions { TargetVersion = new PostgreSqlVersion(14) },
            [],
            [],
            [
                Artifact(schemaId, "Schema", schema, schema, DeploymentPhase.Schemas,
                    $"CREATE SCHEMA \"{schema}\";", []),
                Artifact(tableId, "Table", schema, "failed_table", DeploymentPhase.Tables,
                    $"CREATE TABLE \"{schema}\".failed_table(id integer); " +
                    $"SELECT missing_column FROM \"{schema}\".failed_table;", [schemaId])
            ],
            [],
            [],
            "integration");
    }

    private static ConversionArtifact Artifact(
        InventoryObjectId id,
        string type,
        string schema,
        string name,
        DeploymentPhase phase,
        string sql,
        IReadOnlyList<InventoryObjectId> dependencies) =>
        new(
            id,
            new TargetObjectIdentifier(type, schema, name),
            "integration fixture",
            sql,
            ConversionClassification.Automatic,
            "INTEGRATION",
            1,
            [],
            dependencies,
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            phase,
            phase switch
            {
                DeploymentPhase.Schemas => "02_Schemas.sql",
                DeploymentPhase.Tables => "05_Tables.sql",
                DeploymentPhase.PreDataFunctions => "06_PreDataFunctions.sql",
                DeploymentPhase.PrimaryKeys => "07_PrimaryKeys.sql",
                DeploymentPhase.Functions => "14_Functions.sql",
                DeploymentPhase.Procedures => "15_Procedures.sql",
                DeploymentPhase.Views => "16_Views.sql",
                DeploymentPhase.Triggers => "17_Triggers.sql",
                DeploymentPhase.Security => "18_Security.sql",
                _ => "20_PostDeployment.sql"
            },
            Guid.NewGuid().ToString("N"));

    private static InventoryObjectId Id(InventoryObjectType type, string schema, string name) =>
        InventoryObjectId.Create("integration", type, schema, name, null);

    private static PostgreSqlConnectionOptions ToOptions(NpgsqlConnectionStringBuilder builder) =>
        new()
        {
            Host = builder.Host ?? "localhost",
            Port = builder.Port,
            MaintenanceDatabase = builder.Database ?? "postgres",
            TargetDatabase = builder.Database ?? "postgres",
            Username = builder.Username ?? string.Empty,
            Password = builder.Password,
            SslMode = builder.SslMode.ToString(),
            ConnectionTimeoutSeconds = builder.Timeout,
            CommandTimeoutSeconds = builder.CommandTimeout,
            KeepAliveSeconds = builder.KeepAlive,
            Pooling = builder.Pooling
        };

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

    private sealed class SelfReferencingDataMigrationEngine(string schema)
        : IDataMigrationEngine
    {
        public int ExecutionCount { get; private set; }

        public bool TableAndKeysExisted { get; private set; }

        public bool ForeignKeyExisted { get; private set; }

        public async Task<DataMigrationResult> ExecuteAsync(
            DataMigrationRequest request,
            IProgress<DataMigrationProgress>? progress,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            await using var connection = new NpgsqlConnection(request.TargetConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using (var state = new NpgsqlCommand(
                """
                SELECT
                    to_regclass(@qualified_name) IS NOT NULL,
                    count(*) FILTER (WHERE constraint_type IN ('p', 'u')) = 2,
                    count(*) FILTER (WHERE constraint_type = 'f') > 0
                FROM (
                    SELECT c.contype::text AS constraint_type
                    FROM pg_constraint c
                    JOIN pg_class t ON t.oid = c.conrelid
                    JOIN pg_namespace n ON n.oid = t.relnamespace
                    WHERE n.nspname = @schema AND t.relname = 'node'
                ) constraints;
                """,
                connection))
            {
                state.Parameters.AddWithValue("qualified_name", $"\"{schema}\".node");
                state.Parameters.AddWithValue("schema", schema);
                await using var reader = await state.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                TableAndKeysExisted = reader.GetBoolean(0) && reader.GetBoolean(1);
                ForeignKeyExisted = reader.GetBoolean(2);
            }

            await using (var insert = new NpgsqlCommand(
                $"INSERT INTO \"{schema}\".node(id, parent_id, code) " +
                "VALUES (1, 2, 'child'), (2, NULL, 'parent');",
                connection))
            {
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var now = DateTimeOffset.UtcNow;
            return new DataMigrationResult(
                Guid.NewGuid(),
                MigrationRunState.Completed,
                now,
                now,
                [],
                [],
                [],
                [],
                string.Empty,
                1,
                1,
                1,
                []);
        }

        public Task<DataMigrationResult> ResumeAsync(
            DataMigrationRequest request,
            IProgress<DataMigrationProgress>? progress,
            CancellationToken cancellationToken) =>
            ExecuteAsync(request, progress, cancellationToken);

        public Task RestartTableAsync(
            Guid runId,
            InventoryObjectId tableId,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RestartRunAsync(Guid runId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class UnusedDataMigrationEngine : IDataMigrationEngine
    {
        public Task<DataMigrationResult> ExecuteAsync(
            DataMigrationRequest request,
            IProgress<DataMigrationProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The integration package contains no data phase.");

        public Task<DataMigrationResult> ResumeAsync(
            DataMigrationRequest request,
            IProgress<DataMigrationProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The integration package contains no data phase.");

        public Task RestartTableAsync(
            Guid runId,
            InventoryObjectId tableId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RestartRunAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class TestPaths(string root) : IApplicationPaths
    {
        public string ApplicationDataDirectory { get; } = root;

        public string LogsDirectory { get; } = Path.Combine(root, "Logs");

        public string PluginsDirectory { get; } = Path.Combine(root, "Plugins");

        public string SettingsFilePath { get; } = Path.Combine(root, "settings.json");
    }
}
