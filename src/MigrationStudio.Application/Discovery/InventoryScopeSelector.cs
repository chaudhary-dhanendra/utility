using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Discovery;

public static class InventoryScopeSelector
{
    private static readonly ISourceObjectScopePolicy DefaultScopePolicy =
        new SqlServerUserObjectScopePolicy();

    public static InventorySnapshot Apply(
        InventorySnapshot snapshot,
        InventoryDiscoveryRequest request,
        ISourceObjectScopePolicy? scopePolicy = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        scopePolicy ??= DefaultScopePolicy;

        var objectsById = snapshot.Objects.ToDictionary(item => item.Id);
        var selected = new Dictionary<InventoryObjectId, SelectionReason>();

        foreach (var item in snapshot.Objects.Where(scopePolicy.IsUserMigrationObject))
        {
            var reason = request.ScopeMode switch
            {
                MigrationScopeMode.CompleteDatabase => SelectionReason.CompleteDatabase,
                MigrationScopeMode.SelectedSchemas when request.SelectedSchemas.Contains(item.SourceSchema) =>
                    SelectionReason.SelectedSchema,
                MigrationScopeMode.ExcelSelectedTables when request.ExcelMatchedTableIds.Contains(item.Id) =>
                    SelectionReason.ExcelMatch,
                MigrationScopeMode.ManualObjectSelection when request.SelectedObjectIds.Contains(item.Id) =>
                    SelectionReason.ManualSelection,
                _ => SelectionReason.None
            };

            if (reason != SelectionReason.None)
            {
                selected[item.Id] = reason;
            }
        }

        RemoveOutOfScope(selected, objectsById, scopePolicy);
        IncludeParents(selected, objectsById, scopePolicy);
        if (request.DependencyPolicy != DependencyPolicy.SelectedOnly)
        {
            IncludeRequiredDependencies(selected, snapshot.Dependencies, objectsById, scopePolicy);
        }

        if (request.DependencyPolicy == DependencyPolicy.IncludeDependenciesAndDependents)
        {
            IncludeDependents(selected, snapshot.Dependencies, objectsById, scopePolicy);
        }

        var dependenciesBySource = snapshot.Dependencies
            .Where(edge => edge.TargetObjectId is not null)
            .GroupBy(edge => edge.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Count());
        var dependentsByTarget = snapshot.Dependencies
            .Where(edge => edge.TargetObjectId is not null)
            .GroupBy(edge => edge.TargetObjectId!.Value)
            .ToDictionary(group => group.Key, group => group.Count());

        var updatedObjects = snapshot.Objects.Select(item => item with
        {
            IsIncluded = selected.ContainsKey(item.Id),
            SelectionReason = selected.GetValueOrDefault(item.Id, SelectionReason.None),
            DependencyCount = dependenciesBySource.GetValueOrDefault(item.Id),
            DependentCount = dependentsByTarget.GetValueOrDefault(item.Id)
        }).ToArray();

        var includedSchemas = updatedObjects
            .Where(item => item.IsIncluded)
            .Select(item => item.SourceSchema)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedSchemas = snapshot.Schemas.Select(schema => schema with
        {
            InventoryObject = schema.InventoryObject with
            {
                IsIncluded = includedSchemas.Contains(schema.InventoryObject.SourceName),
                SelectionReason = includedSchemas.Contains(schema.InventoryObject.SourceName)
                    ? SelectionReason.ParentObject
                    : SelectionReason.None
            }
        }).ToArray();

        return snapshot with
        {
            ScopeMode = request.ScopeMode,
            Objects = updatedObjects,
            Schemas = updatedSchemas
        };
    }

    private static void IncludeParents(
        IDictionary<InventoryObjectId, SelectionReason> selected,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        ISourceObjectScopePolicy scopePolicy)
    {
        var pending = new Queue<InventoryObjectId>(selected.Keys);
        while (pending.TryDequeue(out var id))
        {
            if (!objectsById.TryGetValue(id, out var item) ||
                item.ParentObjectId is not { } parentId ||
                selected.ContainsKey(parentId) ||
                !objectsById.TryGetValue(parentId, out var parent) ||
                !scopePolicy.IsUserMigrationObject(parent))
            {
                continue;
            }

            selected[parentId] = SelectionReason.ParentObject;
            pending.Enqueue(parentId);
        }
    }

    private static void IncludeRequiredDependencies(
        IDictionary<InventoryObjectId, SelectionReason> selected,
        IEnumerable<InventoryDependency> dependencies,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        ISourceObjectScopePolicy scopePolicy)
    {
        var edges = dependencies
            .Where(edge => edge.IsResolved && edge.TargetObjectId is not null)
            .GroupBy(edge => edge.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.TargetObjectId!.Value).Distinct());
        var pending = new Queue<InventoryObjectId>(selected.Keys);

        while (pending.TryDequeue(out var id))
        {
            if (!edges.TryGetValue(id, out var targets))
            {
                continue;
            }

            foreach (var target in targets.Where(id =>
                         objectsById.TryGetValue(id, out var item) &&
                         scopePolicy.IsUserMigrationObject(item)))
            {
                if (selected.TryAdd(target, SelectionReason.RequiredDependency))
                {
                    pending.Enqueue(target);
                }
            }
        }

        IncludeParents(selected, objectsById, scopePolicy);
    }

    private static void IncludeDependents(
        IDictionary<InventoryObjectId, SelectionReason> selected,
        IEnumerable<InventoryDependency> dependencies,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        ISourceObjectScopePolicy scopePolicy)
    {
        var reverseEdges = dependencies
            .Where(edge => edge.IsResolved && edge.TargetObjectId is not null)
            .GroupBy(edge => edge.TargetObjectId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.SourceObjectId).Distinct());
        var pending = new Queue<InventoryObjectId>(selected.Keys);

        while (pending.TryDequeue(out var id))
        {
            if (!reverseEdges.TryGetValue(id, out var sources))
            {
                continue;
            }

            foreach (var source in sources.Where(id =>
                         objectsById.TryGetValue(id, out var item) &&
                         scopePolicy.IsUserMigrationObject(item)))
            {
                if (selected.TryAdd(source, SelectionReason.IncludedDependent))
                {
                    pending.Enqueue(source);
                }
            }
        }

        IncludeParents(selected, objectsById, scopePolicy);
    }

    private static void RemoveOutOfScope(
        IDictionary<InventoryObjectId, SelectionReason> selected,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        ISourceObjectScopePolicy scopePolicy)
    {
        foreach (var id in selected.Keys
                     .Where(id => !objectsById.TryGetValue(id, out var item) ||
                         !scopePolicy.IsUserMigrationObject(item))
                     .ToArray())
        {
            selected.Remove(id);
        }
    }
}
