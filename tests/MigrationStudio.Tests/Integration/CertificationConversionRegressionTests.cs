using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Deployment;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;
using MigrationStudio.Validation;
using Npgsql;
using Xunit.Abstractions;

namespace MigrationStudio.Tests.Integration;

public sealed class CertificationConversionRegressionTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private static readonly string[] TemporalTables =
    [
        "audit.employeehistory",
        "audit.salesorderstatushistory",
        "cert.country",
        "cert.customer",
        "cert.jsonpayload",
        "cert.inventorybalance",
        "cert.xmlpayload"
    ];

    [CertificationInventoryFact]
    [Trait("Category", "Integration")]
    public async Task PersistedCertificationInventory_RegeneratesAllRootFailures()
    {
        var inventory = await LoadInventoryAsync();
        var run = await CreateEngine().ConvertAsync(
            inventory,
            CreateOptions(),
            null,
            CancellationToken.None);

        Assert.Equal(221, run.Artifacts.Count);
        foreach (var targetName in TemporalTables)
        {
            var artifact = Assert.Single(run.Artifacts, item =>
                item.TargetObjectId.QualifiedName.Equals(
                    targetName,
                    StringComparison.OrdinalIgnoreCase) &&
                item.TargetObjectId.ObjectType.Equals(
                    nameof(InventoryObjectType.Table),
                    StringComparison.OrdinalIgnoreCase));
            Assert.True(artifact.Validation.IsStructurallyValid);
            Assert.Contains(
                "DEFAULT timezone('UTC', CURRENT_TIMESTAMP)",
                artifact.PostgreSqlDefinition,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "CURRENT_TIMESTAMP AT TIME ZONE",
                artifact.PostgreSqlDefinition,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "sysutcdatetime",
                artifact.PostgreSqlDefinition,
                StringComparison.OrdinalIgnoreCase);
            output.WriteLine($"{targetName}:{Environment.NewLine}{artifact.PostgreSqlDefinition}");
        }

        var documentStore = Assert.Single(run.Artifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.documentstore",
                StringComparison.OrdinalIgnoreCase) &&
            item.TargetObjectId.ObjectType.Equals(
                nameof(InventoryObjectType.Table),
                StringComparison.OrdinalIgnoreCase));
        var documentStorePrimaryKey = Assert.Single(run.Artifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.pk_documentstore",
                StringComparison.OrdinalIgnoreCase) &&
            item.DeploymentPhase == DeploymentPhase.PrimaryKeys);
        Assert.False(documentStore.RequiresManualReview);
        Assert.NotEqual(ConversionClassification.ManualConversion, documentStore.Classification);
        Assert.True(documentStore.Validation.IsStructurallyValid);
        Assert.Contains("gen_random_uuid()", documentStore.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.Contains(
            "DEFAULT timezone('UTC', CURRENT_TIMESTAMP)",
            documentStore.PostgreSqlDefinition,
            StringComparison.Ordinal);
        Assert.Contains(documentStore.SourceObjectId, documentStorePrimaryKey.Dependencies);
        var orderedArtifacts = run.Artifacts.ToArray();
        Assert.True(
            Array.IndexOf(orderedArtifacts, documentStore) <
            Array.IndexOf(orderedArtifacts, documentStorePrimaryKey));

        var employeeAge = Assert.Single(run.Artifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.fn_employeeage",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(inventory.Modules, item =>
            item.ObjectId == employeeAge.SourceObjectId);
        Assert.True(employeeAge.Validation.IsStructurallyValid);
        Assert.Contains("LANGUAGE plpgsql", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.Contains("IF ", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.Contains("END IF;", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.Contains("CASE", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.Contains("END;", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.EndsWith("$migrationstudio$;", employeeAge.PostgreSqlDefinition, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEDIFF", employeeAge.PostgreSqlDefinition, StringComparison.OrdinalIgnoreCase);

        var state = Assert.Single(run.Artifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.state",
                StringComparison.OrdinalIgnoreCase));
        var jsonCheck = Assert.Single(run.Artifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.ck_jsonpayload_valid",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains("THEN TRUE", state.PostgreSqlDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELSE FALSE", state.PostgreSqlDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            " AS \"bit\"",
            state.PostgreSqlDefinition,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" IS JSON", jsonCheck.PostgreSqlDefinition, StringComparison.OrdinalIgnoreCase);
        foreach (var procedureName in new[]
                 {
                     "cert.usp_createcustomer",
                     "cert.usp_createsalesorder"
                 })
        {
            var procedure = Assert.Single(run.Artifacts, item =>
                item.TargetObjectId.QualifiedName.Equals(
                    procedureName,
                    StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                "XACT_ABORT",
                procedure.PostgreSqlDefinition,
                StringComparison.OrdinalIgnoreCase);
        }

        output.WriteLine(
            $"DocumentStore SourceObjectId={documentStore.SourceObjectId}; " +
            $"Classification={documentStore.Classification}; Phase={documentStore.DeploymentPhase}; " +
            $"Dependencies=[{string.Join(", ", documentStore.Dependencies)}]");
        output.WriteLine(documentStore.PostgreSqlDefinition);
        output.WriteLine(
            $"PrimaryKey SourceObjectId={documentStorePrimaryKey.SourceObjectId}; " +
            $"Dependencies=[{string.Join(", ", documentStorePrimaryKey.Dependencies)}]");
        output.WriteLine($"EmployeeAge source:{Environment.NewLine}{employeeAge.SourceDefinition}");
        output.WriteLine($"EmployeeAge target:{Environment.NewLine}{employeeAge.PostgreSqlDefinition}");
        output.WriteLine($"State target:{Environment.NewLine}{state.PostgreSqlDefinition}");
        output.WriteLine($"Json check target:{Environment.NewLine}{jsonCheck.PostgreSqlDefinition}");
        output.WriteLine(
            $"InventoryObjects={inventory.Objects.Count}; IncludedObjects=" +
            $"{inventory.Objects.Count(item => item.IsIncluded)}; Artifacts={run.Artifacts.Count}");
    }

    [CertificationPostgreSqlFact]
    [Trait("Category", "Integration")]
    public async Task PersistedCertificationInventory_LiveValidatesAndPublishesCompletePackage()
    {
        var inventory = await LoadInventoryAsync();
        var run = await CreateEngine().ConvertAsync(
            inventory,
            CreateOptions(),
            null,
            CancellationToken.None);
        var beforeCount = run.Artifacts.Count;
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_POSTGRES_INTEGRATION")!;
        var results = await new GeneratedSqlValidator().ValidateLiveAsync(
            run.Artifacts,
            new PostgreSqlValidationOptions(connectionString),
            CancellationToken.None);
        var validatedArtifacts = run.Artifacts
            .Select(item => item with { Validation = results[item.ContentHash] })
            .ToArray();
        Assert.Equal(beforeCount, validatedArtifacts.Length);
        Assert.Equal(
            run.Artifacts.Select(item => item.SourceObjectId).OrderBy(item => item.Value),
            validatedArtifacts.Select(item => item.SourceObjectId).OrderBy(item => item.Value));

        var failures = validatedArtifacts
            .Where(item =>
                IsExecutable(item) &&
                item.Validation.Outcome != LiveSqlValidationOutcome.Passed)
            .ToArray();
        foreach (var failure in failures)
        {
            output.WriteLine(
                $"{failure.TargetObjectId.QualifiedName}: " +
                $"{failure.Validation.Outcome} {failure.Validation.SqlState} " +
                $"{failure.Validation.Message}; dependencies=" +
                $"{string.Join(", ", ResolveDependencyNames(failure, run.Artifacts))}");
        }
        Assert.Empty(failures);

        foreach (var targetName in TemporalTables.Concat(
                     ["cert.documentstore", "cert.pk_documentstore", "cert.fn_employeeage",
                      "cert.country", "cert.state", "cert.ck_state_dates"]))
        {
            var artifacts = validatedArtifacts.Where(item =>
                item.TargetObjectId.QualifiedName.Equals(
                    targetName,
                    StringComparison.OrdinalIgnoreCase) &&
                IsExecutable(item)).ToArray();
            Assert.NotEmpty(artifacts);
            Assert.All(
                artifacts,
                item => Assert.Equal(
                    LiveSqlValidationOutcome.Passed,
                    item.Validation.Outcome));
        }

        var employeeAge = Assert.Single(validatedArtifacts, item =>
            item.TargetObjectId.QualifiedName.Equals(
                "cert.fn_employeeage",
                StringComparison.OrdinalIgnoreCase));
        await ExecuteAndCallEmployeeAgeAsync(
            connectionString,
            employeeAge.PostgreSqlDefinition);

        var validatedRun = run with { Artifacts = validatedArtifacts };
        var root = Path.Combine(
            Path.GetTempPath(),
            $"MigrationStudio-Certification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var packagePath = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(validatedRun, root, CancellationToken.None);
            var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
                packagePath,
                false,
                CancellationToken.None);
            Assert.Equal(beforeCount, manifest.Artifacts.Count);
            Assert.All(
                manifest.Artifacts.Where(item => item.IsExecutable),
                item => Assert.Equal(
                    LiveSqlValidationOutcome.Passed,
                    item.LiveValidation.Outcome));
            var connectionBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            var assessment = await new PreDeploymentAssessmentService(
                    new MigrationPackageReader(),
                    new PostgreSqlDeploymentConnectionService())
                .AssessAsync(
                    new MigrationStudio.Application.Deployment.DeploymentRequest(
                        packagePath,
                        ToDeploymentConnectionOptions(connectionBuilder),
                        new DeploymentOptions
                        {
                            AnalyzeTables = false,
                            ConflictPolicy = ExistingObjectConflictPolicy.Fail,
                            RequireLivePostgreSqlValidation = true
                        }),
                    CancellationToken.None);
            Assert.True(
                assessment.CanDeploy,
                string.Join(
                    Environment.NewLine,
                    assessment.Findings.Select(item =>
                        $"{item.Severity} {item.Code}: {item.Message}")));
            output.WriteLine(
                $"ArtifactsBeforeValidation={beforeCount}; " +
                $"ArtifactsAfterValidation={validatedArtifacts.Length}; " +
                $"ManifestArtifacts={manifest.Artifacts.Count}; " +
                $"AssessmentCanDeploy={assessment.CanDeploy}; Package={packagePath}");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<InventorySnapshot> LoadInventoryAsync()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_CERTIFICATION_INVENTORY_HISTORY")!;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Assert.IsType<InventorySnapshot>(
            await JsonSerializer.DeserializeAsync<InventorySnapshot>(
                stream,
                JsonOptions));
    }

    private static ConversionOptions CreateOptions() =>
        new()
        {
            TargetVersion = new PostgreSqlVersion(17),
            IdentifierCaseMode = IdentifierCaseMode.QuoteOnlyWhenRequired,
            SchemaMappingMode = SchemaMappingMode.Preserve,
            SchemaMappings =
            [
                new SchemaMappingRule("audit", "audit"),
                new SchemaMappingRule("cert", "cert"),
                new SchemaMappingRule("dbo", "public")
            ],
            EnablePgCrypto = true,
            UseRandomUuidForNewSequentialId = true
        };

    private static ConversionEngine CreateEngine()
    {
        IObjectConverter<InventoryObject, string>[] converters =
        [
            new SchemaConverter(),
            new TableConverter(),
            new ConstraintConverter(),
            new IndexConverter(),
            new SequenceConverter(),
            new UserDefinedTypeConverter(),
            new ProgrammableObjectConverter(),
            new SecurityConverter(),
            new SynonymConverter(),
            new FallbackObjectConverter()
        ];
        return new ConversionEngine(
            converters,
            new PostgreSqlIdentifierMappingService(),
            new PostgreSqlTypeMappingRegistry(),
            new StructuredSqlExpressionTranslator(),
            new GeneratedSqlValidator(),
            NullLogger<ConversionEngine>.Instance);
    }

    private static bool IsExecutable(ConversionArtifact artifact) =>
        artifact.Classification is ConversionClassification.Automatic or
            ConversionClassification.AutomaticWithWarning &&
        !artifact.RequiresManualReview &&
        !artifact.PostgreSqlDefinition.TrimStart().StartsWith("--", StringComparison.Ordinal);

    private static IEnumerable<string> ResolveDependencyNames(
        ConversionArtifact artifact,
        IReadOnlyList<ConversionArtifact> artifacts) =>
        artifact.Dependencies.Select(dependency =>
            artifacts.FirstOrDefault(item => item.SourceObjectId == dependency)
                ?.TargetObjectId.QualifiedName ?? dependency.ToString());

    private static async Task ExecuteAndCallEmployeeAgeAsync(
        string connectionString,
        string generatedSql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var command = new NpgsqlCommand(
                             "CREATE SCHEMA IF NOT EXISTS cert;" +
                             Environment.NewLine +
                             generatedSql,
                             connection,
                             transaction))
            {
                await command.ExecuteNonQueryAsync();
            }
            await using var call = new NpgsqlCommand(
                "SELECT cert.fn_employeeage(DATE '2000-07-29', DATE '2026-07-28'), " +
                "cert.fn_employeeage(NULL, DATE '2026-07-28');",
                connection,
                transaction);
            await using var reader = await call.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(25, reader.GetInt32(0));
            Assert.True(reader.IsDBNull(1));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static PostgreSqlConnectionOptions ToDeploymentConnectionOptions(
        NpgsqlConnectionStringBuilder builder) =>
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

    private sealed class EmptyReportWriter : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string directory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(directory);
            return Task.CompletedTask;
        }
    }
}

public sealed class CertificationInventoryFactAttribute : FactAttribute
{
    public CertificationInventoryFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_CERTIFICATION_INVENTORY_HISTORY");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip =
                "Set MIGRATIONSTUDIO_CERTIFICATION_INVENTORY_HISTORY to a certification discovery run-history payload.";
        }
    }
}

public sealed class CertificationPostgreSqlFactAttribute : FactAttribute
{
    public CertificationPostgreSqlFactAttribute()
    {
        var inventoryPath = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_CERTIFICATION_INVENTORY_HISTORY");
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        if (string.IsNullOrWhiteSpace(inventoryPath) ||
            !File.Exists(inventoryPath) ||
            string.IsNullOrWhiteSpace(connectionString))
        {
            Skip =
                "Set MIGRATIONSTUDIO_CERTIFICATION_INVENTORY_HISTORY and " +
                "MIGRATIONSTUDIO_POSTGRES_INTEGRATION to run certification live validation.";
        }
    }
}
