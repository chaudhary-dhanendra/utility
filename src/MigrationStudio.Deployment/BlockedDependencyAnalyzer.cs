using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Deployment;

public static class BlockedDependencyAnalyzer
{
    public static IReadOnlyList<BlockedArtifactAnalysis> Analyze(
        DeploymentGraph graph,
        DeploymentPlan plan,
        IReadOnlyList<PersistedBlockedArtifact> blockedArtifacts)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(blockedArtifacts);

        var nodesBySource = graph.Nodes.GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var edgesBySource = graph.Edges.GroupBy(item => item.FromArtifactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var ordered = plan.OrderedArtifacts.ToHashSet(StringComparer.Ordinal);
        var deferred = plan.DeferredArtifacts.Select(item => item.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var results = new List<BlockedArtifactAnalysis>(blockedArtifacts.Count);

        foreach (var blocked in blockedArtifacts.OrderBy(item => item.TargetQualifiedName, StringComparer.Ordinal))
        {
            var node = ResolveBlockedNode(blocked, nodesBySource);
            if (node is null)
            {
                results.Add(new BlockedArtifactAnalysis(
                    blocked.SourceObjectId,
                    blocked.TargetQualifiedName,
                    string.Empty,
                    true,
                    blocked.BlockingDependencies.Select(item => new BlockingDependencyAnalysis(
                        item,
                        item.ToString(),
                        DeploymentDependencyKind.HardDeploymentDependency,
                        true,
                        false,
                        "The persisted blocked artifact is absent from the package deployment graph.",
                        "Regenerate the package manifest so the blocked artifact can be resolved by artifact identity."))
                        .ToArray()));
                continue;
            }

            var analyses = blocked.BlockingDependencies.Select(dependency => AnalyzeDependency(
                node,
                dependency,
                nodesBySource,
                edgesBySource,
                ordered,
                deferred)).ToArray();
            results.Add(new BlockedArtifactAnalysis(
                blocked.SourceObjectId,
                blocked.TargetQualifiedName,
                node.ArtifactId,
                analyses.Any(item => !item.IsFalseDependencyBlock && item.ContributesToIndegree),
                analyses));
        }
        return results;
    }

    private static BlockingDependencyAnalysis AnalyzeDependency(
        DeploymentGraphNode blockedNode,
        InventoryObjectId dependencyId,
        IReadOnlyDictionary<InventoryObjectId, DeploymentGraphNode[]> nodesBySource,
        IReadOnlyDictionary<string, DeploymentGraphEdge[]> edgesBySource,
        HashSet<string> ordered,
        HashSet<string> deferred)
    {
        var candidates = nodesBySource.GetValueOrDefault(dependencyId) ?? [];
        var candidateIds = candidates.Select(item => item.ArtifactId).ToHashSet(StringComparer.Ordinal);
        var edge = (edgesBySource.GetValueOrDefault(blockedNode.ArtifactId) ?? [])
            .Where(item => item.ToArtifactId is not null && candidateIds.Contains(item.ToArtifactId))
            .OrderByDescending(item => item.IsHardBlocking)
            .ThenBy(item => item.DependencyKind)
            .FirstOrDefault();
        var target = edge?.ToArtifactId is { } targetId
            ? candidates.FirstOrDefault(item => item.ArtifactId == targetId)
            : candidates.FirstOrDefault(item => item.IsExecutable && !item.RequiresManualReview)
              ?? candidates.FirstOrDefault();
        var targetName = target?.TargetQualifiedName ?? dependencyId.ToString();

        if (edge is null)
        {
            return new BlockingDependencyAnalysis(
                dependencyId,
                targetName,
                DeploymentDependencyKind.OptionalCompatibilityDependency,
                false,
                true,
                "The persisted source-object blocker has no dependency edge from this package artifact; it is a false source-level block.",
                "Track availability by artifact ID and retain the source reference only as diagnostic metadata.");
        }

        if (!edge.IsHardBlocking)
        {
            return new BlockingDependencyAnalysis(
                dependencyId,
                targetName,
                edge.DependencyKind,
                false,
                true,
                edge.Reason + " This dependency does not contribute to deployment indegree.",
                edge.DependencyKind == DeploymentDependencyKind.ManualReviewDependency
                    ? "Deploy the artifact independently; resolve the manual dependency before runtime use."
                    : "Retain the edge for diagnostics and exclude it from creation-time blockage.");
        }

        if (target is not null && ordered.Contains(target.ArtifactId))
        {
            return new BlockingDependencyAnalysis(
                dependencyId,
                targetName,
                edge.DependencyKind,
                true,
                true,
                edge.Reason + " The required artifact is present and ordered before the dependent, so the persisted block is stale or source-ID-wide.",
                "Use the artifact-level plan result instead of propagating unavailable status across every artifact sharing a source object ID.");
        }

        return new BlockingDependencyAnalysis(
            dependencyId,
            targetName,
            edge.DependencyKind,
            true,
            false,
            target?.RequiresManualReview == true
                ? edge.Reason + " The required creation-time prerequisite is a non-executable manual-review artifact."
                : deferred.Contains(target?.ArtifactId ?? string.Empty)
                ? edge.Reason + " The required artifact is deferred by the deployment plan."
                : edge.Reason + " The required artifact is not present in the ordered deployment plan.",
            target?.RequiresManualReview == true
                ? "Complete and deploy the manual-review prerequisite, then schedule this dependent artifact."
                : "Resolve or deploy the hard prerequisite before scheduling this artifact.");
    }

    private static DeploymentGraphNode? ResolveBlockedNode(
        PersistedBlockedArtifact blocked,
        IReadOnlyDictionary<InventoryObjectId, DeploymentGraphNode[]> nodesBySource)
    {
        var candidates = nodesBySource.GetValueOrDefault(blocked.SourceObjectId) ?? [];
        return candidates.FirstOrDefault(item =>
                   string.Equals(item.TargetQualifiedName, blocked.TargetQualifiedName,
                       StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault(item => item.IsExecutable && !item.RequiresManualReview)
               ?? candidates.FirstOrDefault();
    }
}
