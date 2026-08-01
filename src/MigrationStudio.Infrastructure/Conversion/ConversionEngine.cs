using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion.Converters;

namespace MigrationStudio.Infrastructure.Conversion;

public sealed partial class ConversionEngine(
    IEnumerable<IObjectConverter<InventoryObject, string>> converters,
    IIdentifierMappingService identifierMappingService,
    ITypeMappingRegistry typeMappings,
    ISqlExpressionTranslator expressions,
    IGeneratedSqlValidator validator,
    ILogger<ConversionEngine> logger) : IConversionEngine
{
    public async Task<ConversionRun> ConvertAsync(
        InventorySnapshot inventory,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);
        options.TargetVersion.Validate();
        ValidateOptions(options);
        inventory = ReconcileStructuralObjectTypes(inventory);

        var orderedConverters = converters.ToArray();
        if (orderedConverters.Length == 0 || orderedConverters[^1] is not FallbackObjectConverter)
        {
            throw new InvalidOperationException("The converter registry must end with FallbackObjectConverter.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ConversionProgress(
            ConversionStage.CollectingIncludedObjects,
            0,
            Math.Max(1, inventory.Objects.Count),
            "Collecting the immutable conversion scope."));
        var mapper = identifierMappingService.CreateMapper(
            inventory,
            options,
            cancellationToken,
            progress);
        var preConversionMappings = mapper.Mappings.ToArray();
        progress?.Report(new ConversionProgress(
            ConversionStage.ResolvingCollisions,
            preConversionMappings.Length,
            Math.Max(1, preConversionMappings.Length),
            "Identifier collisions resolved deterministically.")
        {
            MappingSetId = mapper.MappingSetId,
            LastProgressAt = DateTimeOffset.UtcNow
        });
        ValidateIdentifierMappingCompleteness(
            inventory,
            options,
            preConversionMappings,
            progress,
            cancellationToken);
        var preConversionCoverage = BuildMappingCoverage(
            inventory,
            options,
            preConversionMappings,
            cancellationToken);
        progress?.Report(new ConversionProgress(
            ConversionStage.PublishingIdentifierMap,
            preConversionMappings.Length,
            Math.Max(1, preConversionMappings.Length),
            $"Immutable identifier map {mapper.MappingSetId:N} is ready.")
        {
            MappingSetId = mapper.MappingSetId,
            LastProgressAt = DateTimeOffset.UtcNow
        });
        if (logger.IsEnabled(LogLevel.Information))
        {
            var autoRecoveredCount = preConversionMappings.Count(item => item.AutoRecovered);
            var unresolvedRequiredCount = preConversionCoverage.Sum(
                item => item.IncludedCount - item.MappedCount);
            LogMappingReady(
                logger,
                mapper.MappingSetId,
                mapper.SchemaVersion,
                preConversionMappings.Length,
                autoRecoveredCount,
                unresolvedRequiredCount);
        }

        var objectsById = inventory.Objects.ToDictionary(item => item.Id);
        var targets = inventory.Objects.ToDictionary(item => item.Id, mapper.MapObject);
        var dependenciesBySource = inventory.Dependencies
            .Where(item => item.TargetObjectId is not null)
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.TargetObjectId!.Value)
                    .Distinct()
                    .OrderBy(item => item.Value)
                    .ToArray());
        var context = new ConversionContext(
            inventory,
            options,
            mapper,
            typeMappings,
            expressions,
            objectsById,
            targets);
        var selected = inventory.Objects
            .Where(item => item.IsIncluded && !item.IsSystemObject)
            .Where(item => item.ObjectType != InventoryObjectType.Column)
            .Where(item => !IsExcludedSchema(item.SourceSchema, options))
            .OrderBy(item => item.ObjectType)
            .ThenBy(item => item.QualifiedSourceName, StringComparer.Ordinal)
            .ToArray();
        var artifacts = new List<ConversionArtifact>(selected.Length);
        var findings = new List<InventoryFinding>(inventory.Findings);
        foreach (var component in inventory.DependencyComponents.Where(item => item.IsCycle))
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.Add(new InventoryFinding(
                "CONVERSION.DEPENDENCY_CYCLE",
                FindingSeverity.Warning,
                $"Dependency cycle {component.Id} contains {component.Members.Count} objects and is emitted in deterministic fallback order.",
                component.Members.Count == 0 ? null : component.Members[0],
                string.Join(", ", component.Members)));
        }
        var requiredExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conversionStopwatch = Stopwatch.StartNew();
        var lastProgressAt = Stopwatch.GetTimestamp();

        LogStarting(logger, inventory.Database.DatabaseName, selected.Length, options.TargetVersion.Major);
        for (var index = 0; index < selected.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = selected[index];
            var converter = orderedConverters.First(item => item.CanConvert(source, context));
            var converted = await converter.ConvertAsync(source, context, cancellationToken).ConfigureAwait(false);
            var sql = converted.Target ?? string.Empty;
            if (sql.Length == 0)
            {
                converted = ConversionRuleSupport.Manual(
                    source,
                    "Converter returned no target SQL.",
                    $"-- Manual conversion required for {source.QualifiedSourceName}.",
                    "empty converter output");
                sql = converted.Target!;
            }

            var validation = await validator.ValidateOfflineAsync(sql, cancellationToken).ConfigureAwait(false);
            var objectFindings = converted.Findings.ToList();
            if (!validation.IsStructurallyValid)
            {
                objectFindings.Add(new InventoryFinding(
                    "POSTGRESQL.STRUCTURE_INVALID",
                    FindingSeverity.Error,
                    validation.Message ?? "Generated SQL failed offline structural validation.",
                    source.Id,
                    sql));
            }
            findings.AddRange(objectFindings);
            requiredExtensions.UnionWith(converted.RequiredExtensions);

            var dependencies = BuildArtifactDependencies(
                source,
                dependenciesBySource.GetValueOrDefault(source.Id) ?? [],
                context);
            var references = dependencies
                .Where(targets.ContainsKey)
                .Select(item => targets[item])
                .ToArray();
            var phase = GetPhase(source, options);
            var target = targets[source.Id];
            artifacts.Add(new ConversionArtifact(
                source.Id,
                target,
                source.SourceDefinition ?? BuildCatalogSourceDescription(source, inventory),
                sql,
                !validation.IsStructurallyValid && converted.Classification == ConversionClassification.Automatic
                    ? ConversionClassification.ManualConversion
                    : converted.Classification,
                converted.RuleId,
                converted.Confidence,
                objectFindings,
                dependencies,
                references,
                converted.RequiredExtensions,
                converted.RequiresManualReview || !validation.IsStructurallyValid,
                converted.UnsupportedConstructs,
                validation,
                phase,
                ScriptFile(phase),
                ConversionRuleSupport.Hash(sql)));

            var now = Stopwatch.GetTimestamp();
            if (index + 1 == selected.Length ||
                (index & 127) == 0 ||
                Stopwatch.GetElapsedTime(lastProgressAt, now) >= TimeSpan.FromMilliseconds(250))
            {
                lastProgressAt = now;
                var elapsed = conversionStopwatch.Elapsed;
                progress?.Report(new ConversionProgress(
                    ConversionStage.ConvertingObjects,
                    index + 1,
                    selected.Length,
                    $"Converted {source.QualifiedSourceName}")
                {
                    ObjectsPerSecond = elapsed.TotalSeconds <= 0 ? 0 : (index + 1) / elapsed.TotalSeconds,
                    Elapsed = elapsed,
                    CurrentObjectType = source.ObjectType.ToString(),
                    CurrentObject = source.QualifiedSourceName,
                    MappingSetId = mapper.MappingSetId,
                    LastProgressAt = DateTimeOffset.UtcNow
                });
            }
        }

        await AddCommentArtifactsAsync(
            inventory, mapper, artifacts, findings, validator, cancellationToken).ConfigureAwait(false);
        await AddIdentitySequenceArtifactsAsync(
            inventory, options, mapper, artifacts, validator, cancellationToken).ConfigureAwait(false);
        await AddIdentityResetArtifactsAsync(
            inventory, options, mapper, artifacts, findings, validator, cancellationToken).ConfigureAwait(false);
        var publishedMappings = mapper.Mappings.ToArray();
        var includedTableIds = inventory.Objects
            .Where(item =>
                item.IsIncluded &&
                !item.IsSystemObject &&
                item.ObjectType == InventoryObjectType.Table &&
                !IsExcludedSchema(item.SourceSchema, options))
            .Select(item => item.Id)
            .ToHashSet();
        var includedColumnCount = inventory.Columns.Count(item =>
            includedTableIds.Contains(item.ParentObjectId));
        var mappedColumnCount = publishedMappings
            .Where(item =>
                item.IncludedInScope &&
                item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase) &&
                item.SourceKey.ColumnKey is { } key &&
                includedTableIds.Contains(key.TableObjectId))
            .Select(item => item.SourceKey.ColumnKey!.Value)
            .Distinct()
            .Count();
        if (includedColumnCount != mappedColumnCount)
        {
            throw new InvalidOperationException(
                $"Identifier mapping publication rejected: {includedColumnCount:N0} included columns " +
                $"but {mappedColumnCount:N0} canonical column mappings.");
        }

        AssertDiagnosticColumnMapping(
            inventory,
            publishedMappings,
            mapper.MappingSetId,
            mapper.SchemaVersion,
            mapper.LoadedFromCache,
            logger);
        var mappingSet = new IdentifierMappingSetMetadata(
            mapper.MappingSetId,
            mapper.SchemaVersion,
            DateTimeOffset.UtcNow,
            mapper.LoadedFromCache,
            mapper.Mappings.Count,
            publishedMappings.Length,
            includedColumnCount,
            mappedColumnCount)
        {
            Coverage = BuildMappingCoverage(
                inventory,
                options,
                publishedMappings,
                cancellationToken),
            AutoRecoveredCount = publishedMappings.Count(item => item.AutoRecovered),
            UnresolvedRequiredCount = 0
        };
        artifacts = PromotePreDataFunctions(artifacts);
        progress?.Report(new ConversionProgress(
            ConversionStage.OrderingDependencies,
            0,
            Math.Max(1, artifacts.Count),
            "Ordering converted objects by dependency."));
        var orderedArtifacts = OrderArtifacts(artifacts, inventory.Dependencies, cancellationToken);
        progress?.Report(new ConversionProgress(
            ConversionStage.OrderingDependencies,
            artifacts.Count,
            Math.Max(1, artifacts.Count),
            "Dependency ordering completed."));
        var run = new ConversionRun(
            DeterministicRunId(inventory, options),
            inventory.SnapshotTimestamp,
            inventory.Database.DatabaseName,
            options.TargetVersion,
            options,
            publishedMappings,
            BuildTypeReport(inventory, typeMappings, options, objectsById, cancellationToken),
            orderedArtifacts,
            findings,
            requiredExtensions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            typeof(ConversionEngine).Assembly.GetName().Version?.ToString() ?? "1.0.0")
        {
            MappingSet = mappingSet
        };
        LogCompleted(logger, run.Artifacts.Count, run.Findings.Count, run.RequiresManualReview);
        return run;
    }

    private static void AssertDiagnosticColumnMapping(
        InventorySnapshot inventory,
        IdentifierMappingEntry[] mappings,
        Guid mappingSetId,
        int mappingVersion,
        bool loadedFromCache,
        ILogger logger)
    {
        var table = inventory.Objects.FirstOrDefault(item =>
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase) &&
            inventory.Tables.Any(facet => facet.ObjectId == item.Id));
        if (table is null)
        {
            return;
        }

        var column = inventory.Columns.FirstOrDefault(item =>
            item.ParentObjectId == table.Id &&
            item.Name.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase));
        if (column is null)
        {
            return;
        }

        var key = new ColumnIdentifierKey(table.Id, column.ColumnId);
        var mapping = mappings.SingleOrDefault(item =>
            item.SourceKey.ColumnKey == key);
        if (logger.IsEnabled(LogLevel.Information))
        {
            var details =
                $"ConversionComplete; ObjectId={column.ObjectId}; ParentTableObjectId={table.Id}; " +
                $"ColumnId={column.ColumnId}; Schema={table.SourceSchema}; Table={table.SourceName}; " +
                $"Column={column.Name}; ProducerKey={key}; TargetIdentifier={mapping?.TargetName ?? string.Empty}; " +
                $"MappingSetId={mappingSetId}; MappingVersion={mappingVersion}; Exists={mapping is not null}; " +
                $"Included={mapping?.IncludedInScope ?? false}; LoadedFromCache={loadedFromCache}; " +
                $"TemporaryMapCount={mappings.Length}; PublishedMapCount={mappings.Length}";
            LogIdentifierLifecycle(logger, details);
        }
        if (mapping is null)
        {
            throw new InvalidOperationException(
                $"Temporary diagnostic assertion failed: no canonical mapping exists for " +
                $"{table.QualifiedSourceName}.{column.Name} using {key}.");
        }
    }

    private static InventorySnapshot ReconcileStructuralObjectTypes(InventorySnapshot inventory)
    {
        var tableIds = inventory.Tables.Select(item => item.ObjectId).ToHashSet();
        var sequenceIds = inventory.Sequences.Select(item => item.ObjectId).ToHashSet();
        var synonymIds = inventory.Synonyms.Select(item => item.ObjectId).ToHashSet();
        var typeIds = inventory.UserDefinedTypes.Select(item => item.ObjectId).ToHashSet();
        var reconciled = inventory.Objects.Select(item =>
        {
            if (item.ObjectType != InventoryObjectType.Unknown)
            {
                return item;
            }

            var type = tableIds.Contains(item.Id)
                ? InventoryObjectType.Table
                : sequenceIds.Contains(item.Id)
                    ? InventoryObjectType.Sequence
                    : synonymIds.Contains(item.Id)
                        ? InventoryObjectType.Synonym
                        : typeIds.Contains(item.Id)
                            ? InventoryObjectType.UserDefinedType
                            : InventoryObjectType.Unknown;
            return type == InventoryObjectType.Unknown
                ? item
                : item with
                {
                    ObjectType = type,
                    ConversionClassification = InventoryClassification.ForObject(type)
                };
        }).ToArray();
        return inventory with { Objects = reconciled };
    }

    private static void ValidateIdentifierMappingCompleteness(
        InventorySnapshot inventory,
        ConversionOptions options,
        IReadOnlyList<IdentifierMappingEntry> mappings,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var unresolved = new List<string>();
        var includedObjects = inventory.Objects
            .Where(item => item.IsIncluded && !item.IsSystemObject)
            .Where(item => !IsExcludedSchema(item.SourceSchema, options))
            .ToDictionary(item => item.Id);
        var mappedObjectIds = mappings
            .Where(item => item.IncludedInScope && item.SourceKey.ObjectId is not null)
            .Select(item => item.SourceKey.ObjectId!.Value)
            .ToHashSet();
        var mappedColumns = mappings
            .Where(item =>
                item.IncludedInScope &&
                item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase) &&
                item.SourceKey.ParentObjectId is not null &&
                item.SourceKey.ObjectId is not null)
            .Select(item => (
                Parent: item.SourceKey.ParentObjectId!.Value,
                Column: item.SourceKey.ObjectId!.Value))
            .ToHashSet();
        var objectCandidates = includedObjects.Values
                     .Where(item => item.ObjectType != InventoryObjectType.Column)
                     .OrderBy(item => item.Id.Value)
                     .ToArray();
        var columnCandidates = inventory.Columns
            .Where(item => includedObjects.ContainsKey(item.ParentObjectId))
            .OrderBy(item => item.ParentObjectId.Value)
            .ThenBy(item => item.OrdinalPosition)
            .ToArray();
        var blockingCandidates = mappings
            .Where(item => item.IncludedInScope && item.IsBlocking)
            .ToArray();
        var total = objectCandidates.Length + columnCandidates.Length + blockingCandidates.Length;
        var processed = 0;
        void Tick(string objectType)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            if (processed != total && processed % 256 != 0)
            {
                return;
            }
            var elapsed = stopwatch.Elapsed;
            progress?.Report(new ConversionProgress(
                ConversionStage.ValidatingIdentifiers,
                processed,
                Math.Max(1, total),
                $"Validated {processed:N0}/{total:N0} identifiers · " +
                $"{(elapsed.TotalSeconds <= 0 ? 0 : processed / elapsed.TotalSeconds):N0} mappings/sec")
            {
                ObjectsPerSecond = elapsed.TotalSeconds <= 0 ? 0 : processed / elapsed.TotalSeconds,
                Elapsed = elapsed,
                CurrentObjectType = objectType,
                LastProgressAt = DateTimeOffset.UtcNow
            });
        }

        foreach (var source in objectCandidates)
        {
            Tick(source.ObjectType.ToString());
            if (!mappedObjectIds.Contains(source.Id))
            {
                unresolved.Add($"{source.ObjectType}: {source.QualifiedSourceName}");
            }
        }

        foreach (var column in columnCandidates)
        {
            Tick("Column");
            if (!mappedColumns.Contains((column.ParentObjectId, column.ObjectId)))
            {
                var owner = includedObjects[column.ParentObjectId];
                unresolved.Add($"Column: {owner.QualifiedSourceName}.{column.Name}");
            }
        }

        foreach (var item in blockingCandidates)
        {
            Tick(item.ObjectType);
            unresolved.Add($"Blocking: {item.SourceQualifiedName} ({item.MappingReason})");
        }
        if (unresolved.Count == 0)
        {
            return;
        }

        var examples = string.Join(
            Environment.NewLine,
            unresolved.Take(100).Select(item => $"- {item}"));
        throw new InvalidOperationException(
            $"Central identifier mapping is incomplete for {unresolved.Count:N0} included identifiers." +
            $"{Environment.NewLine}{examples}" +
            (unresolved.Count > 100
                ? $"{Environment.NewLine}- … {unresolved.Count - 100:N0} additional identifiers"
                : string.Empty));
    }

    private static IdentifierMappingCoverage[] BuildMappingCoverage(
        InventorySnapshot inventory,
        ConversionOptions options,
        IReadOnlyList<IdentifierMappingEntry> mappings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var included = inventory.Objects
            .Where(item => item.IsIncluded && !item.IsSystemObject)
            .Where(item => !IsExcludedSchema(item.SourceSchema, options))
            .GroupBy(item => item.ObjectType)
            .OrderBy(group => group.Key)
            .ToArray();
        var mappedIds = mappings
            .Where(item => item.IncludedInScope && item.SourceKey.ObjectId is not null)
            .Select(item => item.SourceKey.ObjectId!.Value)
            .ToHashSet();
        var result = new List<IdentifierMappingCoverage>(included.Length);
        foreach (var group in included)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = group.Select(item => item.Id).Distinct().ToArray();
            result.Add(new IdentifierMappingCoverage(
                group.Key.ToString(),
                ids.Length,
                ids.Count(mappedIds.Contains)));
        }
        return result.ToArray();
    }

    private static async Task AddCommentArtifactsAsync(
        InventorySnapshot inventory,
        IIdentifierMapper mapper,
        List<ConversionArtifact> artifacts,
        List<InventoryFinding> findings,
        IGeneratedSqlValidator validator,
        CancellationToken cancellationToken)
    {
        foreach (var source in inventory.Objects.Where(item => item.IsIncluded && item.ExtendedProperties.Count > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var descriptions = source.ExtendedProperties
                .Where(item => item.Name.Equals("MS_Description", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (descriptions.Length == 0)
            {
                continue;
            }
            var target = mapper.MapObject(source);
            var kind = source.ObjectType switch
            {
                InventoryObjectType.Schema => "SCHEMA",
                InventoryObjectType.Table => "TABLE",
                InventoryObjectType.View => "VIEW",
                InventoryObjectType.Function => "FUNCTION",
                InventoryObjectType.StoredProcedure => "PROCEDURE",
                InventoryObjectType.UserDefinedType or InventoryObjectType.TableType => "TYPE",
                _ => null
            };
            if (kind is null)
            {
                findings.Add(new InventoryFinding(
                    "COMMENT.PROPERTY_REPORTED",
                    FindingSeverity.Information,
                    $"Extended properties for {source.QualifiedSourceName} are reported but not emitted as COMMENT statements.",
                    source.Id,
                    null));
                continue;
            }
            var text = descriptions[0].Value ?? string.Empty;
            var sql = $"COMMENT ON {kind} {target.QualifiedName} IS '{ConversionRuleSupport.EscapeLiteral(text)}';";
            var validation = await validator.ValidateOfflineAsync(sql, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ConversionArtifact(
                source.Id,
                target with { ObjectType = $"Comment:{target.ObjectType}" },
                text,
                sql,
                ConversionClassification.Automatic,
                "COMMENT.MS_DESCRIPTION",
                1m,
                [],
                [source.Id],
                [target],
                [],
                false,
                [],
                validation,
                DeploymentPhase.Comments,
                ScriptFile(DeploymentPhase.Comments),
                ConversionRuleSupport.Hash(sql)));
        }

        var includedTables = inventory.Objects
            .Where(item => item.IsIncluded && item.ObjectType == InventoryObjectType.Table)
            .ToDictionary(item => item.Id);
        foreach (var column in inventory.Columns.Where(item =>
                     includedTables.ContainsKey(item.ParentObjectId) && item.ExtendedProperties.Count > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var description = column.ExtendedProperties.FirstOrDefault(item =>
                item.Name.Equals("MS_Description", StringComparison.OrdinalIgnoreCase));
            if (description is null)
            {
                continue;
            }
            var table = includedTables[column.ParentObjectId];
            var targetTable = mapper.MapObject(table);
            var targetColumn = mapper.MapChildIdentifier(table.Id, "column", table.SourceSchema, column.Name);
            var target = new TargetObjectIdentifier(
                "Column",
                targetTable.Schema,
                $"{targetTable.Name}.{targetColumn}");
            var text = description.Value ?? string.Empty;
            var sql = $"COMMENT ON COLUMN {target.QualifiedName} IS '{ConversionRuleSupport.EscapeLiteral(text)}';";
            var validation = await validator.ValidateOfflineAsync(sql, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ConversionArtifact(
                column.ObjectId,
                target,
                text,
                sql,
                ConversionClassification.Automatic,
                "COMMENT.COLUMN.MS_DESCRIPTION",
                1m,
                [],
                [table.Id],
                [targetTable],
                [],
                false,
                [],
                validation,
                DeploymentPhase.Comments,
                ScriptFile(DeploymentPhase.Comments),
                ConversionRuleSupport.Hash(sql)));
        }
    }

    private static TypeMappingResult[] BuildTypeReport(
        InventorySnapshot inventory,
        ITypeMappingRegistry mappings,
        ConversionOptions options,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        CancellationToken cancellationToken)
    {
        var results = new List<TypeMappingResult>();
        var seen = new HashSet<(string SourceType, string TargetType, string RuleId)>();
        var index = 0;
        foreach (var column in inventory.Columns)
        {
            if ((index++ & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!objectsById.TryGetValue(column.ParentObjectId, out var table) || !table.IsIncluded)
            {
                continue;
            }
            var mapped = mappings.Map(column, table, options);
            if (seen.Add((mapped.SourceType, mapped.TargetType, mapped.RuleId)))
            {
                results.Add(mapped);
            }
        }
        return results.OrderBy(item => item.SourceType, StringComparer.Ordinal).ToArray();
    }

    private static async Task AddIdentityResetArtifactsAsync(
        InventorySnapshot inventory,
        ConversionOptions options,
        IIdentifierMapper mapper,
        List<ConversionArtifact> artifacts,
        List<InventoryFinding> findings,
        IGeneratedSqlValidator validator,
        CancellationToken cancellationToken)
    {
        if (options.IdentityStrategy == IdentityConversionStrategy.PlainIntegerManual)
        {
            return;
        }
        var tables = inventory.Objects
            .Where(item => item.IsIncluded && item.ObjectType == InventoryObjectType.Table)
            .ToDictionary(item => item.Id);
        foreach (var column in inventory.Columns.Where(item =>
                     item.IsIdentity && tables.ContainsKey(item.ParentObjectId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = tables[column.ParentObjectId];
            var targetTable = mapper.MapObject(table);
            var targetColumn = mapper.MapChildIdentifier(table.Id, "column", table.SourceSchema, column.Name);
            var seed = column.IdentitySeed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            string sequenceExpression;
            TargetObjectIdentifier target;
            if (options.IdentityStrategy == IdentityConversionStrategy.SequenceAndDefault)
            {
                var sequence = mapper.MapChildIdentifier(
                    table.Id, "sequence", table.SourceSchema, $"{table.SourceName}_{column.Name}_seq");
                target = new TargetObjectIdentifier("SequenceReset", targetTable.Schema, sequence);
                sequenceExpression = $"'{target.QualifiedName.Replace("'", "''", StringComparison.Ordinal)}'::regclass";
            }
            else
            {
                target = new TargetObjectIdentifier(
                    "IdentityReset",
                    targetTable.Schema,
                    $"{targetTable.Name}.{targetColumn}");
                sequenceExpression =
                    $"pg_get_serial_sequence('{targetTable.QualifiedName.Replace("'", "''", StringComparison.Ordinal)}', '{targetColumn.Replace("'", "''", StringComparison.Ordinal)}')";
            }
            var sql = $"SELECT setval({sequenceExpression}, " +
                      $"GREATEST(COALESCE((SELECT MAX({targetColumn}) FROM {targetTable.QualifiedName}), {seed}), {seed}), true);";
            var validation = await validator.ValidateOfflineAsync(sql, cancellationToken).ConfigureAwait(false);
            var finding = new InventoryFinding(
                "IDENTITY.POST_LOAD_RESET",
                FindingSeverity.Information,
                $"Reset the PostgreSQL identity/sequence for {targetTable.QualifiedName}.{targetColumn} after explicit identity values are loaded.",
                table.Id,
                null);
            findings.Add(finding);
            artifacts.Add(new ConversionArtifact(
                column.ObjectId,
                target,
                $"Identity seed {seed}; last source value {column.IdentityLastValue}",
                sql,
                ConversionClassification.Automatic,
                "IDENTITY.SEQUENCE_RESET",
                1m,
                [finding],
                [table.Id],
                [targetTable],
                [],
                false,
                [],
                validation,
                DeploymentPhase.SequenceReset,
                ScriptFile(DeploymentPhase.SequenceReset),
                ConversionRuleSupport.Hash(sql)));
        }
    }

    private static async Task AddIdentitySequenceArtifactsAsync(
        InventorySnapshot inventory,
        ConversionOptions options,
        IIdentifierMapper mapper,
        List<ConversionArtifact> artifacts,
        IGeneratedSqlValidator validator,
        CancellationToken cancellationToken)
    {
        if (options.IdentityStrategy != IdentityConversionStrategy.SequenceAndDefault)
        {
            return;
        }
        var tables = inventory.Objects
            .Where(item => item.IsIncluded && item.ObjectType == InventoryObjectType.Table)
            .ToDictionary(item => item.Id);
        foreach (var column in inventory.Columns.Where(item =>
                     item.IsIdentity && tables.ContainsKey(item.ParentObjectId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var table = tables[column.ParentObjectId];
            var targetTable = mapper.MapObject(table);
            var sequence = mapper.MapChildIdentifier(
                table.Id, "sequence", table.SourceSchema, $"{table.SourceName}_{column.Name}_seq");
            var target = new TargetObjectIdentifier("IdentitySequence", targetTable.Schema, sequence);
            var seed = column.IdentitySeed?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            var increment = column.IdentityIncrement?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1";
            var sql = $"CREATE SEQUENCE {target.QualifiedName} START WITH {seed} INCREMENT BY {increment};";
            var validation = await validator.ValidateOfflineAsync(sql, cancellationToken).ConfigureAwait(false);
            artifacts.Add(new ConversionArtifact(
                column.ObjectId,
                target,
                $"SQL Server identity {table.QualifiedSourceName}.{column.Name}",
                sql,
                ConversionClassification.Automatic,
                "IDENTITY.EXPLICIT_SEQUENCE",
                1m,
                [],
                [],
                [],
                [],
                false,
                [],
                validation,
                DeploymentPhase.Sequences,
                "04_IdentitySequences.sql",
                ConversionRuleSupport.Hash(sql)));
        }
    }

    private static List<ConversionArtifact> OrderArtifacts(
        List<ConversionArtifact> artifacts,
        IReadOnlyList<InventoryDependency> dependencies,
        CancellationToken cancellationToken)
    {
        _ = dependencies;
        cancellationToken.ThrowIfCancellationRequested();
        return ArtifactDependencyPlanner.Order(
                artifacts,
                item => item.SourceObjectId,
                item => item.Dependencies,
                item => DeploymentPhaseOrdering.GetRank(
                    item.DeploymentPhase,
                    item.TargetObjectId.ObjectType),
                item => $"{item.TargetObjectId.QualifiedName}|{item.RuleId}|{item.ContentHash}",
                failOnCycle: false)
            .ToList();
    }

    private static List<ConversionArtifact> PromotePreDataFunctions(
        IReadOnlyList<ConversionArtifact> artifacts)
    {
        var requiredIds = ArtifactDependencyPlanner.GetTransitiveDependencyClosure(
            artifacts,
            item => item.SourceObjectId,
            item => item.Dependencies,
            item => IsIntrinsicPreDataPhase(item.DeploymentPhase));
        return artifacts
            .Select(item =>
                item.DeploymentPhase == DeploymentPhase.Functions &&
                requiredIds.Contains(item.SourceObjectId)
                    ? item with
                    {
                        DeploymentPhase = DeploymentPhase.PreDataFunctions,
                        ScriptFileName = ScriptFile(DeploymentPhase.PreDataFunctions)
                    }
                    : item)
            .ToList();
    }

    private static bool IsIntrinsicPreDataPhase(DeploymentPhase phase) =>
        phase is DeploymentPhase.PreDeployment
            or DeploymentPhase.Extensions
            or DeploymentPhase.Schemas
            or DeploymentPhase.Types
            or DeploymentPhase.Sequences
            or DeploymentPhase.Tables
            or DeploymentPhase.DefaultsAndGeneratedColumns
            or DeploymentPhase.PrimaryKeys
            or DeploymentPhase.UniqueConstraints
            or DeploymentPhase.CheckConstraints;

    private static InventoryObjectId[] BuildArtifactDependencies(
        InventoryObject source,
        IReadOnlyList<InventoryObjectId> discoveredDependencies,
        ConversionContext context)
    {
        var dependencies = discoveredDependencies.ToHashSet();
        if (source.ParentObjectId is { } parentId)
        {
            dependencies.Add(parentId);
        }
        if (context.InventoryIndex.ConstraintsByObjectId.TryGetValue(source.Id, out var constraint))
        {
            dependencies.Add(constraint.TableObjectId);
            if (constraint.ReferencedTableObjectId is { } referencedTableId)
            {
                dependencies.Add(referencedTableId);
            }
        }
        if (context.InventoryIndex.IndexesByObjectId.TryGetValue(source.Id, out var index))
        {
            dependencies.Add(index.TableObjectId);
        }
        return dependencies
            .Where(item => item != source.Id)
            .OrderBy(item => item.Value)
            .ToArray();
    }

    private static DeploymentPhase GetPhase(
        InventoryObject source,
        ConversionOptions options) =>
        source.ObjectType switch
        {
            InventoryObjectType.Schema => DeploymentPhase.Schemas,
            InventoryObjectType.UserDefinedType or InventoryObjectType.TableType => DeploymentPhase.Types,
            InventoryObjectType.Sequence => DeploymentPhase.Sequences,
            InventoryObjectType.Table or InventoryObjectType.ExternalTable => DeploymentPhase.Tables,
            InventoryObjectType.PrimaryKey => DeploymentPhase.PrimaryKeys,
            InventoryObjectType.UniqueConstraint => DeploymentPhase.UniqueConstraints,
            InventoryObjectType.CheckConstraint => DeploymentPhase.CheckConstraints,
            InventoryObjectType.ForeignKey => DeploymentPhase.ForeignKeys,
            InventoryObjectType.DefaultConstraint => DeploymentPhase.DefaultsAndGeneratedColumns,
            InventoryObjectType.Index => DeploymentPhase.Indexes,
            InventoryObjectType.View => DeploymentPhase.Views,
            InventoryObjectType.Function => DeploymentPhase.Functions,
            InventoryObjectType.StoredProcedure => DeploymentPhase.Procedures,
            InventoryObjectType.Trigger => DeploymentPhase.Triggers,
            InventoryObjectType.User or InventoryObjectType.Role or InventoryObjectType.ApplicationRole or
                InventoryObjectType.Permission => DeploymentPhase.Security,
            _ => DeploymentPhase.ManualReview
        };

    private static string ScriptFile(DeploymentPhase phase) => phase switch
    {
        DeploymentPhase.PreDeployment => "00_PreDeployment.sql",
        DeploymentPhase.Extensions => "01_Extensions.sql",
        DeploymentPhase.Schemas => "02_Schemas.sql",
        DeploymentPhase.Types => "03_Types.sql",
        DeploymentPhase.Sequences => "10_Sequences.sql",
        DeploymentPhase.Tables => "05_Tables.sql",
        DeploymentPhase.PreDataFunctions => "06_PreDataFunctions.sql",
        DeploymentPhase.DefaultsAndGeneratedColumns => "06_DefaultsAndGeneratedColumns.sql",
        DeploymentPhase.PrimaryKeys => "07_PrimaryKeys.sql",
        DeploymentPhase.UniqueConstraints => "08_UniqueConstraints.sql",
        DeploymentPhase.CheckConstraints => "09_CheckConstraints.sql",
        DeploymentPhase.Data => "10_Data.sql",
        DeploymentPhase.SequenceReset => "11_SequenceReset.sql",
        DeploymentPhase.ForeignKeys => "12_ForeignKeys.sql",
        DeploymentPhase.Indexes => "13_Indexes.sql",
        DeploymentPhase.Functions => "14_Functions.sql",
        DeploymentPhase.Procedures => "15_Procedures.sql",
        DeploymentPhase.Views => "16_Views.sql",
        DeploymentPhase.Triggers => "17_Triggers.sql",
        DeploymentPhase.Security => "18_Security.sql",
        DeploymentPhase.Comments => "19_Comments.sql",
        DeploymentPhase.PostDeployment => "20_PostDeployment.sql",
        _ => "ManualReview/ManualReview.sql"
    };

    private static bool IsExcludedSchema(string schema, ConversionOptions options) =>
        options.SchemaMappings.Any(item =>
            item.IsExcluded && string.Equals(item.SourceSchema, schema, StringComparison.OrdinalIgnoreCase));

    private static void ValidateOptions(ConversionOptions options)
    {
        var duplicateSource = options.SchemaMappings
            .GroupBy(item => item.SourceSchema, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSource is not null)
        {
            throw new InvalidOperationException(
                $"Source schema '{duplicateSource.Key}' has more than one mapping.");
        }
        if (options.SchemaMappings.Any(item =>
                !item.IsExcluded && string.IsNullOrWhiteSpace(item.TargetSchema)))
        {
            throw new InvalidOperationException("Every included schema mapping requires a target schema.");
        }
        if (options.SchemaMappingMode == SchemaMappingMode.Custom)
        {
            var duplicateTarget = options.SchemaMappings
                .Where(item => !item.IsExcluded)
                .GroupBy(item => item.TargetSchema, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateTarget is not null)
            {
                throw new InvalidOperationException(
                    $"Custom mappings target schema '{duplicateTarget.Key}' more than once. Use MapAllToOne for intentional consolidation.");
            }
        }
    }

    private static string BuildCatalogSourceDescription(
        InventoryObject source,
        InventorySnapshot inventory) =>
        $"-- Catalog-derived SQL Server {source.ObjectType}: {source.QualifiedSourceName}{Environment.NewLine}" +
        $"-- Metadata hash: {source.MetadataHash}";

    private static Guid DeterministicRunId(InventorySnapshot inventory, ConversionOptions options)
    {
        var input = $"{inventory.Database.DatabaseName}\u001f{inventory.SnapshotTimestamp:O}\u001f{JsonSerializer.Serialize(options)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(bytes.AsSpan(0, 16));
    }

    [LoggerMessage(EventId = 2200, Level = LogLevel.Information, Message = "Converting {ObjectCount} objects from {Database} to PostgreSQL {TargetVersion}.")]
    private static partial void LogStarting(ILogger logger, string database, int objectCount, int targetVersion);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Information, Message = "Conversion produced {ArtifactCount} artifacts and {FindingCount} findings. Manual review: {ManualReview}.")]
    private static partial void LogCompleted(ILogger logger, int artifactCount, int findingCount, bool manualReview);

    [LoggerMessage(EventId = 2212, Level = LogLevel.Information, Message = "Identifier lifecycle {Details}")]
    private static partial void LogIdentifierLifecycle(ILogger logger, string details);

    [LoggerMessage(
        EventId = 2214,
        Level = LogLevel.Information,
        Message = "Central identifier map ready before conversion. MappingSetId={MappingSetId}, SchemaVersion={SchemaVersion}, MappingCount={MappingCount}, AutoRecovered={AutoRecovered}, UnresolvedRequired={UnresolvedRequired}.")]
    private static partial void LogMappingReady(
        ILogger logger,
        Guid mappingSetId,
        int schemaVersion,
        int mappingCount,
        int autoRecovered,
        int unresolvedRequired);
}
