using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion;

public sealed partial class PostgreSqlIdentifierMappingService(
    ILogger<PostgreSqlIdentifierMappingService>? logger = null) : IIdentifierMappingService
{
    public IIdentifierMapper CreateMapper(InventorySnapshot inventory, ConversionOptions options)
        => CreateMapper(inventory, options, CancellationToken.None, null);

    public IIdentifierMapper CreateMapper(
        InventorySnapshot inventory,
        ConversionOptions options,
        CancellationToken cancellationToken,
        IProgress<ConversionProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);
        options.TargetVersion.Validate();
        return new Mapper(inventory, options, logger, cancellationToken, progress);
    }

    private sealed partial class Mapper : IIdentifierMapper
    {
        private const int MaximumBytes = 63;
        private readonly ConversionOptions _options;
        private readonly Dictionary<InventoryObjectId, TargetObjectIdentifier> _objects = [];
        private readonly Dictionary<string, string> _schemas;
        private readonly Dictionary<string, Allocation> _schemaAllocations;
        private readonly Dictionary<string, string> _allocated = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _children;
        private readonly Dictionary<string, int> _childMappingIndexes;
        private readonly Dictionary<InventoryObjectId, InventoryObject> _sources;
        private readonly Dictionary<string, InventoryObjectId> _schemaIds;
        private readonly List<IdentifierMappingEntry> _mappings = [];
        private readonly StringComparer _sourceNameComparer;
        private readonly ILogger<PostgreSqlIdentifierMappingService>? _logger;
        private readonly CancellationToken _cancellationToken;
        private readonly MappingLiveness _liveness;

        public Mapper(
            InventorySnapshot inventory,
            ConversionOptions options,
            ILogger<PostgreSqlIdentifierMappingService>? logger,
            CancellationToken cancellationToken,
            IProgress<ConversionProgress>? progress)
        {
            _options = options;
            _logger = logger;
            _cancellationToken = cancellationToken;
            _liveness = new MappingLiveness(
                CalculateMappingWorkTotal(inventory),
                cancellationToken,
                progress);
            _sourceNameComparer = IsCaseSensitiveCollation(inventory.Database.Collation)
                ? StringComparer.Ordinal
                : StringComparer.OrdinalIgnoreCase;
            _schemas = new Dictionary<string, string>(_sourceNameComparer);
            _schemaAllocations = new Dictionary<string, Allocation>(_sourceNameComparer);
            _children = new Dictionary<string, string>(_sourceNameComparer);
            _childMappingIndexes = new Dictionary<string, int>(_sourceNameComparer);
            _sources = inventory.Objects.ToDictionary(item => item.Id);
            _schemaIds = inventory.Schemas
                .Select(item => item.InventoryObject)
                .GroupBy(item => item.SourceName, _sourceNameComparer)
                .ToDictionary(group => group.Key, group => group.First().Id, _sourceNameComparer);
            var effectiveObjectTypes = BuildEffectiveObjectTypes(inventory);
            foreach (var schema in inventory.Schemas
                         .OrderBy(item => item.InventoryObject.SourceName, StringComparer.Ordinal)
                         .Select(item => item.InventoryObject.SourceName)
                         .Distinct(_sourceNameComparer))
            {
                _liveness.Tick("Schema");
                var requested = ResolveSchema(schema);
                var allocation = Allocate(
                    "schema",
                    Normalize(requested),
                    SchemaStableIdentity(inventory.Database.DatabaseName, schema, requested));
                _schemas[schema] = allocation.Name;
                _schemaAllocations[schema] = allocation;
            }

            foreach (var source in inventory.Objects.OrderBy(item => item.Id.Value))
            {
                _liveness.Tick(source.ObjectType.ToString());
                if (source.ObjectType == InventoryObjectType.Schema)
                {
                    var schemaAllocation = GetSchemaAllocation(source.SourceName);
                    var quotedSchema = QuoteIdentifier(schemaAllocation.Name);
                    _objects[source.Id] = new TargetObjectIdentifier(
                        source.ObjectType.ToString(),
                        quotedSchema,
                        quotedSchema);
                    _mappings.Add(CreateMapping(
                        source.Id,
                        source.ObjectType.ToString(),
                        source.SourceDatabase,
                        string.Empty,
                        string.Empty,
                        source.SourceName,
                        source.QualifiedSourceName,
                        schemaAllocation.Name,
                        schemaAllocation,
                        Normalize(ResolveSchema(source.SourceName)),
                        new SourceIdentifierKey(
                            source.SourceDatabase,
                            string.Empty,
                            string.Empty,
                            source.SourceName,
                            InventoryObjectType.Schema.ToString(),
                            null,
                            source.Id)
                        {
                            SourceSchemaId = source.Id
                        },
                        source.IsIncluded,
                        source.ConversionClassification));
                    continue;
                }

                var effectiveType = EffectiveObjectType(source, effectiveObjectTypes);
                if (IsFacetOwnedType(effectiveType) && source.ParentObjectId is not null)
                {
                    continue;
                }
                var targetSchema = MapSchema(source.SourceSchema);
                var normalized = Normalize(source.SourceName);
                var stableIdentity =
                    $"{source.SourceDatabase}|{effectiveType}|{source.SourceSchema}|{source.SourceName}|{source.Id}";
                var allocation = Allocate(
                    ObjectAllocationScope(targetSchema, effectiveType),
                    normalized,
                    stableIdentity);
                var target = new TargetObjectIdentifier(
                    effectiveType.ToString(),
                    QuoteIdentifier(targetSchema),
                    QuoteIdentifier(allocation.Name));
                _objects[source.Id] = target;
                _mappings.Add(CreateMapping(
                    source.Id,
                    effectiveType.ToString(),
                    source.SourceDatabase,
                    string.Empty,
                    source.SourceSchema,
                    source.SourceName,
                    source.QualifiedSourceName,
                    targetSchema,
                    allocation,
                    normalized,
                    new SourceIdentifierKey(
                        source.SourceDatabase,
                        source.SourceSchema,
                        string.Empty,
                        source.SourceName,
                        effectiveType.ToString(),
                        source.ParentObjectId,
                        source.Id)
                    {
                        SourceSchemaId = GetSchemaId(source.SourceSchema)
                    },
                    source.IsIncluded,
                    source.ConversionClassification));
            }

            RegisterIncludedChildMappings(inventory);
            RegisterMissingFacetMappings(inventory);
            _liveness.Complete();
        }

        private static int CalculateMappingWorkTotal(InventorySnapshot inventory)
        {
            var sourceIds = inventory.Objects.Select(item => item.Id).ToHashSet();
            var ownerIds = inventory.Objects
                .Where(item => !item.IsSystemObject)
                .Select(item => item.Id)
                .ToHashSet();
            var schemaCount = inventory.Schemas
                .Select(item => item.InventoryObject.SourceName)
                .Distinct(
                    IsCaseSensitiveCollation(inventory.Database.Collation)
                        ? StringComparer.Ordinal
                        : StringComparer.OrdinalIgnoreCase)
                .Count();
            var columnCount = inventory.Columns.Count(item => ownerIds.Contains(item.ParentObjectId));
            var constraintCount =
                inventory.Constraints.Count(item => ownerIds.Contains(item.TableObjectId));
            var indexCount = inventory.Indexes.Count(item => ownerIds.Contains(item.TableObjectId));
            var moduleCount = inventory.Modules
                .Where(item => ownerIds.Contains(item.ObjectId))
                .Sum(item => 1 + item.Parameters.Count + item.ResultColumns.Count);
            var typeCount = inventory.UserDefinedTypes
                .Where(item => ownerIds.Contains(item.ObjectId))
                .Sum(item => 1 + item.TableTypeColumns.Count);
            var triggerCount = inventory.Triggers.Count(item =>
                item.ParentObjectId is { } parent &&
                ownerIds.Contains(parent) &&
                sourceIds.Contains(item.ObjectId));
            var missingFacetPassCount = inventory.Objects.Count(item =>
                IsFacetOwnedType(item.ObjectType) && item.ParentObjectId is not null);

            return Math.Max(
                1,
                checked(
                    schemaCount + inventory.Objects.Count + columnCount + constraintCount +
                    indexCount + moduleCount + typeCount + triggerCount +
                    missingFacetPassCount));
        }

        public IReadOnlyList<IdentifierMappingEntry> Mappings => _mappings;

        public Guid MappingSetId { get; } = Guid.NewGuid();

        public int SchemaVersion => IdentifierMappingSchema.CurrentVersion;

        public bool LoadedFromCache => false;

        public string MapSchema(string sourceSchema)
        {
            if (_schemas.TryGetValue(sourceSchema, out var allocated))
            {
                return allocated;
            }

            var requested = ResolveSchema(sourceSchema);
            var allocation = Allocate(
                "schema",
                Normalize(requested),
                SchemaStableIdentity(string.Empty, sourceSchema, requested));
            _schemas[sourceSchema] = allocation.Name;
            _schemaAllocations[sourceSchema] = allocation;
            return allocation.Name;
        }

        private Allocation GetSchemaAllocation(string sourceSchema)
        {
            _ = MapSchema(sourceSchema);
            return _schemaAllocations[sourceSchema];
        }

        public TargetObjectIdentifier MapObject(InventoryObject source) =>
            _objects.TryGetValue(source.Id, out var target)
                ? target
                : throw new KeyNotFoundException(
                    $"No identifier mapping exists for {source.QualifiedSourceName}.");

        public string MapChildIdentifier(
            InventoryObjectId ownerId,
            string objectType,
            string sourceSchema,
            string sourceName) =>
            MapChildIdentifier(
                ownerId,
                objectType,
                sourceSchema,
                sourceName,
                null,
                true,
                ConversionClassification.Automatic,
                null,
                false);

        private string MapChildIdentifier(
            InventoryObjectId ownerId,
            string objectType,
            string sourceSchema,
            string sourceName,
            InventoryObjectId? objectId,
            bool included,
            ConversionClassification classification,
            int? columnId = null,
            bool autoRecovered = false)
        {
            var normalizedType = objectType.Trim().ToLowerInvariant();
            var childKey = $"{ownerId}\u001f{normalizedType}\u001f{sourceName}";
            if (_children.TryGetValue(childKey, out var existing))
            {
                var existingIndex = _childMappingIndexes.GetValueOrDefault(childKey, -1);
                var existingMapping =
                    existingIndex >= 0 ? _mappings[existingIndex] : null;
                if (objectId is { } authoritativeObjectId &&
                    existingMapping is not null &&
                    existingMapping.SourceKey.ObjectId is null)
                {
                    var upgradedKey = existingMapping.SourceKey with
                    {
                        ObjectId = authoritativeObjectId,
                        ColumnId = columnId,
                        SourceSchemaId = GetSchemaId(sourceSchema)
                    };
                    var upgraded = existingMapping with
                    {
                        SourceObjectId = authoritativeObjectId,
                        SourceKey = upgradedKey,
                        IncludedInScope = included,
                        ConversionClassification = classification,
                        AutoRecovered = autoRecovered,
                        MappingAction = autoRecovered
                            ? IdentifierMappingAction.AutoRecovered
                            : existingMapping.MappingAction,
                        MappingReason = autoRecovered
                            ? $"AutoRecovered during complete pre-conversion mapping; {existingMapping.MappingReason}"
                            : existingMapping.MappingReason
                    };
                    _mappings[existingIndex] = upgraded;
                    _objects[authoritativeObjectId] = new TargetObjectIdentifier(
                        normalizedType,
                        QuoteIdentifier(MapSchema(sourceSchema)),
                        QuoteIdentifier(existing));
                    LogTargetMappingMutation(
                        "Replace",
                        sourceSchema,
                        _sources.GetValueOrDefault(ownerId)?.SourceName ?? string.Empty,
                        sourceName,
                        authoritativeObjectId,
                        ownerId,
                        columnId,
                        upgradedKey.ColumnKey?.ToString() ??
                        upgradedKey.TriggerKey?.ToString() ??
                        upgradedKey.ToString(),
                        existing,
                        MappingSetId,
                        SchemaVersion,
                        included,
                        LoadedFromCache);
                    return QuoteIdentifier(existing);
                }
                if (objectId is { } distinctObjectId &&
                    existingMapping?.SourceKey.ObjectId is { } existingObjectId &&
                    existingObjectId != distinctObjectId)
                {
                    childKey = $"{childKey}\u001f{distinctObjectId}";
                }
                else
                {
                LogTargetMappingMutation(
                    "Reuse",
                    sourceSchema,
                    _sources.GetValueOrDefault(ownerId)?.SourceName ?? string.Empty,
                    sourceName,
                    existingMapping?.SourceKey.ObjectId,
                    ownerId,
                    existingMapping?.SourceKey.ColumnId,
                    existingMapping?.SourceKey.ColumnKey?.ToString() ??
                    existingMapping?.SourceKey.TriggerKey?.ToString() ??
                    existingMapping?.SourceKey.ToString() ?? string.Empty,
                    existing,
                    MappingSetId,
                    SchemaVersion,
                    existingMapping?.IncludedInScope ?? false,
                    LoadedFromCache);
                return QuoteIdentifier(existing);
                }
            }

            var schema = MapSchema(sourceSchema);
            var normalized = Normalize(sourceName);
            _sources.TryGetValue(ownerId, out var owner);
            var parent = owner?.QualifiedSourceName ?? ownerId.ToString();
            var sourceDatabase = owner?.SourceDatabase ?? string.Empty;
            var stableIdentity =
                $"{sourceDatabase}|{normalizedType}|{parent}|{sourceName}|{ownerId}|{objectId}";
            var allocation = Allocate(
                ChildAllocationScope(ownerId, schema, normalizedType),
                normalized,
                stableIdentity);
            _children[childKey] = allocation.Name;
            if (objectId is { } childObjectId)
            {
                _objects[childObjectId] = new TargetObjectIdentifier(
                    normalizedType,
                    QuoteIdentifier(schema),
                    QuoteIdentifier(allocation.Name));
            }
            var sourceKey = new SourceIdentifierKey(
                sourceDatabase,
                sourceSchema,
                parent,
                sourceName,
                normalizedType,
                ownerId,
                objectId)
            {
                ColumnId = columnId,
                SourceSchemaId = GetSchemaId(sourceSchema)
            };
            var baseMapping = CreateMapping(
                objectId ?? ownerId,
                normalizedType,
                sourceDatabase,
                parent,
                sourceSchema,
                sourceName,
                $"{parent}.{sourceName}",
                schema,
                allocation,
                normalized,
                sourceKey,
                included,
                classification);
            var mapping = baseMapping with
            {
                TargetParentObject = _objects.TryGetValue(ownerId, out var parentTarget)
                    ? parentTarget.QualifiedName
                    : string.Empty,
                AutoRecovered = autoRecovered,
                MappingAction = autoRecovered
                    ? IdentifierMappingAction.AutoRecovered
                    : baseMapping.MappingAction,
                MappingReason = autoRecovered
                    ? $"AutoRecovered during complete pre-conversion mapping; {baseMapping.MappingReason}"
                    : baseMapping.MappingReason
            };
            _mappings.Add(mapping);
            _childMappingIndexes[childKey] = _mappings.Count - 1;
            LogTargetMappingMutation(
                "Add",
                sourceSchema,
                owner?.SourceName ?? string.Empty,
                sourceName,
                objectId,
                ownerId,
                columnId,
                sourceKey.ColumnKey?.ToString() ??
                sourceKey.TriggerKey?.ToString() ??
                sourceKey.ToString(),
                mapping.TargetName,
                MappingSetId,
                SchemaVersion,
                included,
                LoadedFromCache);
            return QuoteIdentifier(allocation.Name);
        }

        public string QuoteIdentifier(string identifier)
        {
            if (identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"')
            {
                return identifier;
            }

            return RequiresQuoting(identifier)
                ? PostgreSqlIdentifierQuoter.Quote(identifier)
                : identifier;
        }

        private void RegisterIncludedChildMappings(InventorySnapshot inventory)
        {
            var owners = _sources.Values
                .Where(item => !item.IsSystemObject)
                .ToDictionary(item => item.Id);

            foreach (var column in inventory.Columns
                         .Where(item => owners.ContainsKey(item.ParentObjectId))
                         .OrderBy(item => item.ParentObjectId.Value)
                         .ThenBy(item => item.OrdinalPosition)
                         .ThenBy(item => item.ObjectId.Value))
            {
                _liveness.Tick("Column");
                var owner = owners[column.ParentObjectId];
                var included = IsIncludedChild(owner, column.ObjectId);
                _ = MapChildIdentifier(
                    owner.Id,
                    "column",
                    owner.SourceSchema,
                    column.Name,
                    column.ObjectId,
                    included,
                    ConversionClassification.Automatic,
                    column.ColumnId);
                if (!string.IsNullOrWhiteSpace(column.DefaultConstraintName))
                {
                    _ = MapChildIdentifier(
                        owner.Id,
                        "constraint",
                        owner.SourceSchema,
                        column.DefaultConstraintName,
                        null,
                        included,
                        ConversionClassification.Automatic);
                }
            }

            foreach (var constraint in inventory.Constraints
                         .Where(item => owners.ContainsKey(item.TableObjectId))
                         .OrderBy(item => item.TableObjectId.Value)
                         .ThenBy(item => item.ObjectId.Value))
            {
                _liveness.Tick("Constraint");
                var owner = owners[constraint.TableObjectId];
                _ = MapChildIdentifier(
                    owner.Id,
                    "constraint",
                    owner.SourceSchema,
                    constraint.Name,
                    constraint.ObjectId,
                    IsIncludedChild(owner, constraint.ObjectId),
                    ConversionClassification.Automatic);
            }

            foreach (var index in inventory.Indexes
                         .Where(item => owners.ContainsKey(item.TableObjectId))
                         .OrderBy(item => item.TableObjectId.Value)
                         .ThenBy(item => item.IndexId)
                         .ThenBy(item => item.ObjectId.Value))
            {
                _liveness.Tick("Index");
                var owner = owners[index.TableObjectId];
                _ = MapChildIdentifier(
                    owner.Id,
                    "index",
                    owner.SourceSchema,
                    index.Name,
                    index.ObjectId,
                    IsIncludedChild(owner, index.ObjectId),
                    index.Classification);
            }

            foreach (var module in inventory.Modules
                         .Where(item => owners.ContainsKey(item.ObjectId))
                         .OrderBy(item => item.ObjectId.Value))
            {
                _liveness.Tick(module.Kind.ToString());
                var owner = owners[module.ObjectId];
                foreach (var parameter in module.Parameters.OrderBy(item => item.ParameterId))
                {
                    _liveness.Tick("Parameter");
                    _ = MapChildIdentifier(
                        owner.Id,
                        "parameter",
                        owner.SourceSchema,
                        parameter.Name.TrimStart('@'),
                        null,
                        owner.IsIncluded,
                        ConversionClassification.Automatic);
                }
                foreach (var field in module.ResultColumns.OrderBy(item => item.OrdinalPosition))
                {
                    _liveness.Tick("ResultColumn");
                    _ = MapChildIdentifier(
                        owner.Id,
                        "field",
                        owner.SourceSchema,
                        field.Name,
                        field.ObjectId,
                        owner.IsIncluded,
                        ConversionClassification.Automatic);
                }
            }

            foreach (var type in inventory.UserDefinedTypes
                         .Where(item => owners.ContainsKey(item.ObjectId))
                         .OrderBy(item => item.ObjectId.Value))
            {
                _liveness.Tick("UserDefinedType");
                var owner = owners[type.ObjectId];
                foreach (var field in type.TableTypeColumns.OrderBy(item => item.OrdinalPosition))
                {
                    _liveness.Tick("TableTypeColumn");
                    _ = MapChildIdentifier(
                        owner.Id,
                        "field",
                        owner.SourceSchema,
                        field.Name,
                        field.ObjectId,
                        owner.IsIncluded,
                        ConversionClassification.Automatic);
                }
            }

            foreach (var trigger in inventory.Triggers
                         .Where(item => item.ParentObjectId is { } parent &&
                             owners.ContainsKey(parent) &&
                             _sources.ContainsKey(item.ObjectId))
                         .OrderBy(item => item.ParentObjectId!.Value.Value)
                         .ThenBy(item => item.ObjectId.Value))
            {
                _liveness.Tick("Trigger");
                var owner = owners[trigger.ParentObjectId!.Value];
                var source = _sources[trigger.ObjectId];
                _ = MapChildIdentifier(
                    owner.Id,
                    "trigger",
                    owner.SourceSchema,
                    source.SourceName,
                    source.Id,
                    IsIncludedChild(owner, source.Id),
                    source.ConversionClassification);
            }
        }

        private void RegisterMissingFacetMappings(InventorySnapshot inventory)
        {
            foreach (var source in inventory.Objects
                         .Where(item => IsFacetOwnedType(item.ObjectType))
                         .Where(item => item.ParentObjectId is not null)
                         .OrderBy(item => FacetOrder(item.ObjectType))
                         .ThenBy(item => item.ParentObjectId!.Value.Value)
                         .ThenBy(item => item.Id.Value))
            {
                _liveness.Tick(source.ObjectType.ToString());
                if (_objects.ContainsKey(source.Id) ||
                    source.ParentObjectId is not { } parentId ||
                    !_sources.TryGetValue(parentId, out var owner))
                {
                    continue;
                }

                _ = MapChildIdentifier(
                    owner.Id,
                    ChildObjectType(source.ObjectType),
                    owner.SourceSchema,
                    source.SourceName,
                    source.Id,
                    IsIncludedChild(owner, source.Id),
                    source.ConversionClassification,
                    null,
                    true);
            }
        }

        private static int FacetOrder(InventoryObjectType objectType) =>
            objectType switch
            {
                InventoryObjectType.Column => 0,
                InventoryObjectType.PrimaryKey or InventoryObjectType.UniqueConstraint or
                    InventoryObjectType.CheckConstraint or InventoryObjectType.ForeignKey or
                    InventoryObjectType.DefaultConstraint => 1,
                InventoryObjectType.Index => 2,
                InventoryObjectType.Trigger => 3,
                _ => 4
            };

        private static string ChildObjectType(InventoryObjectType objectType) =>
            objectType switch
            {
                InventoryObjectType.Column => "column",
                InventoryObjectType.Index => "index",
                InventoryObjectType.Trigger => "trigger",
                InventoryObjectType.PrimaryKey or InventoryObjectType.UniqueConstraint or
                    InventoryObjectType.CheckConstraint or InventoryObjectType.ForeignKey or
                    InventoryObjectType.DefaultConstraint => "constraint",
                _ => objectType.ToString().ToLowerInvariant()
            };

        private bool IsIncludedChild(InventoryObject owner, InventoryObjectId childId) =>
            owner.IsIncluded &&
            !_options.SchemaMappings.Any(item =>
                item.IsExcluded &&
                _sourceNameComparer.Equals(item.SourceSchema, owner.SourceSchema)) &&
            (!_sources.TryGetValue(childId, out var child) || child.IsIncluded);

        private InventoryObjectId? GetSchemaId(string sourceSchema) =>
            _schemaIds.GetValueOrDefault(sourceSchema);

        private void LogTargetMappingMutation(
            string action,
            string schema,
            string table,
            string column,
            InventoryObjectId? objectId,
            InventoryObjectId parentId,
            int? columnId,
            string canonicalKey,
            string target,
            Guid mappingSetId,
            int mappingVersion,
            bool included,
            bool loadedFromCache)
        {
            var isDiagnosticColumn =
                schema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
                table.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase) &&
                column.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase);
            var isDiagnosticTrigger =
                schema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
                table.Equals("DigiPay_TrainerDetails", StringComparison.OrdinalIgnoreCase) &&
                column.Equals(
                    "TRG_DigiPay_TrainerDetailsHistory_Del",
                    StringComparison.OrdinalIgnoreCase);
            if (_logger is null || (!isDiagnosticColumn && !isDiagnosticTrigger))
            {
                return;
            }

            if (_logger.IsEnabled(LogLevel.Information))
            {
                var details =
                    $"{action}; ObjectId={objectId}; ParentTableObjectId={parentId}; ColumnId={columnId}; " +
                    $"Schema={schema}; Table={table}; Column={column}; CanonicalKey={canonicalKey}; " +
                    $"TargetIdentifier={target}; MappingSetId={mappingSetId}; MappingVersion={mappingVersion}; " +
                    $"Exists=True; Included={included}; LoadedFromCache={loadedFromCache}";
                LogIdentifierLifecycle(_logger, details);
            }
        }

        [LoggerMessage(EventId = 2213, Level = LogLevel.Information, Message = "Identifier lifecycle {Details}")]
        private static partial void LogIdentifierLifecycle(ILogger logger, string details);

        private IdentifierMappingEntry CreateMapping(
            InventoryObjectId sourceObjectId,
            string objectType,
            string sourceDatabase,
            string parentObject,
            string sourceSchema,
            string sourceName,
            string sourceQualifiedName,
            string targetSchema,
            Allocation allocation,
            string normalized,
            SourceIdentifierKey? sourceKey = null,
            bool included = true,
            ConversionClassification classification = ConversionClassification.Automatic)
        {
            var quotedSchema = QuoteIdentifier(targetSchema);
            var quotedName = QuoteIdentifier(allocation.Name);
            var isReserved = PostgreSqlKeywordRegistry.IsRestricted(
                _options.TargetVersion,
                allocation.Name);
            var requiresQuoting = RequiresQuoting(allocation.Name);
            var wasQuoted = quotedName.Length >= 2 && quotedName[0] == '"';
            var caseCandidate = _options.IdentifierCaseMode is IdentifierCaseMode.LowercaseUnquoted
                    or IdentifierCaseMode.QuoteOnlyWhenRequired
                ? sourceName.ToLowerInvariant()
                : sourceName;
            var wasCaseNormalized = !string.Equals(
                sourceName,
                caseCandidate,
                StringComparison.Ordinal);
            var invalidCharacterReplacement = !string.Equals(
                caseCandidate,
                normalized,
                StringComparison.Ordinal);
            var wasNormalized = wasCaseNormalized || invalidCharacterReplacement;
            var wasShortened = Encoding.UTF8.GetByteCount(normalized) > MaximumBytes;
            var status = allocation.CollisionResolved
                ? IdentifierMappingStatus.CollisionResolved
                : wasShortened
                    ? IdentifierMappingStatus.AutomaticallyShortened
                    : isReserved
                        ? IdentifierMappingStatus.ReservedWordSafelyQuoted
                        : IdentifierMappingStatus.Safe;
            var severity = status is IdentifierMappingStatus.AutomaticallyShortened
                or IdentifierMappingStatus.CollisionResolved
                ? IdentifierMappingSeverity.Warning
                : IdentifierMappingSeverity.Information;
            var reasons = new List<string>();
            if (wasNormalized)
            {
                if (wasCaseNormalized)
                {
                    reasons.Add("case normalized");
                }
                if (invalidCharacterReplacement)
                {
                    reasons.Add("unsupported characters normalized");
                }
            }
            else if (wasQuoted &&
                     sourceName.Any(char.IsUpper) &&
                     _options.IdentifierCaseMode is IdentifierCaseMode.PreserveQuoted
                         or IdentifierCaseMode.QuoteEveryIdentifier)
            {
                reasons.Add("source case preserved through quoting; PostgreSQL references remain case-sensitive");
            }
            if (isReserved)
            {
                reasons.Add("PostgreSQL restricted keyword safely quoted");
            }
            if (wasShortened)
            {
                reasons.Add("long identifier automatically shortened to the 63-byte PostgreSQL limit");
            }
            if (allocation.CollisionResolved)
            {
                reasons.Add("target namespace collision resolved deterministically");
            }
            if (reasons.Count == 0)
            {
                reasons.Add("safe mapping");
            }

            var mapping = new IdentifierMappingEntry(
                sourceObjectId,
                objectType,
                sourceSchema,
                sourceName,
                sourceQualifiedName,
                quotedSchema,
                quotedName,
                $"{quotedSchema}.{quotedName}",
                Encoding.UTF8.GetByteCount(sourceName),
                Encoding.UTF8.GetByteCount(allocation.Name),
                wasShortened,
                allocation.CollisionDetected,
                allocation.HashSuffix,
                string.Join("; ", reasons))
            {
                ParentObject = parentObject,
                SourceDatabase = sourceDatabase,
                SourceCharacterLength = sourceName.Length,
                TargetCharacterLength = allocation.Name.Length,
                IsReservedWord = isReserved,
                RequiresQuoting = requiresQuoting,
                WasQuoted = wasQuoted,
                WasCaseNormalized = wasCaseNormalized,
                CollisionResolved = allocation.CollisionResolved,
                InvalidCharacterReplacement = invalidCharacterReplacement,
                MappingStatus = status,
                Severity = severity,
                ManualReviewRequired = false,
                SourceKey = sourceKey ?? new SourceIdentifierKey(
                    sourceDatabase,
                    sourceSchema,
                    parentObject,
                    sourceName,
                    objectType,
                    null,
                    sourceObjectId),
                IncludedInScope = included,
                ConversionClassification = classification,
                MappingAction = allocation.CollisionResolved
                    ? IdentifierMappingAction.CollisionResolved
                    : wasShortened
                        ? IdentifierMappingAction.Truncated
                        : isReserved
                            ? IdentifierMappingAction.ReservedWordAdjusted
                            : invalidCharacterReplacement
                                ? IdentifierMappingAction.Sanitized
                                : wasCaseNormalized
                                ? IdentifierMappingAction.Lowercased
                                : IdentifierMappingAction.Unchanged,
                CollisionGroup = allocation.CollisionDetected
                    ? $"{targetSchema}:{objectType}:{normalized}"
                    : string.Empty,
                CollisionResolution = allocation.CollisionResolved
                    ? allocation.Name
                    : string.Empty
            };

            if (mapping.TargetUtf8ByteLength > MaximumBytes)
            {
                return mapping with
                {
                    MappingStatus = IdentifierMappingStatus.BlockingConflict,
                    Severity = IdentifierMappingSeverity.Error,
                    ManualReviewRequired = true
                };
            }

            return mapping;
        }

        private Allocation Allocate(string scope, string requested, string stableIdentity)
        {
            var normalized = Normalize(requested);
            var shortened = Shorten(normalized, stableIdentity);
            var initialHash = !string.Equals(shortened, normalized, StringComparison.Ordinal)
                ? Hash(stableIdentity)
                : null;
            var key = $"{scope}\u001f{shortened}";
            if (_allocated.TryAdd(key, stableIdentity) ||
                string.Equals(_allocated[key], stableIdentity, StringComparison.Ordinal))
            {
                return new Allocation(shortened, false, false, initialHash);
            }

            const int maximumCollisionAttempts = 4096;
            for (var collision = 0; collision < maximumCollisionAttempts; collision++)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var collisionIdentity = $"{stableIdentity}|collision|{collision}";
                var collisionName = WithHashSuffix(normalized, collisionIdentity);
                if (_allocated.TryAdd($"{scope}\u001f{collisionName}", stableIdentity) ||
                    string.Equals(
                        _allocated[$"{scope}\u001f{collisionName}"],
                        stableIdentity,
                        StringComparison.Ordinal))
                {
                    return new Allocation(
                        collisionName,
                        true,
                        true,
                        Hash(collisionIdentity));
                }
            }

            throw new InvalidOperationException(
                $"Identifier collision allocation exceeded {maximumCollisionAttempts:N0} deterministic attempts " +
                $"for namespace '{scope}'.");
        }

        private sealed class MappingLiveness(
            int total,
            CancellationToken cancellationToken,
            IProgress<ConversionProgress>? progress)
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private long _lastReportTicks;
            private int _processed;
            private string _lastObjectType = string.Empty;

            public void Tick(string objectType)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processed = Interlocked.Increment(ref _processed);
                _lastObjectType = objectType;
                var now = Stopwatch.GetTimestamp();
                var reportDue = processed == total ||
                    processed % 256 == 0 ||
                    Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReportTicks), now) >=
                    TimeSpan.FromMilliseconds(250);
                if (!reportDue)
                {
                    return;
                }

                Interlocked.Exchange(ref _lastReportTicks, now);
                var elapsed = _stopwatch.Elapsed;
                progress?.Report(new ConversionProgress(
                    ConversionStage.GeneratingIdentifierCandidates,
                    Math.Min(processed, total),
                    total,
                    $"Generating identifier candidates · {Math.Min(processed, total):N0}/{total:N0} · " +
                    $"{(elapsed.TotalSeconds <= 0 ? 0 : processed / elapsed.TotalSeconds):N0} mappings/sec")
                {
                    ObjectsPerSecond = elapsed.TotalSeconds <= 0 ? 0 : processed / elapsed.TotalSeconds,
                    Elapsed = elapsed,
                    CurrentObjectType = objectType,
                    LastProgressAt = DateTimeOffset.UtcNow
                });
            }

            public void Complete()
            {
                cancellationToken.ThrowIfCancellationRequested();
                var prior = Interlocked.Exchange(ref _processed, total);
                if (prior == total)
                {
                    return;
                }
                var elapsed = _stopwatch.Elapsed;
                progress?.Report(new ConversionProgress(
                    ConversionStage.GeneratingIdentifierCandidates,
                    total,
                    total,
                    $"Identifier candidates generated · {total:N0}/{total:N0}")
                {
                    ObjectsPerSecond = elapsed.TotalSeconds <= 0 ? 0 : total / elapsed.TotalSeconds,
                    Elapsed = elapsed,
                    CurrentObjectType = _lastObjectType.Length == 0 ? "Identifier" : _lastObjectType,
                    LastProgressAt = DateTimeOffset.UtcNow
                });
            }
        }

        private bool RequiresQuoting(string identifier)
        {
            var syntacticallySafe = identifier.Length > 0 &&
                (identifier[0] is >= 'a' and <= 'z' || identifier[0] == '_') &&
                identifier.Skip(1).All(character =>
                    character is >= 'a' and <= 'z' or >= '0' and <= '9' || character == '_');
            return _options.IdentifierCaseMode is IdentifierCaseMode.PreserveQuoted
                    or IdentifierCaseMode.QuoteEveryIdentifier ||
                !syntacticallySafe ||
                PostgreSqlKeywordRegistry.IsRestricted(_options.TargetVersion, identifier);
        }

        private string Normalize(string value)
        {
            if (_options.IdentifierCaseMode is IdentifierCaseMode.PreserveQuoted
                or IdentifierCaseMode.QuoteEveryIdentifier)
            {
                return value;
            }

            var lower = value.ToLowerInvariant();
            var builder = new StringBuilder(lower.Length);
            var replaced = false;
            foreach (var rune in lower.EnumerateRunes())
            {
                if (Rune.IsLetterOrDigit(rune) || rune.Value == '_')
                {
                    builder.Append(rune);
                    replaced = false;
                }
                else if (!replaced)
                {
                    builder.Append('_');
                    replaced = true;
                }
            }

            var normalized = builder.ToString().Trim('_');
            if (normalized.Length == 0)
            {
                normalized = "unnamed";
            }
            if (normalized[0] is >= '0' and <= '9')
            {
                normalized = $"_{normalized}";
            }
            return normalized;
        }

        private static bool IsCaseSensitiveCollation(string? collation) =>
            collation?.Contains("_CS_", StringComparison.OrdinalIgnoreCase) == true ||
            collation?.Contains("_BIN", StringComparison.OrdinalIgnoreCase) == true;

        private static bool IsFacetOwnedType(InventoryObjectType objectType) =>
            objectType is InventoryObjectType.Column
                or InventoryObjectType.PrimaryKey
                or InventoryObjectType.UniqueConstraint
                or InventoryObjectType.CheckConstraint
                or InventoryObjectType.ForeignKey
                or InventoryObjectType.DefaultConstraint
                or InventoryObjectType.Index
                or InventoryObjectType.Trigger;

        private static Dictionary<InventoryObjectId, InventoryObjectType>
            BuildEffectiveObjectTypes(InventorySnapshot inventory)
        {
            var result = new Dictionary<InventoryObjectId, InventoryObjectType>();

            foreach (var table in inventory.Tables)
            {
                result.TryAdd(table.ObjectId, InventoryObjectType.Table);
            }
            foreach (var sequence in inventory.Sequences)
            {
                result.TryAdd(sequence.ObjectId, InventoryObjectType.Sequence);
            }
            foreach (var synonym in inventory.Synonyms)
            {
                result.TryAdd(synonym.ObjectId, InventoryObjectType.Synonym);
            }
            foreach (var userDefinedType in inventory.UserDefinedTypes)
            {
                result.TryAdd(userDefinedType.ObjectId, InventoryObjectType.UserDefinedType);
            }
            foreach (var index in inventory.Indexes)
            {
                result.TryAdd(index.ObjectId, InventoryObjectType.Index);
            }
            foreach (var constraint in inventory.Constraints)
            {
                result.TryAdd(
                    constraint.ObjectId,
                    constraint.Kind switch
                    {
                        ConstraintKind.PrimaryKey => InventoryObjectType.PrimaryKey,
                        ConstraintKind.Unique => InventoryObjectType.UniqueConstraint,
                        ConstraintKind.Check => InventoryObjectType.CheckConstraint,
                        ConstraintKind.ForeignKey => InventoryObjectType.ForeignKey,
                        ConstraintKind.Default => InventoryObjectType.DefaultConstraint,
                        _ => InventoryObjectType.Unknown
                    });
            }

            return result;
        }

        private static InventoryObjectType EffectiveObjectType(
            InventoryObject source,
            Dictionary<InventoryObjectId, InventoryObjectType> effectiveObjectTypes)
        {
            if (source.ObjectType != InventoryObjectType.Unknown)
            {
                return source.ObjectType;
            }

            return effectiveObjectTypes.GetValueOrDefault(
                source.Id,
                InventoryObjectType.Unknown);
        }

        private string ResolveSchema(string sourceSchema)
        {
            var rule = _options.SchemaMappings.FirstOrDefault(item =>
                _sourceNameComparer.Equals(item.SourceSchema, sourceSchema));
            return rule?.TargetSchema ?? _options.SchemaMappingMode switch
            {
                SchemaMappingMode.MapDboToPublic when
                    _sourceNameComparer.Equals(sourceSchema, "dbo") => "public",
                SchemaMappingMode.MapAllToOne => _options.ConsolidatedSchema,
                _ => sourceSchema
            };
        }

        private string SchemaStableIdentity(
            string sourceDatabase,
            string sourceSchema,
            string requestedSchema)
        {
            var target = Normalize(requestedSchema);
            return _options.SchemaMappingMode is SchemaMappingMode.MapAllToOne
                    or SchemaMappingMode.Custom
                ? $"{sourceDatabase}|target-schema|explicit|{target}"
                : $"{sourceDatabase}|target-schema|{sourceSchema}|{target}";
        }

        private static string ObjectAllocationScope(
            string targetSchema,
            InventoryObjectType objectType) =>
            objectType switch
            {
                InventoryObjectType.Function or InventoryObjectType.StoredProcedure =>
                    $"{targetSchema}\u001froutine",
                InventoryObjectType.Table or InventoryObjectType.View or InventoryObjectType.Sequence
                    or InventoryObjectType.UserDefinedType =>
                    $"{targetSchema}\u001frelation",
                _ => $"{targetSchema}\u001f{objectType}"
            };

        private static string ChildAllocationScope(
            InventoryObjectId ownerId,
            string targetSchema,
            string objectType) =>
            objectType switch
            {
                "column" or "constraint" or "parameter" or "field" or "trigger" =>
                    $"{ownerId}\u001f{objectType}",
                "index" or "sequence" or "trigger_function" or "helper" or "temporary" =>
                    $"{targetSchema}\u001frelation",
                _ => $"{ownerId}\u001f{objectType}"
            };

        private static string Shorten(string value, string stableIdentity) =>
            Encoding.UTF8.GetByteCount(value) <= MaximumBytes
                ? value
                : WithHashSuffix(value, stableIdentity);

        private static string WithHashSuffix(string value, string stableIdentity)
        {
            var hash = Hash(stableIdentity);
            var prefixBudget = MaximumBytes - hash.Length - 1;
            var builder = new StringBuilder();
            var byteCount = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (byteCount + rune.Utf8SequenceLength > prefixBudget)
                {
                    break;
                }

                builder.Append(rune);
                byteCount += rune.Utf8SequenceLength;
            }

            var result = $"{builder}_{hash}";
            if (Encoding.UTF8.GetByteCount(result) > MaximumBytes)
            {
                throw new InvalidOperationException(
                    "Identifier shortening produced a name above PostgreSQL's 63-byte limit.");
            }
            return result;
        }

        private static string Hash(string stableIdentity) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity)))[..8]
                .ToLowerInvariant();

        private sealed record Allocation(
            string Name,
            bool CollisionDetected,
            bool CollisionResolved,
            string? HashSuffix);
    }
}
