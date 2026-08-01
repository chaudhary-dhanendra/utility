namespace MigrationStudio.Domain.Inventory;

public sealed record InventoryObject(
    InventoryObjectId Id,
    string SourceDatabase,
    string SourceSchema,
    string SourceName,
    string QualifiedSourceName,
    InventoryObjectType ObjectType,
    int? SqlServerObjectId,
    InventoryObjectId? ParentObjectId,
    DateTimeOffset? CreationDate,
    DateTimeOffset? ModificationDate,
    bool IsSystemObject,
    bool IsIncluded,
    SelectionReason SelectionReason,
    int DependencyCount,
    int DependentCount,
    IReadOnlyList<InventoryFinding> DiscoveryWarnings,
    ConversionClassification ConversionClassification,
    string? SourceDefinition,
    string? SourceDefinitionHash,
    string MetadataHash,
    IReadOnlyList<ExtendedProperty> ExtendedProperties,
    DiscoveryStatus DiscoveryStatus);

public sealed record ExtendedProperty(
    string Name,
    string? Value,
    string TargetLevel,
    InventoryObjectId TargetObjectId,
    string? TargetSubObjectName = null);

public sealed record SchemaInventory(
    InventoryObject InventoryObject,
    string? Owner,
    int ObjectCount,
    bool IsSystemSchema,
    bool IsIncludedByDefault);
