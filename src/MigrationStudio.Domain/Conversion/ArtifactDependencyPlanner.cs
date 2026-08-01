using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.Conversion;

/// <summary>
/// Produces the canonical, global execution order for generated artifacts.
/// Dependency edges always take precedence over deployment-phase preferences.
/// </summary>
public static class ArtifactDependencyPlanner
{
    public static IReadOnlySet<InventoryObjectId> GetTransitiveDependencyClosure<T>(
        IReadOnlyList<T> artifacts,
        Func<T, InventoryObjectId> sourceId,
        Func<T, IReadOnlyList<InventoryObjectId>> dependencies,
        Func<T, bool> isSeed)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(isSeed);

        var artifactsById = artifacts
            .GroupBy(sourceId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var closure = artifacts
            .Where(isSeed)
            .Select(sourceId)
            .ToHashSet();
        var pending = new Stack<InventoryObjectId>(closure.OrderBy(item => item.Value));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!artifactsById.TryGetValue(current, out var currentArtifacts))
            {
                continue;
            }

            foreach (var dependency in currentArtifacts
                         .SelectMany(dependencies)
                         .Where(artifactsById.ContainsKey)
                         .Distinct()
                         .OrderByDescending(item => item.Value))
            {
                if (closure.Add(dependency))
                {
                    pending.Push(dependency);
                }
            }
        }

        return closure;
    }

    public static IReadOnlyList<T> Order<T>(
        IReadOnlyList<T> artifacts,
        Func<T, InventoryObjectId> sourceId,
        Func<T, IReadOnlyList<InventoryObjectId>> dependencies,
        Func<T, int> preferredRank,
        Func<T, string> stableName,
        bool failOnCycle)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(preferredRank);
        ArgumentNullException.ThrowIfNull(stableName);

        var groups = artifacts
            .GroupBy(sourceId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var knownIds = groups.Keys.ToHashSet();
        var groupDependencies = groups.ToDictionary(
            group => group.Key,
            group => group.Value
                .SelectMany(dependencies)
                .Where(item => knownIds.Contains(item) && item != group.Key)
                .Distinct()
                .ToArray());
        var remaining = groupDependencies.ToDictionary(
            item => item.Key,
            item => item.Value.Length);
        var dependents = new Dictionary<InventoryObjectId, List<InventoryObjectId>>();
        foreach (var (artifactId, artifactDependencies) in groupDependencies)
        {
            foreach (var dependency in artifactDependencies)
            {
                if (!dependents.TryGetValue(dependency, out var items))
                {
                    items = [];
                    dependents.Add(dependency, items);
                }
                items.Add(artifactId);
            }
        }

        var comparer = Comparer<T>.Create((left, right) =>
        {
            var comparison = preferredRank(left).CompareTo(preferredRank(right));
            if (comparison != 0)
            {
                return comparison;
            }
            comparison = StringComparer.Ordinal.Compare(stableName(left), stableName(right));
            if (comparison != 0)
            {
                return comparison;
            }
            return sourceId(left).Value.CompareTo(sourceId(right).Value);
        });
        var sourceComparer = Comparer<InventoryObjectId>.Create((left, right) =>
        {
            var leftItem = groups[left].OrderBy(item => item, comparer).First();
            var rightItem = groups[right].OrderBy(item => item, comparer).First();
            var comparison = comparer.Compare(leftItem, rightItem);
            return comparison != 0 ? comparison : left.Value.CompareTo(right.Value);
        });
        var ready = new SortedSet<InventoryObjectId>(
            remaining.Where(item => item.Value == 0).Select(item => item.Key),
            sourceComparer);
        var ordered = new List<T>(artifacts.Count);
        while (ready.Count > 0)
        {
            var artifactId = ready.Min;
            ready.Remove(artifactId);
            ordered.AddRange(groups[artifactId].OrderBy(item => item, comparer));
            foreach (var dependentId in dependents.GetValueOrDefault(artifactId) ?? [])
            {
                if (--remaining[dependentId] == 0)
                {
                    ready.Add(dependentId);
                }
            }
        }

        if (ordered.Count == artifacts.Count)
        {
            EnsureDependenciesPrecedeDependents(ordered, sourceId, dependencies);
            return ordered;
        }

        var cycleIds = remaining
            .Where(item => item.Value > 0)
            .Select(item => item.Key)
            .OrderBy(item => item.Value)
            .ToArray();
        if (failOnCycle)
        {
            throw new InvalidDataException(
                "Artifact dependency graph contains a cycle. Package publication was refused. " +
                $"Objects: {string.Join(", ", cycleIds)}.");
        }

        // Conversion and diagnostics must remain inspectable even when a source cycle
        // exists. Append the cyclic component deterministically; package publication
        // performs the strict check.
        ordered.AddRange(
            groups.Where(item => remaining[item.Key] > 0)
                .OrderBy(item => item.Key, sourceComparer)
                .SelectMany(item => item.Value.OrderBy(value => value, comparer)));
        return ordered;
    }

    public static void EnsureDependenciesPrecedeDependents<T>(
        IReadOnlyList<T> ordered,
        Func<T, InventoryObjectId> sourceId,
        Func<T, IReadOnlyList<InventoryObjectId>> dependencies)
    {
        var positions = ordered
            .Select((item, index) => (Id: sourceId(item), Position: index))
            .GroupBy(item => item.Id)
            .ToDictionary(
                group => group.Key,
                group => (First: group.Min(item => item.Position), Last: group.Max(item => item.Position)));
        foreach (var artifact in ordered)
        {
            var artifactId = sourceId(artifact);
            foreach (var dependency in dependencies(artifact).Distinct())
            {
                if (dependency == artifactId)
                {
                    continue;
                }
                if (positions.TryGetValue(dependency, out var dependencyPosition) &&
                    dependencyPosition.Last >= positions[artifactId].First)
                {
                    throw new InvalidDataException(
                        $"Artifact dependency order is invalid: {dependency} must precede {artifactId}.");
                }
            }
        }
    }
}
