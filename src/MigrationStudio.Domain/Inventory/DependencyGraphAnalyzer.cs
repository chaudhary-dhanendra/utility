namespace MigrationStudio.Domain.Inventory;

public static class DependencyGraphAnalyzer
{
    public static IReadOnlyList<DependencyComponent> FindStronglyConnectedComponents(
        IEnumerable<InventoryObjectId> objectIds,
        IEnumerable<InventoryDependency> dependencies,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectIds);
        ArgumentNullException.ThrowIfNull(dependencies);

        var nodes = objectIds.Distinct().OrderBy(item => item.Value).ToArray();
        var nodeSet = nodes.ToHashSet();
        var resolvedEdges = dependencies
            .Where(edge => edge.IsResolved && edge.TargetObjectId is not null &&
                           nodeSet.Contains(edge.SourceObjectId) &&
                           nodeSet.Contains(edge.TargetObjectId.Value))
            .OrderBy(edge => edge.SourceObjectId.Value)
            .ThenBy(edge => edge.TargetObjectId!.Value.Value)
            .ToArray();
        var adjacency = resolvedEdges
            .GroupBy(edge => edge.SourceObjectId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.TargetObjectId!.Value)
                    .Distinct().OrderBy(item => item.Value).ToArray());
        var reverseAdjacency = resolvedEdges
            .GroupBy(edge => edge.TargetObjectId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.SourceObjectId)
                    .Distinct().OrderBy(item => item.Value).ToArray());

        var visited = new HashSet<InventoryObjectId>();
        var finishOrder = new List<InventoryObjectId>(nodes.Length);
        foreach (var root in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(root))
            {
                continue;
            }

            var traversal = new Stack<(InventoryObjectId Node, bool Expanded)>();
            traversal.Push((root, false));
            while (traversal.Count > 0)
            {
                if ((finishOrder.Count & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var (node, expanded) = traversal.Pop();
                if (expanded)
                {
                    finishOrder.Add(node);
                    continue;
                }

                traversal.Push((node, true));
                if (!adjacency.TryGetValue(node, out var targets))
                {
                    continue;
                }

                for (var index = targets.Length - 1; index >= 0; index--)
                {
                    if (visited.Add(targets[index]))
                    {
                        traversal.Push((targets[index], false));
                    }
                }
            }
        }

        var assigned = new HashSet<InventoryObjectId>();
        var componentId = 0;
        var components = new List<DependencyComponent>();
        for (var orderIndex = finishOrder.Count - 1; orderIndex >= 0; orderIndex--)
        {
            if ((orderIndex & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var root = finishOrder[orderIndex];
            if (!assigned.Add(root))
            {
                continue;
            }

            var members = new List<InventoryObjectId>();
            var traversal = new Stack<InventoryObjectId>();
            traversal.Push(root);
            while (traversal.Count > 0)
            {
                if ((members.Count & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var node = traversal.Pop();
                members.Add(node);
                if (!reverseAdjacency.TryGetValue(node, out var sources))
                {
                    continue;
                }

                foreach (var source in sources)
                {
                    if (assigned.Add(source))
                    {
                        traversal.Push(source);
                    }
                }
            }

            var selfCycle = members.Count == 1 &&
                            adjacency.TryGetValue(members[0], out var selfTargets) &&
                            selfTargets.Contains(members[0]);
            members.Sort(static (left, right) => left.Value.CompareTo(right.Value));
            components.Add(new DependencyComponent(componentId++, members, members.Count > 1 || selfCycle));
        }

        return components;
    }

    public static IReadOnlyList<InventoryDependency> AssignComponents(
        IReadOnlyList<InventoryDependency> dependencies,
        IReadOnlyList<DependencyComponent> components)
    {
        var componentByObject = components
            .Where(component => component.IsCycle)
            .SelectMany(component => component.Members.Select(member => (member, component.Id)))
            .ToDictionary(item => item.member, item => item.Id);

        return dependencies.Select(edge =>
        {
            if (componentByObject.TryGetValue(edge.SourceObjectId, out var sourceComponent) &&
                edge.TargetObjectId is { } target &&
                componentByObject.TryGetValue(target, out var targetComponent) &&
                sourceComponent == targetComponent)
            {
                return edge with { StronglyConnectedComponent = sourceComponent };
            }

            return edge;
        }).ToArray();
    }
}
