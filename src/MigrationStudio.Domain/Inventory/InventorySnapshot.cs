namespace MigrationStudio.Domain.Inventory;

public sealed record InventorySnapshot
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public string DiscoveryEngineVersion { get; init; } = string.Empty;

    public string ApplicationVersion { get; init; } = string.Empty;

    public DateTimeOffset SnapshotTimestamp { get; init; }

    public MigrationScopeMode ScopeMode { get; init; }

    public DatabaseMetadata Database { get; init; } = null!;

    public IReadOnlyList<SchemaInventory> Schemas { get; init; } = [];

    public IReadOnlyList<InventoryObject> Objects { get; init; } = [];

    public IReadOnlyList<TableInventory> Tables { get; init; } = [];

    public IReadOnlyList<ColumnInventory> Columns { get; init; } = [];

    public IReadOnlyList<ConstraintInventory> Constraints { get; init; } = [];

    public IReadOnlyList<IndexInventory> Indexes { get; init; } = [];

    public IReadOnlyList<ModuleInventory> Modules { get; init; } = [];

    public IReadOnlyList<SequenceInventory> Sequences { get; init; } = [];

    public IReadOnlyList<UserDefinedTypeInventory> UserDefinedTypes { get; init; } = [];

    public IReadOnlyList<SynonymInventory> Synonyms { get; init; } = [];

    public IReadOnlyList<SecurityPrincipalInventory> SecurityPrincipals { get; init; } = [];

    public IReadOnlyList<PermissionInventory> Permissions { get; init; } = [];

    public IReadOnlyList<TemporalTableInventory> TemporalTables { get; init; } = [];

    public IReadOnlyList<TriggerInventory> Triggers { get; init; } = [];

    public IReadOnlyList<ChangeDataInventory> ChangeData { get; init; } = [];

    public IReadOnlyList<EncryptionInventory> Encryption { get; init; } = [];

    public IReadOnlyList<FullTextInventory> FullText { get; init; } = [];

    public IReadOnlyList<ServiceBrokerInventory> ServiceBroker { get; init; } = [];

    public IReadOnlyList<SqlAgentJobInventory> SqlAgentJobs { get; init; } = [];

    public IReadOnlyList<ExternalDependencyInventory> ExternalDependencies { get; init; } = [];

    public IReadOnlyList<PartitionFunctionInventory> PartitionFunctions { get; init; } = [];

    public IReadOnlyList<PartitionSchemeInventory> PartitionSchemes { get; init; } = [];

    public IReadOnlyList<ReplicationInventory> Replication { get; init; } = [];

    public IReadOnlyList<InventoryDependency> Dependencies { get; init; } = [];

    public IReadOnlyList<DependencyComponent> DependencyComponents { get; init; } = [];

    public IReadOnlyList<InventoryFinding> Findings { get; init; } = [];
}
