namespace MigrationStudio.Domain.Inventory;

public sealed record InventoryDependency(
    InventoryObjectId SourceObjectId,
    InventoryObjectId? TargetObjectId,
    DependencyKind Kind,
    string ReferencedName,
    bool IsResolved,
    bool IsAmbiguous,
    string? ReferencedServer = null,
    string? ReferencedDatabase = null,
    string? Evidence = null,
    int? StronglyConnectedComponent = null);

public sealed record DependencyComponent(
    int Id,
    IReadOnlyList<InventoryObjectId> Members,
    bool IsCycle);
