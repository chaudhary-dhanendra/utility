using System.IO;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Deployment;
using MigrationStudio.Reporting;
using MigrationStudio.Validation;
using Xunit.Abstractions;

namespace MigrationStudio.Tests.Integration;

public sealed class ProductionIdentifierMapRuntimeTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [ProductionInventoryFact]
    [Trait("Category", "Integration")]
    public async Task PersistedVbgramgInventory_UsesSameColumnKeyAndPreviewPlanSucceeds()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_INVENTORY_HISTORY")!;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var inventory = await JsonSerializer.DeserializeAsync<InventorySnapshot>(
            stream,
            JsonOptions);
        Assert.NotNull(inventory);
        inventory = ApplyProductionScope(inventory);

        var table = Assert.Single(inventory.Objects, item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase) &&
            inventory.Tables.Any(facet => facet.ObjectId == item.Id));
        var column = Assert.Single(inventory.Columns, item =>
            item.ParentObjectId == table.Id &&
            item.Name.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase));
        var expectedKey = new ColumnIdentifierKey(table.Id, column.ColumnId);
        var options = new ConversionOptions();
        var service = new PostgreSqlIdentifierMappingService();
        var mapper = service.CreateMapper(inventory, options);
        var mappings = mapper.Mappings.ToArray();
        var produced = Assert.Single(
            mappings,
            item => item.SourceKey.ColumnKey == expectedKey);
        var trigger = Assert.Single(inventory.Objects, item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals(
                "TRG_DigiPay_TrainerDetailsHistory_Del",
                StringComparison.OrdinalIgnoreCase));
        var parentTable = Assert.Single(inventory.Objects, item =>
            item.Id == trigger.ParentObjectId);
        var triggerMapping = Assert.Single(mappings, item =>
            item.SourceKey.ObjectId == trigger.Id);
        var triggerKey = Assert.IsType<TriggerIdentifierKey>(
            triggerMapping.SourceKey.TriggerKey);
        var includedTableIds = inventory.Objects
            .Where(item => item.IsIncluded && inventory.Tables.Any(facet => facet.ObjectId == item.Id))
            .Select(item => item.Id)
            .ToHashSet();
        var includedColumnCount = inventory.Columns.Count(item =>
            includedTableIds.Contains(item.ParentObjectId));
        var mappedColumnCount = mappings
            .Where(item =>
                item.IncludedInScope &&
                item.SourceKey.ColumnKey is { } key &&
                includedTableIds.Contains(key.TableObjectId))
            .Select(item => item.SourceKey.ColumnKey!.Value)
            .Distinct()
            .Count();
        var conversion = new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            inventory.Database.DatabaseName,
            options.TargetVersion,
            options,
            mappings,
            [],
            [],
            [],
            [],
            "production-runtime-test")
        {
            MappingSet = new IdentifierMappingSetMetadata(
                mapper.MappingSetId,
                mapper.SchemaVersion,
                DateTimeOffset.UtcNow,
                mapper.LoadedFromCache,
                mappings.Length,
                mappings.Length,
                includedColumnCount,
                mappedColumnCount)
        };
        var request = new DataMigrationRequest(
            inventory,
            conversion,
            new SqlServerConnectionOptions
            {
                Server = "runtime-test",
                Database = inventory.Database.DatabaseName
            },
            "Host=runtime-test;Database=runtime_test;Username=runtime_test;Password=not-used",
            new DataMigrationOptions
            {
                ExecutionMode = DataMigrationExecutionMode.Preview
            },
            SelectedTables: new HashSet<InventoryObjectId> { table.Id });

        var plan = new DataMigrationPlanner(new SensitiveColumnClassifier())
            .CreatePlan(request);

        var plannedTable = Assert.Single(plan.Tables);
        var plannedColumn = Assert.Single(
            plannedTable.Columns,
            item => item.SourceName.Equals(column.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expectedKey, produced.SourceKey.ColumnKey);
        Assert.Equal("discre_obsrv", plannedColumn.TargetName);
        Assert.Equal(includedColumnCount, mappedColumnCount);
        Assert.Empty(plan.RecoveredIdentifierMappings);
        Assert.Equal(119642663, trigger.SqlServerObjectId);
        Assert.Equal(1543491571, parentTable.SqlServerObjectId);
        Assert.Equal(parentTable.Id, triggerKey.ParentTableObjectId);
        Assert.Equal(trigger.Id, triggerKey.TriggerObjectId);
        Assert.Equal(mapper.MapObject(parentTable).QualifiedName, triggerMapping.TargetParentObject);
        Assert.True(triggerMapping.AutoRecovered);
        Assert.Equal(IdentifierMappingAction.AutoRecovered, triggerMapping.MappingAction);
        output.WriteLine(
            $"ObjectId={column.ObjectId}; ParentId={table.Id}; ColumnId={column.ColumnId}; " +
            $"ProducerKey={produced.SourceKey.ColumnKey}; Target={produced.TargetName}; " +
            $"Temporary={mappings.Length}; Published={conversion.MappingSet.PublishedMapCount}; " +
            $"IncludedColumns={includedColumnCount}; MappedColumns={mappedColumnCount}; " +
            $"MappingSet={conversion.MappingSet.MappingSetId}; Version={conversion.MappingSet.SchemaVersion}");
        output.WriteLine(
            $"TriggerObjectId={trigger.Id}; SqlObjectId={trigger.SqlServerObjectId}; " +
            $"ParentId={parentTable.Id}; ParentSqlObjectId={parentTable.SqlServerObjectId}; " +
            $"TriggerKey={triggerKey}; Target={triggerMapping.TargetName}; " +
            $"AutoRecovered={triggerMapping.AutoRecovered}");
    }

    [ProductionInventoryFact]
    [Trait("Category", "Integration")]
    public async Task PersistedVbgramgInventory_FullConversionPublishesCompleteMapBeforeArtifacts()
    {
        var inventory = await LoadProductionInventoryAsync();
        var identifiers = new PostgreSqlIdentifierMappingService();
        var types = new PostgreSqlTypeMappingRegistry();
        var expressions = new StructuredSqlExpressionTranslator();
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
        var engine = new ConversionEngine(
            converters,
            identifiers,
            types,
            expressions,
            new GeneratedSqlValidator(),
            NullLogger<ConversionEngine>.Instance);
        var progressSnapshots = new List<ConversionProgress>();
        var conversionStopwatch = Stopwatch.StartNew();
        var lastReportedStage = (ConversionStage?)null;

        var run = await engine.ConvertAsync(
            inventory,
            new ConversionOptions(),
            new InlineProgress<ConversionProgress>(snapshot =>
            {
                progressSnapshots.Add(snapshot);
                if (lastReportedStage != snapshot.Stage ||
                    snapshot.CompletedObjects == snapshot.TotalObjects ||
                    snapshot.CompletedObjects % 10_000 < 256)
                {
                    output.WriteLine(
                        $"{conversionStopwatch.Elapsed:c} {snapshot.Stage}: " +
                        $"{snapshot.CompletedObjects:N0}/{snapshot.TotalObjects:N0} " +
                        $"{snapshot.Percentage:F1}% {snapshot.CurrentObject}");
                    lastReportedStage = snapshot.Stage;
                }
            }),
            CancellationToken.None);
        conversionStopwatch.Stop();

        var trigger = Assert.Single(inventory.Objects, item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals(
                "TRG_DigiPay_TrainerDetailsHistory_Del",
                StringComparison.OrdinalIgnoreCase));
        var triggerMapping = Assert.Single(run.IdentifierMappings, item =>
            item.SourceKey.ObjectId == trigger.Id);
        var triggerArtifact = Assert.Single(run.Artifacts, item =>
            item.SourceObjectId == trigger.Id);
        Assert.True(triggerMapping.AutoRecovered);
        Assert.NotNull(triggerMapping.SourceKey.TriggerKey);
        Assert.False(string.IsNullOrWhiteSpace(triggerArtifact.PostgreSqlDefinition));
        Assert.Equal(0, run.MappingSet.UnresolvedRequiredCount);
        Assert.All(run.MappingSet.Coverage, item =>
            Assert.Equal(item.IncludedCount, item.MappedCount));

        var table = Assert.Single(inventory.Objects, item =>
            item.Id == trigger.ParentObjectId);
        var request = new DataMigrationRequest(
            inventory,
            run,
            new SqlServerConnectionOptions
            {
                Server = "runtime-test",
                Database = inventory.Database.DatabaseName
            },
            "Host=runtime-test;Database=runtime_test;Username=runtime_test;Password=not-used",
            new DataMigrationOptions
            {
                ExecutionMode = DataMigrationExecutionMode.Preview
            },
            SelectedTables: new HashSet<InventoryObjectId> { table.Id });
        var plan = new DataMigrationPlanner(new SensitiveColumnClassifier())
            .CreatePlan(request);

        Assert.Single(plan.Tables);
        Assert.Empty(plan.RecoveredIdentifierMappings);
        var computedFallbackTable = Assert.Single(inventory.Objects, item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("sp_tracking", StringComparison.OrdinalIgnoreCase) &&
            item.ObjectType == InventoryObjectType.Table);
        var computedFallbackPlan = new DataMigrationPlanner(new SensitiveColumnClassifier())
            .CreatePlan(request with
            {
                SelectedTables = new HashSet<InventoryObjectId> { computedFallbackTable.Id }
            });
        var computedFallbackColumn = Assert.Single(
            Assert.Single(computedFallbackPlan.Tables).Columns,
            item => item.SourceName.Equals("fin_to", StringComparison.OrdinalIgnoreCase));
        Assert.True(computedFallbackColumn.IsIncluded);
        Assert.Equal(
            GeneratedColumnLoadStrategy.PopulateFromSource,
            computedFallbackColumn.GeneratedStrategy);
        var identifierCompletion = Assert.Single(
            progressSnapshots,
            item => item.Stage == ConversionStage.GeneratingIdentifierCandidates &&
                    item.CompletedObjects == item.TotalObjects);
        Assert.True(identifierCompletion.TotalObjects >= 386_000);
        Assert.Contains(
            progressSnapshots,
            item => item.Stage == ConversionStage.ConvertingObjects &&
                    item.CompletedObjects > 0);
        Assert.Contains(
            progressSnapshots,
            item => item.Stage == ConversionStage.OrderingDependencies &&
                    item.CompletedObjects == item.TotalObjects);
        output.WriteLine(
            $"MappingSet={run.MappingSet.MappingSetId}; TotalMappings={run.IdentifierMappings.Count}; " +
            $"AutoRecovered={run.MappingSet.AutoRecoveredCount}; " +
            $"Unresolved={run.MappingSet.UnresolvedRequiredCount}; Artifacts={run.Artifacts.Count}; " +
            $"TriggerKey={triggerMapping.SourceKey.TriggerKey}; " +
            $"TriggerTarget={triggerMapping.TargetQualifiedName}; PreviewTables={plan.Tables.Count}");
        foreach (var coverage in run.MappingSet.Coverage)
        {
            output.WriteLine(
                $"{coverage.ObjectType}: {coverage.IncludedCount}/{coverage.MappedCount}");
        }

        var packageRoot = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_PACKAGE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(packageRoot))
        {
            var packageStopwatch = Stopwatch.StartNew();
            var package = await new MigrationPackageWriter(new ConversionReportWriter())
                .WriteAsync(
                    run,
                    packageRoot,
                    new InlineProgress<ConversionProgress>(snapshot =>
                    {
                        if (snapshot.CompletedObjects == snapshot.TotalObjects ||
                            snapshot.CompletedObjects % 512 == 0)
                        {
                            output.WriteLine(
                                $"{packageStopwatch.Elapsed:c} {snapshot.Stage}: " +
                                $"{snapshot.CompletedObjects:N0}/{snapshot.TotalObjects:N0} " +
                                $"{snapshot.CurrentObject}");
                        }
                    }),
                    CancellationToken.None);
            var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
                package,
                false,
                CancellationToken.None);
            packageStopwatch.Stop();

            Assert.Equal(run.RunId, manifest.MigrationRunId);
            Assert.NotEmpty(manifest.Files);
            Assert.NotEmpty(manifest.Artifacts);
            var assessmentFindings = new List<DeploymentFinding>();
            var deploymentOptions = new DeploymentOptions
            {
                Mode = DeploymentMode.GenerateOnly,
                Scope = DeploymentScope.CompletePackage
            };
            PreDeploymentAssessmentService.AssessManifest(
                manifest,
                deploymentOptions,
                null,
                assessmentFindings);
            var packageDuplicates = PreDeploymentAssessmentService.FindPackageDuplicates(
                manifest,
                deploymentOptions);
            Assert.Empty(packageDuplicates);
            output.WriteLine(
                $"ConversionDuration={conversionStopwatch.Elapsed:c}; " +
                $"PackageDuration={packageStopwatch.Elapsed:c}; Package={package}; " +
                $"ManifestFiles={manifest.Files.Count:N0}; " +
                $"ManifestArtifacts={manifest.Artifacts.Count:N0}; " +
                $"PackageDuplicates={packageDuplicates.Count:N0}; " +
                $"Warnings={assessmentFindings.Count(item => item.Severity == DeploymentFindingSeverity.Warning):N0}; " +
                $"Errors={assessmentFindings.Count(item => item.Severity == DeploymentFindingSeverity.Error):N0}; " +
                $"Critical={assessmentFindings.Count(item => item.Severity == DeploymentFindingSeverity.Critical):N0}");
        }
    }

    [ProductionPackageFact]
    [Trait("Category", "Integration")]
    public async Task ProductionPackage_HasNoDuplicateTargetsAndClassifiesOnlyRequiredBlockers()
    {
        var packagePath = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_EXISTING_PACKAGE")!;
        var manifest = await new MigrationPackageReader().ReadAndVerifyAsync(
            packagePath,
            false,
            CancellationToken.None);
        var options = new DeploymentOptions
        {
            Mode = DeploymentMode.GenerateOnly,
            Scope = DeploymentScope.CompletePackage
        };
        var findings = new List<DeploymentFinding>();
        PreDeploymentAssessmentService.AssessManifest(manifest, options, null, findings);
        var duplicates = PreDeploymentAssessmentService.FindPackageDuplicates(manifest, options);

        Assert.Empty(duplicates);
        Assert.DoesNotContain(findings, item => item.Code == "MANUAL.BLOCKER");
        output.WriteLine(
            $"Package={manifest.PackageId}; Format={manifest.FormatVersion}; " +
            $"Artifacts={manifest.Artifacts.Count:N0}; PackageDuplicates={duplicates.Count:N0}; " +
            $"Warnings={findings.Count(item => item.Severity == DeploymentFindingSeverity.Warning):N0}; " +
            $"Errors={findings.Count(item => item.Severity == DeploymentFindingSeverity.Error):N0}; " +
            $"Critical={findings.Count(item => item.Severity == DeploymentFindingSeverity.Critical):N0}; " +
            $"RequiredManualDependencies={findings.Count(item => item.Code == "MANUAL.REQUIRED_DEPENDENCY"):N0}; " +
            $"UnsupportedRequiredDependencies={findings.Count(item => item.Code == "OBJECT.UNSUPPORTED_REQUIRED_DEPENDENCY"):N0}");
        foreach (var group in findings
                     .GroupBy(item => item.Code)
                     .OrderByDescending(group => group.Count())
                     .Take(12))
        {
            output.WriteLine($"{group.Key}: {group.Count():N0}");
        }
        var artifactsById = manifest.Artifacts
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var group in findings
                     .Where(item => item.Code == "OBJECT.SQL_MISSING" && item.ObjectId is not null)
                     .SelectMany(item => artifactsById.GetValueOrDefault(item.ObjectId!.Value, []))
                     .GroupBy(item => $"{item.Phase}|{item.TargetObjectType}")
                     .OrderByDescending(group => group.Count()))
        {
            output.WriteLine($"SQL_MISSING {group.Key}: {group.Count():N0}");
        }
        foreach (var finding in findings
                     .Where(item => item.Severity == DeploymentFindingSeverity.Critical)
                     .Take(10))
        {
            output.WriteLine(
                $"CRITICAL {finding.Code} {finding.ObjectId}: {finding.Message}");
        }
        var executableById = manifest.Artifacts
            .Where(item => item.IsExecutable)
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var group in findings
                     .Where(item => item.Code == "ORDER.DEPENDENCY" && item.ObjectId is not null)
                     .SelectMany(finding =>
                     {
                         var artifact = executableById.GetValueOrDefault(finding.ObjectId!.Value);
                         return artifact is null
                             ? []
                             : artifact.Dependencies
                                 .Where(executableById.ContainsKey)
                                 .Where(dependency => executableById[dependency].Phase > artifact.Phase)
                                 .Select(dependency =>
                                     $"{artifact.Phase}|{artifact.TargetObjectType} -> " +
                                     $"{executableById[dependency].Phase}|{executableById[dependency].TargetObjectType}");
                     })
                     .GroupBy(item => item)
                     .OrderByDescending(group => group.Count()))
        {
            output.WriteLine($"ORDER {group.Key}: {group.Count():N0}");
        }
        foreach (var requiredManual in findings
                     .Where(item => item.Code == "MANUAL.REQUIRED_DEPENDENCY" &&
                         item.ObjectId is not null))
        {
            foreach (var required in manifest.Artifacts.Where(item =>
                         item.SourceObjectId == requiredManual.ObjectId))
            {
                output.WriteLine(
                    $"MANUAL_OBJECT {required.Phase}|{required.TargetObjectType} " +
                    $"{required.TargetSchema}.{required.TargetName}; " +
                    $"Unsupported={string.Join(",", required.UnsupportedConstructs)}; " +
                    $"Sql={required.Sql.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim()}");
            }
            foreach (var consumer in manifest.Artifacts.Where(item =>
                         item.IsExecutable &&
                         !item.RequiresManualReview &&
                         item.Dependencies.Contains(requiredManual.ObjectId!.Value)))
            {
                output.WriteLine(
                    $"REQUIRES_MANUAL {consumer.Phase}|{consumer.TargetObjectType} " +
                    $"{consumer.TargetSchema}.{consumer.TargetName} -> {requiredManual.ObjectId}");
            }
        }
        var deployableArtifacts = manifest.Artifacts.Where(item =>
                item.IsExecutable &&
                !item.RequiresManualReview &&
                item.Classification != ConversionClassification.Unsupported)
            .ToArray();
        foreach (var cycle in PreDeploymentAssessmentService.FindDependencyCycles(deployableArtifacts))
        {
            output.WriteLine($"CYCLE size={cycle.Count:N0}");
            foreach (var objectId in cycle.Take(12))
            {
                var artifact = executableById[objectId];
                output.WriteLine(
                    $"  {artifact.Phase}|{artifact.TargetObjectType} " +
                    $"{artifact.TargetSchema}.{artifact.TargetName}");
            }
        }
    }

    private static async Task<InventorySnapshot> LoadProductionInventoryAsync()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_INVENTORY_HISTORY")!;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ApplyProductionScope((await JsonSerializer.DeserializeAsync<InventorySnapshot>(
            stream,
            JsonOptions))!);
    }

    private static InventorySnapshot ApplyProductionScope(InventorySnapshot inventory)
    {
        var request = new InventoryDiscoveryRequest(
            new SqlServerConnectionOptions
            {
                Server = "production-inventory",
                Database = inventory.Database.DatabaseName
            },
            MigrationScopeMode.CompleteDatabase,
            new HashSet<string>(),
            new HashSet<InventoryObjectId>(),
            new HashSet<InventoryObjectId>(),
            DependencyPolicy.IncludeRequiredDependencies,
            new DiscoveryOptions());
        var scoped = InventoryScopeSelector.Apply(
            inventory,
            request,
            new SqlServerUserObjectScopePolicy());
        Assert.DoesNotContain(scoped.Objects, item =>
            item.IsIncluded &&
            (item.SourceSchema.Equals("sys", StringComparison.OrdinalIgnoreCase) ||
             item.SourceSchema.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(scoped.Objects, item =>
            item.IsIncluded &&
            item.SourceSchema.Equals("dbo", StringComparison.OrdinalIgnoreCase) &&
            item.ObjectType == InventoryObjectType.Table);
        Assert.Contains(scoped.Objects, item =>
            item.IsIncluded &&
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.ObjectType == InventoryObjectType.Table);
        return scoped;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

public sealed class ProductionInventoryFactAttribute : FactAttribute
{
    public ProductionInventoryFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_INVENTORY_HISTORY");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Skip =
                "Set MIGRATIONSTUDIO_PRODUCTION_INVENTORY_HISTORY to a sanitized discovery run-history payload.";
        }
    }
}

public sealed class ProductionPackageFactAttribute : FactAttribute
{
    public ProductionPackageFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_PRODUCTION_EXISTING_PACKAGE");
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Skip =
                "Set MIGRATIONSTUDIO_PRODUCTION_EXISTING_PACKAGE to a generated migration package.";
        }
    }
}
