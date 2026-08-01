namespace MigrationStudio.Domain.Inventory;

public sealed record SecurityPrincipalInventory(
    InventoryObjectId ObjectId,
    int PrincipalId,
    string Name,
    string TypeDescription,
    string AuthenticationType,
    string? DefaultSchema,
    bool IsFixedRole,
    bool IsOrphaned,
    IReadOnlyList<string> RoleMemberships);

public sealed record PermissionInventory(
    InventoryObjectId ObjectId,
    string State,
    string PermissionName,
    string ClassDescription,
    string Grantee,
    string Grantor,
    InventoryObjectId? TargetObjectId,
    string? ColumnName);

public sealed record TemporalTableInventory(
    InventoryObjectId CurrentTableId,
    InventoryObjectId? HistoryTableId,
    string? PeriodStartColumn,
    string? PeriodEndColumn,
    string? HistoryRetentionPeriod,
    bool? DataConsistencyCheck,
    bool IsSystemVersioned);

public sealed record TriggerInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId? ParentObjectId,
    string Scope,
    bool IsInsteadOf,
    bool IsDisabled,
    bool IsNotForReplication,
    string? ExecuteAsPrincipal,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> FirstForEvents,
    IReadOnlyList<string> LastForEvents);

public sealed record ChangeDataInventory(
    InventoryObjectId? TableObjectId,
    string Feature,
    bool IsEnabled,
    string? CaptureInstance,
    string? Retention,
    bool? AutoCleanup,
    bool? TrackColumnsUpdated,
    IReadOnlyList<string> TrackedColumns);

public sealed record EncryptionInventory(
    InventoryObjectId? ObjectId,
    string Kind,
    string Name,
    string? Algorithm,
    string? Provider,
    bool IsDatabaseEncryptionKey,
    string? State);

public sealed record FullTextInventory(
    InventoryObjectId ObjectId,
    string Kind,
    string Name,
    InventoryObjectId? TargetObjectId,
    string? ChangeTrackingState,
    string? Stoplist,
    IReadOnlyList<string> IndexedColumns);

public sealed record ServiceBrokerInventory(
    InventoryObjectId ObjectId,
    string Kind,
    string Name,
    bool IsEnabled,
    string? RelatedObject);

public sealed record SqlAgentJobInventory(
    InventoryObjectId ObjectId,
    Guid JobId,
    string Name,
    bool IsEnabled,
    string Owner,
    string Category,
    IReadOnlyList<SqlAgentStepInventory> Steps,
    IReadOnlyList<string> Schedules);

public sealed record SqlAgentStepInventory(
    int StepId,
    string Name,
    string Subsystem,
    string? DatabaseName,
    string CommandMetadata,
    string? ProxyName);

public sealed record ExternalDependencyInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId? SourceObjectId,
    string ReferenceKind,
    string ReferencedName,
    string? ServerName,
    string? DatabaseName,
    string? SchemaName,
    bool IsResolved,
    string Evidence);

public sealed record PartitionFunctionInventory(
    InventoryObjectId ObjectId,
    bool RangeRight,
    IReadOnlyList<string> BoundaryValues);

public sealed record PartitionSchemeInventory(
    InventoryObjectId ObjectId,
    string FunctionName,
    IReadOnlyList<string> DestinationDataSpaces);

public sealed record ReplicationInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId? SourceObjectId,
    string Kind,
    string Name,
    bool IsEnabled,
    string? Detail);
