using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

internal sealed class InventoryAccumulator
{
    private readonly Dictionary<int, InventoryObject> _objectsBySqlId = [];
    private readonly Dictionary<InventoryObjectId, InventoryObject> _objects = [];
    private readonly Dictionary<int, int> _parentSqlIds = [];
    private readonly Dictionary<int, SchemaInventory> _schemasById = [];
    private readonly Dictionary<(int ObjectId, int ColumnId), InventoryObjectId> _columnIds = [];
    private readonly Dictionary<InventoryObjectId, List<ExtendedProperty>> _extendedProperties = [];

    public InventoryAccumulator(string databaseName)
    {
        DatabaseName = databaseName;
    }

    public string DatabaseName { get; }

    public DatabaseMetadata Database { get; set; } = null!;

    public int SqlServerMajorVersion { get; set; }

    public List<TableInventory> Tables { get; } = [];

    public List<ColumnInventory> Columns { get; } = [];

    public List<ConstraintInventory> Constraints { get; } = [];

    public List<IndexInventory> Indexes { get; } = [];

    public List<ModuleInventory> Modules { get; } = [];

    public List<SequenceInventory> Sequences { get; } = [];

    public List<UserDefinedTypeInventory> UserDefinedTypes { get; } = [];

    public List<SynonymInventory> Synonyms { get; } = [];

    public List<SecurityPrincipalInventory> SecurityPrincipals { get; } = [];

    public List<PermissionInventory> Permissions { get; } = [];

    public List<TemporalTableInventory> TemporalTables { get; } = [];

    public List<TriggerInventory> Triggers { get; } = [];

    public List<ChangeDataInventory> ChangeData { get; } = [];

    public List<EncryptionInventory> Encryption { get; } = [];

    public List<FullTextInventory> FullText { get; } = [];

    public List<ServiceBrokerInventory> ServiceBroker { get; } = [];

    public List<SqlAgentJobInventory> SqlAgentJobs { get; } = [];

    public List<ExternalDependencyInventory> ExternalDependencies { get; } = [];

    public List<PartitionFunctionInventory> PartitionFunctions { get; } = [];

    public List<PartitionSchemeInventory> PartitionSchemes { get; } = [];

    public List<ReplicationInventory> Replication { get; } = [];

    public List<InventoryDependency> Dependencies { get; } = [];

    public List<InventoryFinding> Findings { get; } = [];

    public IReadOnlyDictionary<int, InventoryObject> ObjectsBySqlId => _objectsBySqlId;

    public IReadOnlyDictionary<int, SchemaInventory> SchemasById => _schemasById;

    public int TotalFacetCount =>
        _objects.Count + Tables.Count + Columns.Count + Constraints.Count + Indexes.Count +
        Modules.Count + Sequences.Count + UserDefinedTypes.Count + Synonyms.Count +
        SecurityPrincipals.Count + Permissions.Count + TemporalTables.Count + Triggers.Count +
        ChangeData.Count + Encryption.Count + FullText.Count + ServiceBroker.Count +
        SqlAgentJobs.Count + ExternalDependencies.Count + PartitionFunctions.Count +
        PartitionSchemes.Count + Replication.Count + Dependencies.Count;

    public void AddSchema(int schemaId, string name, string? owner, int objectCount, bool isSystem)
    {
        var id = InventoryObjectId.Create(DatabaseName, InventoryObjectType.Schema, string.Empty, name, schemaId);
        var item = new InventoryObject(
            id,
            DatabaseName,
            string.Empty,
            name,
            $"[{Escape(name)}]",
            InventoryObjectType.Schema,
            null,
            null,
            null,
            null,
            isSystem,
            false,
            SelectionReason.None,
            0,
            0,
            [],
            ConversionClassification.Automatic,
            null,
            null,
            HashMetadata(new { schemaId, name, owner, objectCount, isSystem }),
            [],
            DiscoveryStatus.Discovered);
        _schemasById[schemaId] = new SchemaInventory(item, owner, objectCount, isSystem, !isSystem);
        _objects[id] = item;
    }

    public InventoryObject AddObject(
        int sqlObjectId,
        int parentSqlObjectId,
        string schema,
        string name,
        InventoryObjectType objectType,
        DateTimeOffset? created,
        DateTimeOffset? modified,
        bool isSystem,
        string? definition,
        DiscoveryStatus status,
        ConversionClassification? classification = null,
        object? metadata = null)
    {
        var parentId = parentSqlObjectId != 0 && _objectsBySqlId.TryGetValue(parentSqlObjectId, out var parent)
            ? parent.Id
            : (InventoryObjectId?)null;
        var id = InventoryObjectId.Create(DatabaseName, objectType, schema, name, sqlObjectId, parentId);
        var warnings = status == DiscoveryStatus.DefinitionUnavailable
            ? new[]
            {
                new InventoryFinding(
                    "DISCOVERY.DEFINITION_UNAVAILABLE",
                    FindingSeverity.Warning,
                    "The source definition is encrypted, obfuscated, or unavailable to the current principal.",
                    id)
            }
            : [];
        var item = new InventoryObject(
            id,
            DatabaseName,
            schema,
            name,
            Qualified(schema, name),
            objectType,
            sqlObjectId,
            parentId,
            created,
            modified,
            isSystem,
            false,
            SelectionReason.None,
            0,
            0,
            warnings,
            classification ?? InventoryClassification.ForObject(objectType),
            definition,
            definition is null ? null : HashText(definition),
            HashMetadata(metadata ?? new { sqlObjectId, parentSqlObjectId, schema, name, objectType, created, modified, isSystem }),
            [],
            status);

        _objectsBySqlId[sqlObjectId] = item;
        _objects[id] = item;
        if (parentSqlObjectId != 0)
        {
            _parentSqlIds[sqlObjectId] = parentSqlObjectId;
        }

        return item;
    }

    public InventoryObject AddSyntheticObject(
        InventoryObjectType type,
        string schema,
        string name,
        InventoryObjectId? parentId,
        ConversionClassification classification,
        object metadata,
        bool isSystem = false)
    {
        var id = InventoryObjectId.Create(DatabaseName, type, schema, name, null, parentId);
        if (_objects.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var item = new InventoryObject(
            id,
            DatabaseName,
            schema,
            name,
            Qualified(schema, name),
            type,
            null,
            parentId,
            null,
            null,
            isSystem,
            false,
            SelectionReason.None,
            0,
            0,
            [],
            classification,
            null,
            null,
            HashMetadata(metadata),
            [],
            DiscoveryStatus.Discovered);
        _objects[id] = item;
        return item;
    }

    public InventoryObject AddColumnObject(int objectId, int columnId, string name, object metadata)
    {
        var parent = GetObject(objectId);
        var id = InventoryObjectId.Create(DatabaseName, InventoryObjectType.Column, parent.SourceSchema, name, columnId, parent.Id);
        var item = new InventoryObject(
            id,
            DatabaseName,
            parent.SourceSchema,
            name,
            $"{parent.QualifiedSourceName}.[{Escape(name)}]",
            InventoryObjectType.Column,
            null,
            parent.Id,
            null,
            null,
            parent.IsSystemObject,
            false,
            SelectionReason.None,
            0,
            0,
            [],
            ConversionClassification.Automatic,
            null,
            null,
            HashMetadata(metadata),
            [],
            DiscoveryStatus.Discovered);
        _objects[id] = item;
        _columnIds[(objectId, columnId)] = id;
        return item;
    }

    public InventoryObject GetObject(int sqlObjectId) =>
        _objectsBySqlId.TryGetValue(sqlObjectId, out var item)
            ? item
            : throw new InvalidDataException($"Catalog object {sqlObjectId} was not discovered.");

    public void UpdateObject(int sqlObjectId, Func<InventoryObject, InventoryObject> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var current = GetObject(sqlObjectId);
        var changed = update(current);
        _objectsBySqlId[sqlObjectId] = changed;
        _objects.Remove(current.Id);
        _objects[changed.Id] = changed;
    }

    public InventoryObjectId? TryGetObjectId(int? sqlObjectId) =>
        sqlObjectId is { } id && _objectsBySqlId.TryGetValue(id, out var item) ? item.Id : null;

    public InventoryObjectId? TryResolveObjectId(
        string? schema,
        string? entity,
        string? referencingSchema,
        out bool isAmbiguous)
    {
        isAmbiguous = false;
        if (string.IsNullOrWhiteSpace(entity))
        {
            return null;
        }

        var effectiveSchema = !string.IsNullOrWhiteSpace(schema)
            ? schema
            : referencingSchema;
        var candidates = _objects.Values
            .Where(item => item.SourceName.Equals(entity, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(effectiveSchema) ||
                           item.SourceSchema.Equals(effectiveSchema, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Id)
            .Distinct()
            .ToArray();
        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        isAmbiguous = candidates.Length > 1;
        return null;
    }

    public InventoryObjectId? TryGetColumnId(int objectId, int columnId) =>
        _columnIds.TryGetValue((objectId, columnId), out var id) ? id : null;

    public void AddExtendedProperty(InventoryObjectId targetId, ExtendedProperty property)
    {
        if (!_extendedProperties.TryGetValue(targetId, out var properties))
        {
            properties = [];
            _extendedProperties[targetId] = properties;
        }

        properties.Add(property);
    }

    public InventorySnapshot Build(string applicationVersion)
    {
        ResolveParentIds();
        ApplyExtendedProperties();

        var distinctDependencies = Dependencies.Distinct().ToArray();
        var components = DependencyGraphAnalyzer.FindStronglyConnectedComponents(_objects.Keys, distinctDependencies);
        var assignedDependencies = DependencyGraphAnalyzer.AssignComponents(distinctDependencies, components);
        var snapshot = new InventorySnapshot
        {
            DiscoveryEngineVersion = typeof(InventoryAccumulator).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            ApplicationVersion = applicationVersion,
            SnapshotTimestamp = DateTimeOffset.UtcNow,
            ScopeMode = MigrationScopeMode.CompleteDatabase,
            Database = Database,
            Schemas = _schemasById.Values.OrderBy(schema => schema.InventoryObject.SourceName, StringComparer.OrdinalIgnoreCase).ToArray(),
            Objects = _objects.Values
                .OrderBy(item => item.ObjectType)
                .ThenBy(item => item.SourceSchema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Tables = Tables,
            Columns = Columns,
            Constraints = Constraints,
            Indexes = Indexes,
            Modules = Modules,
            Sequences = Sequences,
            UserDefinedTypes = UserDefinedTypes,
            Synonyms = Synonyms,
            SecurityPrincipals = SecurityPrincipals,
            Permissions = Permissions,
            TemporalTables = TemporalTables,
            Triggers = Triggers,
            ChangeData = ChangeData,
            Encryption = Encryption,
            FullText = FullText,
            ServiceBroker = ServiceBroker,
            SqlAgentJobs = SqlAgentJobs,
            ExternalDependencies = ExternalDependencies,
            PartitionFunctions = PartitionFunctions,
            PartitionSchemes = PartitionSchemes,
            Replication = Replication,
            Dependencies = assignedDependencies,
            DependencyComponents = components,
            Findings = Findings.Select(finding => finding.Normalize()).ToArray()
        };

        return snapshot;
    }

    private void ResolveParentIds()
    {
        foreach (var (sqlObjectId, parentSqlId) in _parentSqlIds)
        {
            if (!_objectsBySqlId.TryGetValue(sqlObjectId, out var child) ||
                !_objectsBySqlId.TryGetValue(parentSqlId, out var parent))
            {
                continue;
            }

            var updated = child with { ParentObjectId = parent.Id };
            _objectsBySqlId[sqlObjectId] = updated;
            _objects[updated.Id] = updated;
        }
    }

    private void ApplyExtendedProperties()
    {
        foreach (var (id, properties) in _extendedProperties)
        {
            if (!_objects.TryGetValue(id, out var item))
            {
                continue;
            }

            var updated = item with { ExtendedProperties = properties };
            _objects[id] = updated;
            if (item.SqlServerObjectId is { } sqlId)
            {
                _objectsBySqlId[sqlId] = updated;
            }
        }
    }

    public static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string HashMetadata(object value) =>
        HashText(JsonSerializer.Serialize(value));

    private static string Qualified(string schema, string name) =>
        string.IsNullOrWhiteSpace(schema) ? $"[{Escape(name)}]" : $"[{Escape(schema)}].[{Escape(name)}]";

    private static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);
}
