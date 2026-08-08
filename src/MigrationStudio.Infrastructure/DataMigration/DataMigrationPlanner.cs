using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using Npgsql;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed partial class DataMigrationPlanner(
    ISensitiveColumnClassifier sensitiveClassifier,
    IMigrationWavePlanner? migrationWavePlanner = null,
    IIdentifierMappingService? identifierMappingService = null,
    ILogger<DataMigrationPlanner>? logger = null) : IDataMigrationPlanner
{
    public DataMigrationPlan CreatePlan(DataMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Conversion.MappingSet.SchemaVersion != IdentifierMappingSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"The active identifier mapping set uses stale schema version " +
                $"{request.Conversion.MappingSet.SchemaVersion}; reconvert the current inventory.");
        }
        var options = request.Options.Validate();
        var selected = request.SelectedTables;
        var objects = request.Inventory.Objects.ToDictionary(item => item.Id);
        var canonicalMapper = (identifierMappingService ??
            new MigrationStudio.Infrastructure.Conversion.PostgreSqlIdentifierMappingService()).CreateMapper(
                request.Inventory,
                request.Conversion.Options);
        var canonicalMappings = canonicalMapper.Mappings;
        var recoveredMappings = new List<IdentifierMappingEntry>();
        var caseSensitive = IsCaseSensitiveCollation(request.Inventory.Database.Collation);
        var targetMappings = request.Conversion.IdentifierMappings
            .Where(item => item.ObjectType.Equals("table", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Last());
        var columnTargets = request.Conversion.IdentifierMappings
            .Where(item => item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => ColumnLookupKey(
                item.SourceObjectId,
                item.SourceName,
                caseSensitive))
            .ToDictionary(
                group => group.Key,
                group => Unquote(group.Last().TargetName),
                StringComparer.Ordinal);
        var columnTargetsByKey = request.Conversion.IdentifierMappings
            .Where(item => item.SourceKey.ColumnKey is not null)
            .GroupBy(item => item.SourceKey.ColumnKey!.Value)
            .ToDictionary(group => group.Key, group => group.Last());
        var overrides = options.TableOverrides.ToDictionary(item => item.TableId);
        var primaryKeys = request.Inventory.Constraints
            .Where(item => item.Kind == ConstraintKind.PrimaryKey)
            .GroupBy(item => item.TableObjectId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.First().Columns
                    .OrderBy(item => item.Ordinal)
                    .Select(item => item.Name)
                    .ToArray());
        var dependencies = BuildTableDependencies(request.Inventory);
        var defaultOrder = TopologicalOrder(
            request.Inventory.Tables.Select(item => item.ObjectId),
            dependencies);
        var waves = (migrationWavePlanner ?? new MigrationWavePlanner()).CreatePlan(request.Inventory);
        var waveByTable = waves.Waves
            .SelectMany(wave => wave.Items.Select(item => (item.ObjectId, wave.Sequence)))
            .Where(item => item.ObjectId != default)
            .ToDictionary(item => item.ObjectId, item => item.Sequence);
        var warnings = new List<string>();
        var plans = new List<TableLoadPlan>();

        foreach (var table in request.Inventory.Tables)
        {
            if (!objects.TryGetValue(table.ObjectId, out var source) ||
                !source.IsIncluded ||
                selected is not null && !selected.Contains(table.ObjectId))
            {
                continue;
            }

            overrides.TryGetValue(table.ObjectId, out var tableOverride);
            if (tableOverride?.IsExcluded == true)
            {
                continue;
            }

            if (!targetMappings.TryGetValue(table.ObjectId, out var target))
            {
                target = canonicalMappings.LastOrDefault(item =>
                    item.SourceKey.ObjectId == table.ObjectId &&
                    item.ObjectType.Equals("table", StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    warnings.Add($"{source.QualifiedSourceName} has no deterministic target identifier and was omitted.");
                    continue;
                }
                target = MarkAutoRecovered(target);
                recoveredMappings.Add(target);
                targetMappings[table.ObjectId] = target;
            }

            var includedColumnNames = tableOverride?.IncludedColumns?.ToHashSet(
                StringComparer.OrdinalIgnoreCase);
            var targetTableSql = request.Conversion.Artifacts
                .LastOrDefault(item =>
                    item.SourceObjectId == table.ObjectId &&
                    item.DeploymentPhase == DeploymentPhase.Tables)
                ?.PostgreSqlDefinition ?? string.Empty;
            var columns = request.Inventory.Columns
                .Where(item => item.ParentObjectId == table.ObjectId)
                .OrderBy(item => item.OrdinalPosition)
                .Select(item =>
                {
                    var targetColumnName = ResolveColumnTarget(
                        item,
                        table.ObjectId,
                        source,
                        columnTargets,
                        columnTargetsByKey,
                        canonicalMappings,
                        recoveredMappings,
                        caseSensitive,
                        request.Conversion.MappingSet);
                    return CreateColumn(
                        item,
                        targetColumnName,
                        includedColumnNames,
                        options,
                        TargetColumnIsGenerated(targetTableSql, targetColumnName));
                })
                .ToArray();
            var manual = columns.FirstOrDefault(item =>
                item.GeneratedStrategy == GeneratedColumnLoadStrategy.ManualMigration ||
                item.EncryptionStrategy == EncryptedColumnStrategy.ManualMigration);
            var keys = primaryKeys.GetValueOrDefault(table.ObjectId, []);
            var stableKey = tableOverride?.StableResumeKey ??
                (keys.Count == 1 ? keys[0] : null);
            var isResumable = stableKey is not null &&
                columns.Any(item => item.SourceName.Equals(stableKey, StringComparison.OrdinalIgnoreCase));
            if (!isResumable)
            {
                warnings.Add(
                    $"{source.QualifiedSourceName} has no single stable resume key; interrupted loads require a table restart.");
            }

            var targetPreparation = tableOverride?.TargetPreparation ?? options.TargetPreparation;
            var strategy = targetPreparation == TargetPreparationStrategy.Upsert
                ? DataTransferStrategy.ParameterizedBatchInsert
                : tableOverride?.TransferStrategy ?? SelectDefaultStrategy(columns);
            var dependencyOrder = Array.IndexOf(defaultOrder, table.ObjectId);
            var order = tableOverride?.AdministratorOrder ??
                checked(waveByTable.GetValueOrDefault(table.ObjectId, 99) * 1_000_000 +
                        Math.Max(dependencyOrder, 0));
            plans.Add(new TableLoadPlan(
                table.ObjectId,
                source.SourceSchema,
                source.SourceName,
                Unquote(target.TargetSchema),
                Unquote(target.TargetName),
                table.RowCountEstimate,
                columns,
                keys,
                stableKey,
                NormalizePredicate(tableOverride?.SourcePredicate),
                strategy,
                targetPreparation,
                isResumable,
                isResumable && IsPartitionable(columns, stableKey!),
                order < 0 ? int.MaxValue : order,
                FindDependencyGroup(request.Inventory, table.ObjectId),
                columns.Any(item => item.IsSensitive),
                manual is not null && options.ManualColumnPolicy == ManualColumnPolicy.StopTable,
                manual is null ? null : $"Column {manual.SourceName} requires manual migration.",
                source.MetadataHash));
        }

        var sourceIdentity = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{request.Inventory.Database.DatabaseName}:{request.Inventory.Database.DatabaseId}:{request.Inventory.Database.ProductVersion}");
        var targetIdentity = SafeTargetIdentity(request.TargetConnectionString);
        var metadataHash = Hash(string.Join(
            "\n",
            plans.OrderBy(item => item.SourceQualifiedName, StringComparer.Ordinal)
                .Select(item => item.MetadataHash)));
        var configurationHash = Hash(JsonSerializer.Serialize(options));
        var runId = request.ResumeRunId ?? Guid.NewGuid();

        return new DataMigrationPlan(
            runId,
            DateTimeOffset.UtcNow,
            sourceIdentity,
            targetIdentity,
            metadataHash,
            configurationHash,
            options,
            plans.OrderBy(item => item.LoadOrder)
                .ThenBy(item => item.SourceQualifiedName, StringComparer.Ordinal)
                .ToArray(),
            warnings,
            typeof(DataMigrationPlanner).Assembly.GetName().Version?.ToString() ?? "0.0.0")
        {
            RecoveredIdentifierMappings = recoveredMappings
                .OrderBy(item => item.SourceQualifiedName, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private string ResolveColumnTarget(
        ColumnInventory column,
        InventoryObjectId tableId,
        InventoryObject table,
        Dictionary<string, string> columnTargets,
        Dictionary<ColumnIdentifierKey, IdentifierMappingEntry> columnTargetsByKey,
        IReadOnlyList<IdentifierMappingEntry> canonicalMappings,
        List<IdentifierMappingEntry> recoveredMappings,
        bool caseSensitive,
        IdentifierMappingSetMetadata mappingSet)
    {
        var canonicalKey = new ColumnIdentifierKey(tableId, column.ColumnId);
        if (columnTargetsByKey.TryGetValue(canonicalKey, out var keyedMapping))
        {
            LogDiagnosticLookup(
                "Lookup",
                column,
                table,
                canonicalKey,
                keyedMapping,
                mappingSet,
                true);
            return Unquote(keyedMapping.TargetName);
        }

        var lookup = ColumnLookupKey(tableId, column.Name, caseSensitive);
        if (columnTargets.TryGetValue(lookup, out var existing))
        {
            LogDiagnosticLookup(
                "LegacyNameFallback",
                column,
                table,
                canonicalKey,
                null,
                mappingSet,
                true,
                existing);
            return existing;
        }

        var recovered = canonicalMappings.LastOrDefault(item =>
            item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase) &&
            item.SourceKey.ParentObjectId == tableId &&
            item.SourceKey.ObjectId == column.ObjectId);
        if (recovered is null)
        {
            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            recovered = canonicalMappings.LastOrDefault(item =>
                item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase) &&
                item.SourceObjectId == tableId &&
                item.SourceName.Equals(column.Name, comparison));
        }
        if (recovered is null)
        {
            LogDiagnosticLookup(
                "Missing",
                column,
                table,
                canonicalKey,
                null,
                mappingSet,
                false);
            throw new InvalidOperationException(
                $"Identifier '{table.QualifiedSourceName}.{column.Name}' cannot be mapped deterministically.");
        }

        recovered = MarkAutoRecovered(recovered);
        recoveredMappings.Add(recovered);
        columnTargetsByKey[canonicalKey] = recovered;
        var target = Unquote(recovered.TargetName);
        columnTargets[lookup] = target;
        LogDiagnosticLookup(
            "AutoRecovered",
            column,
            table,
            canonicalKey,
            recovered,
            mappingSet,
            true,
            target);
        return target;
    }

    private void LogDiagnosticLookup(
        string action,
        ColumnInventory column,
        InventoryObject table,
        ColumnIdentifierKey consumerKey,
        IdentifierMappingEntry? mapping,
        IdentifierMappingSetMetadata mappingSet,
        bool exists,
        string? targetOverride = null)
    {
        if (logger is null ||
            !table.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) ||
            !table.SourceName.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase) ||
            !column.Name.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var details =
                $"PreviewPlan{action}; ObjectId={column.ObjectId}; ParentTableObjectId={table.Id}; " +
                $"ColumnId={column.ColumnId}; Schema={table.SourceSchema}; Table={table.SourceName}; " +
                $"Column={column.Name}; ConsumerKey={consumerKey}; " +
                $"ProducerKey={mapping?.SourceKey.ColumnKey?.ToString() ?? string.Empty}; " +
                $"TargetIdentifier={targetOverride ?? mapping?.TargetName ?? string.Empty}; " +
                $"MappingSetId={mappingSet.MappingSetId}; MappingVersion={mappingSet.SchemaVersion}; " +
                $"Exists={exists}; Included={mapping?.IncludedInScope ?? table.IsIncluded}; " +
                $"LoadedFromCache={mappingSet.LoadedFromCache}";
            LogIdentifierLifecycle(logger, details);
        }
    }

    [LoggerMessage(EventId = 4105, Level = LogLevel.Information, Message = "Identifier lifecycle {Details}")]
    private static partial void LogIdentifierLifecycle(ILogger logger, string details);

    private static IdentifierMappingEntry MarkAutoRecovered(
        IdentifierMappingEntry mapping) =>
        mapping with
        {
            AutoRecovered = true,
            MappingAction = IdentifierMappingAction.AutoRecovered,
            MappingReason =
                $"{mapping.MappingReason}; missing active mapping regenerated automatically"
        };

    private static string ColumnLookupKey(
        InventoryObjectId tableId,
        string sourceName,
        bool caseSensitive) =>
        $"{tableId}\u001f{(caseSensitive ? sourceName : sourceName.ToUpperInvariant())}";

    private static bool IsCaseSensitiveCollation(string? collation) =>
        collation?.Contains("_CS_", StringComparison.OrdinalIgnoreCase) == true ||
        collation?.Contains("_BIN", StringComparison.OrdinalIgnoreCase) == true;

    private ColumnMapping CreateColumn(
        ColumnInventory column,
        string targetName,
        HashSet<string>? includedColumnNames,
        DataMigrationOptions options,
        bool targetColumnIsGenerated)
    {
        var included = includedColumnNames is null || includedColumnNames.Contains(column.Name);
        var generated = column.GeneratedAlwaysType != 0 ||
            column.IsComputed && targetColumnIsGenerated
            ? GeneratedColumnLoadStrategy.ExcludeGenerated
            : GeneratedColumnLoadStrategy.PopulateFromSource;
        EncryptedColumnStrategy? encryption = column.EncryptionType is null
            ? null
            : EncryptedColumnStrategy.CopyCiphertextAsOpaqueData;
        return new ColumnMapping(
            column.OrdinalPosition,
            column.Name,
            targetName,
            column.SystemTypeName,
            MapTargetType(column),
            ClassifyTransport(column.SystemTypeName),
            column.IsNullable,
            sensitiveClassifier.IsSensitive(column, options.SensitiveData),
            column.IsIdentity,
            column.IdentitySeed,
            column.IdentityIncrement,
            generated,
            encryption,
            included && generated != GeneratedColumnLoadStrategy.ExcludeGenerated,
            null);
    }

    private static bool TargetColumnIsGenerated(string tableSql, string targetColumnName)
    {
        if (string.IsNullOrWhiteSpace(tableSql))
        {
            return false;
        }

        var quotedName = MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter
            .Quote(MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Unquote(
                targetColumnName));
        var start = tableSql.IndexOf(quotedName, StringComparison.Ordinal);
        if (start < 0)
        {
            start = tableSql.IndexOf(targetColumnName, StringComparison.Ordinal);
        }
        if (start < 0)
        {
            return false;
        }

        var end = tableSql.IndexOfAny([',', '\r', '\n'], start);
        var definition = end < 0 ? tableSql[start..] : tableSql[start..end];
        return definition.Contains("GENERATED ALWAYS AS", StringComparison.OrdinalIgnoreCase);
    }

    private static DataTransferStrategy SelectDefaultStrategy(IEnumerable<ColumnMapping> columns)
    {
        var included = columns.Where(item => item.IsIncluded).ToArray();
        return included.All(item =>
                item.TransportKind is not DataTransportKind.Spatial and not DataTransportKind.Opaque)
            ? DataTransferStrategy.PostgreSqlBinaryCopy
            : DataTransferStrategy.ParameterizedBatchInsert;
    }

    private static bool IsPartitionable(IEnumerable<ColumnMapping> columns, string key) =>
        columns.Any(item =>
            item.SourceName.Equals(key, StringComparison.OrdinalIgnoreCase) &&
            item.TransportKind is DataTransportKind.Signed16 or DataTransportKind.Signed32
                or DataTransportKind.Signed64 or DataTransportKind.Uuid);

    private static string? NormalizePredicate(string? predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate))
        {
            return null;
        }

        if (predicate.Contains(';', StringComparison.Ordinal) ||
            predicate.Contains("--", StringComparison.Ordinal) ||
            predicate.Contains("/*", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Administrator predicates cannot contain statement terminators or comments.");
        }

        return predicate.Trim();
    }

    private static Dictionary<InventoryObjectId, HashSet<InventoryObjectId>> BuildTableDependencies(
        InventorySnapshot inventory)
    {
        var tableIds = inventory.Tables.Select(item => item.ObjectId).ToHashSet();
        var result = tableIds.ToDictionary(item => item, _ => new HashSet<InventoryObjectId>());
        foreach (var dependency in inventory.Dependencies.Where(item =>
                     item.TargetObjectId is not null &&
                     tableIds.Contains(item.SourceObjectId) &&
                     tableIds.Contains(item.TargetObjectId.Value)))
        {
            result[dependency.SourceObjectId].Add(dependency.TargetObjectId!.Value);
        }

        foreach (var foreignKey in inventory.Constraints.Where(item =>
                     item.Kind == ConstraintKind.ForeignKey &&
                     item.ReferencedTableObjectId is not null &&
                     tableIds.Contains(item.TableObjectId) &&
                     tableIds.Contains(item.ReferencedTableObjectId.Value)))
        {
            result[foreignKey.TableObjectId].Add(foreignKey.ReferencedTableObjectId!.Value);
        }

        return result;
    }

    private static InventoryObjectId[] TopologicalOrder(
        IEnumerable<InventoryObjectId> ids,
        IReadOnlyDictionary<InventoryObjectId, HashSet<InventoryObjectId>> dependencies)
    {
        var nodes = ids.Distinct().OrderBy(item => item.Value).ToArray();
        var nodeSet = nodes.ToHashSet();
        var dependencyCount = nodes.ToDictionary(
            item => item,
            item => dependencies.GetValueOrDefault(item, [])
                .Count(nodeSet.Contains));
        var dependents = nodes.ToDictionary(item => item, _ => new List<InventoryObjectId>());
        foreach (var (node, required) in dependencies)
        {
            if (!nodeSet.Contains(node))
            {
                continue;
            }

            foreach (var dependency in required.Where(nodeSet.Contains))
            {
                dependents[dependency].Add(node);
            }
        }

        var ready = new SortedSet<InventoryObjectId>(
            dependencyCount.Where(item => item.Value == 0).Select(item => item.Key),
            Comparer<InventoryObjectId>.Create((left, right) => left.Value.CompareTo(right.Value)));
        var remaining = nodeSet;
        var result = new List<InventoryObjectId>(nodes.Length);
        while (remaining.Count > 0)
        {
            if (ready.Count == 0)
            {
                // Break a dependency cycle deterministically. The SCC is retained on the plan so
                // constraints can still be deferred until after its table group is loaded.
                ready.Add(remaining.MinBy(item => item.Value));
            }

            var item = ready.Min;
            ready.Remove(item);
            if (!remaining.Remove(item))
            {
                continue;
            }

            result.Add(item);
            foreach (var dependent in dependents[item])
            {
                dependencyCount[dependent]--;
                if (dependencyCount[dependent] == 0 && remaining.Contains(dependent))
                {
                    ready.Add(dependent);
                }
            }
        }

        return result.ToArray();
    }

    private static int FindDependencyGroup(InventorySnapshot inventory, InventoryObjectId tableId) =>
        inventory.DependencyComponents.FirstOrDefault(item => item.Members.Contains(tableId))?.Id ?? -1;

    private static string SafeTargetIdentity(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{builder.Host}:{builder.Port}:{builder.Database}";
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Unquote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Unquote(identifier);

    private static DataTransportKind ClassifyTransport(string type) =>
        type.ToLowerInvariant() switch
        {
            "bit" => DataTransportKind.Boolean,
            "tinyint" or "smallint" => DataTransportKind.Signed16,
            "int" => DataTransportKind.Signed32,
            "bigint" => DataTransportKind.Signed64,
            "decimal" or "numeric" or "money" or "smallmoney" => DataTransportKind.ExactNumeric,
            "real" => DataTransportKind.Floating32,
            "float" => DataTransportKind.Floating64,
            "date" => DataTransportKind.Date,
            "time" => DataTransportKind.Time,
            "datetime" or "datetime2" or "smalldatetime" => DataTransportKind.DateTime,
            "datetimeoffset" => DataTransportKind.DateTimeOffset,
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => DataTransportKind.Binary,
            "uniqueidentifier" => DataTransportKind.Uuid,
            "xml" => DataTransportKind.Xml,
            "geometry" or "geography" => DataTransportKind.Spatial,
            "char" or "varchar" or "text" or "nchar" or "nvarchar" or "ntext" =>
                DataTransportKind.Text,
            _ => DataTransportKind.Opaque
        };

    private static string MapTargetType(ColumnInventory column) =>
        ClassifyTransport(column.SystemTypeName) switch
        {
            DataTransportKind.Boolean => "boolean",
            DataTransportKind.Signed16 => "smallint",
            DataTransportKind.Signed32 => "integer",
            DataTransportKind.Signed64 => "bigint",
            DataTransportKind.ExactNumeric => $"numeric({column.Precision},{column.Scale})",
            DataTransportKind.Floating32 => "real",
            DataTransportKind.Floating64 => "double precision",
            DataTransportKind.Date => "date",
            DataTransportKind.Time => "time",
            DataTransportKind.DateTime => "timestamp",
            DataTransportKind.DateTimeOffset => "timestamp with time zone",
            DataTransportKind.Binary => "bytea",
            DataTransportKind.Uuid => "uuid",
            DataTransportKind.Xml => "xml",
            DataTransportKind.Json => "jsonb",
            DataTransportKind.Spatial => "geometry",
            _ => "text"
        };
}
