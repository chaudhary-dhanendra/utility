using System.Security.Cryptography;
using System.Text;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Deployment;

public static class DeploymentGraphPlanner
{
    public static (DeploymentGraph Graph, DeploymentPlan Plan) Build(
        MigrationPackageManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var artifacts = manifest.Artifacts
            .Select((artifact, index) => new ArtifactEntry(artifact, index, CreateArtifactId(artifact)))
            .ToArray();
        var duplicateArtifactIds = artifacts.GroupBy(item => item.ArtifactId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateArtifactIds.Length > 0)
        {
            throw new InvalidDataException(
                "Deployment graph contains duplicate artifact IDs: " +
                string.Join(", ", duplicateArtifactIds));
        }
        var duplicateTargets = artifacts
            .Where(item => item.Artifact.IsExecutable && !item.Artifact.RequiresManualReview)
            .GroupBy(item => TargetIdentity(item.Artifact), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var bySource = artifacts.GroupBy(item => item.Artifact.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Order(ArtifactEntryComparer.Instance).ToArray());
        var byTarget = artifacts.GroupBy(item => NormalizeQualifiedName(
                $"{item.Artifact.TargetSchema}.{item.Artifact.TargetName}"), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Order(ArtifactEntryComparer.Instance).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var mappings = manifest.ObjectMappings.GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        var edges = new List<DeploymentGraphEdge>();
        var unresolved = new List<DeploymentUnresolvedDependency>();
        var external = new List<DeploymentUnresolvedDependency>();
        foreach (var entry in artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var dependencyId in entry.Artifact.Dependencies.Distinct())
            {
                var target = ResolveDependency(dependencyId, bySource, mappings, byTarget);
                if (target is null)
                {
                    unresolved.Add(new DeploymentUnresolvedDependency(
                        entry.ArtifactId,
                        dependencyId,
                        dependencyId.ToString(),
                        DeploymentDependencyKind.HardDeploymentDependency,
                        true,
                        "An explicit package dependency could not be resolved to an artifact or target mapping."));
                    continue;
                }

                AddEdge(edges, ClassifyEdge(entry, target, "Explicit package dependency."));
            }

            AddParentOwnershipEdge(entry, artifacts, edges);
            AddSchemaOwnershipEdge(entry, artifacts, edges);

            foreach (var extension in entry.Artifact.RequiredExtensions
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var dependency = new DeploymentUnresolvedDependency(
                    entry.ArtifactId,
                    null,
                    extension,
                    DeploymentDependencyKind.ExternalDependency,
                    false,
                    "The PostgreSQL extension is provisioned outside the artifact manifest by the package extension script.");
                external.Add(dependency);
                AddEdge(edges, new DeploymentGraphEdge(
                    entry.ArtifactId,
                    null,
                    DeploymentDependencyKind.ExternalDependency,
                    false,
                    true,
                    dependency.Reason,
                    ReferencedTarget: extension));
            }
        }

        AddGeneratedOwnershipEdges(bySource, edges);
        edges = edges.Distinct().OrderBy(item => item.FromArtifactId, StringComparer.Ordinal)
            .ThenBy(item => item.ToArtifactId, StringComparer.Ordinal)
            .ThenBy(item => item.DependencyKind)
            .ToList();

        var components = FindComponents(artifacts, edges, cancellationToken);
        var dependenciesByNode = edges.Where(item => item.ToArtifactId is not null)
            .GroupBy(item => item.FromArtifactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.ToArtifactId!)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var dependentsByNode = edges.Where(item => item.ToArtifactId is not null)
            .GroupBy(item => item.ToArtifactId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.FromArtifactId)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal);
        var nodes = artifacts.Order(ArtifactEntryComparer.Instance).Select(item => new DeploymentGraphNode(
            item.ArtifactId,
            item.Artifact.SourceObjectId,
            $"{item.Artifact.TargetSchema}.{item.Artifact.TargetName}",
            item.Artifact.TargetSchema,
            item.Artifact.TargetName,
            item.Artifact.TargetObjectType,
            item.Artifact.Phase,
            item.Artifact.IsExecutable,
            item.Artifact.RequiresManualReview,
            dependenciesByNode.GetValueOrDefault(item.ArtifactId) ?? [],
            dependentsByNode.GetValueOrDefault(item.ArtifactId) ?? [])).ToArray();
        var graph = new DeploymentGraph(
            nodes,
            edges,
            components,
            unresolved.OrderBy(item => item.FromArtifactId, StringComparer.Ordinal).ToArray(),
            external.OrderBy(item => item.FromArtifactId, StringComparer.Ordinal)
                .ThenBy(item => item.ReferencedName, StringComparer.Ordinal).ToArray(),
            duplicateArtifactIds,
            duplicateTargets);
        return (graph, CreatePlan(graph));
    }

    private static DeploymentPlan CreatePlan(DeploymentGraph graph)
    {
        var deployable = graph.Nodes.Where(item => item.IsExecutable && !item.RequiresManualReview)
            .ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var deferredReasons = graph.UnresolvedDependencies.Where(item => item.IsHardBlocking)
            .Where(item => deployable.ContainsKey(item.FromArtifactId))
            .ToDictionary(item => item.FromArtifactId,
                item => $"Unresolved internal dependency: {item.ReferencedName}.", StringComparer.Ordinal);
        var allNodes = graph.Nodes.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var allHardEdges = graph.Edges.Where(item => item.IsHardBlocking && item.ToArtifactId is not null)
            .Where(item => deployable.ContainsKey(item.FromArtifactId)).ToArray();
        foreach (var edge in allHardEdges.Where(item => !deployable.ContainsKey(item.ToArtifactId!)))
        {
            var target = allNodes[edge.ToArtifactId!];
            deferredReasons.TryAdd(
                edge.FromArtifactId,
                $"Hard prerequisite {target.TargetQualifiedName} is not deployable" +
                (target.RequiresManualReview ? " because it requires manual review." : "."));
        }
        var hardEdges = allHardEdges.Where(item => deployable.ContainsKey(item.ToArtifactId!)).ToArray();

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var edge in hardEdges)
            {
                if (!deferredReasons.ContainsKey(edge.FromArtifactId) &&
                    deferredReasons.ContainsKey(edge.ToArtifactId!))
                {
                    deferredReasons[edge.FromArtifactId] =
                        $"Hard prerequisite {deployable[edge.ToArtifactId!].TargetQualifiedName} was deferred.";
                    changed = true;
                }
            }
        }

        var candidates = deployable.Values.Where(item => !deferredReasons.ContainsKey(item.ArtifactId))
            .ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var indegree = candidates.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in hardEdges.Where(item => candidates.ContainsKey(item.FromArtifactId) &&
                                                     candidates.ContainsKey(item.ToArtifactId!)))
        {
            indegree[edge.FromArtifactId]++;
            if (!dependents.TryGetValue(edge.ToArtifactId!, out var values))
            {
                values = [];
                dependents.Add(edge.ToArtifactId!, values);
            }
            values.Add(edge.FromArtifactId);
        }

        var ready = new SortedSet<DeploymentGraphNode>(DeploymentNodeComparer.Instance);
        foreach (var node in candidates.Values.Where(item => indegree[item.ArtifactId] == 0))
        {
            ready.Add(node);
        }
        var ordered = new List<DeploymentGraphNode>(candidates.Count);
        while (ready.Count > 0)
        {
            var node = ready.Min!;
            ready.Remove(node);
            ordered.Add(node);
            foreach (var dependent in dependents.GetValueOrDefault(node.ArtifactId) ?? [])
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Add(candidates[dependent]);
                }
            }
        }

        foreach (var unscheduled in candidates.Values.Where(item => indegree[item.ArtifactId] > 0))
        {
            deferredReasons[unscheduled.ArtifactId] = "Artifact participates in a hard deployment cycle.";
        }
        var deferred = deferredReasons.OrderBy(item => deployable[item.Key], DeploymentNodeComparer.Instance)
            .Select(item => new DeferredDeploymentArtifact(
                item.Key,
                deployable[item.Key].TargetQualifiedName,
                deployable[item.Key].DeploymentPhase,
                item.Value)).ToArray();

        var stages = new List<DeploymentPlanStage>();
        foreach (var phaseGroup in ConsecutivePhaseGroups(ordered))
        {
            var artifactIds = phaseGroup.Items.Select(item => item.ArtifactId).ToArray();
            var prerequisites = graph.Edges
                .Where(edge => edge.IsHardBlocking && artifactIds.Contains(edge.FromArtifactId, StringComparer.Ordinal) &&
                               edge.ToArtifactId is not null)
                .Select(edge => edge.ToArtifactId!).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            stages.Add(new DeploymentPlanStage(
                stages.Count + 1,
                phaseGroup.Phase,
                artifactIds,
                phaseGroup.Items.Select(item => item.TargetQualifiedName).ToArray(),
                prerequisites));
        }

        var cycles = graph.StronglyConnectedComponents.Where(item => item.IsCycle).ToArray();
        var stats = new DeploymentPlanStatistics(
            graph.Nodes.Count,
            graph.Nodes.Count(item => item.IsExecutable && !item.RequiresManualReview),
            graph.Nodes.Count(item => item.RequiresManualReview),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.HardDeploymentDependency),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.RuntimeDependency),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.OptionalCompatibilityDependency),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.ExternalDependency),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.ManualReviewDependency),
            graph.Edges.Count(item => item.DependencyKind == DeploymentDependencyKind.PhaseOrderingDependency),
            graph.UnresolvedDependencies.Count,
            graph.StronglyConnectedComponents.Count,
            cycles.Length,
            cycles.Count(item => item.Resolution == DeploymentCycleResolution.HardUnresolvableDeploymentCycle),
            ordered.Count,
            deferred.Length);
        return new DeploymentPlan(
            CreatePlanId(graph),
            stages,
            ordered.Select(item => item.ArtifactId).ToArray(),
            deferred,
            graph.ExternalDependencies,
            graph.Edges.Where(item => item.DependencyKind == DeploymentDependencyKind.ManualReviewDependency).ToArray(),
            cycles,
            graph.UnresolvedDependencies,
            stats);
    }

    private static DeploymentGraphEdge ClassifyEdge(ArtifactEntry from, ArtifactEntry to, string reason)
    {
        if ((to.Artifact.RequiresManualReview || !to.Artifact.IsExecutable) &&
            !RequiresCreationTimeResolution(from.Artifact))
        {
            return new DeploymentGraphEdge(from.ArtifactId, to.ArtifactId,
                to.Artifact.RequiresManualReview
                    ? DeploymentDependencyKind.ManualReviewDependency
                    : DeploymentDependencyKind.OptionalCompatibilityDependency,
                false, false, reason, to.Artifact.SourceObjectId,
                $"{to.Artifact.TargetSchema}.{to.Artifact.TargetName}");
        }

        var runtime = from.Artifact.Phase == DeploymentPhase.Procedures &&
                      IsRuntimeReferenceTarget(to.Artifact);
        return new DeploymentGraphEdge(
            from.ArtifactId,
            to.ArtifactId,
            runtime ? DeploymentDependencyKind.RuntimeDependency : DeploymentDependencyKind.HardDeploymentDependency,
            !runtime,
            false,
            reason,
            to.Artifact.SourceObjectId,
            $"{to.Artifact.TargetSchema}.{to.Artifact.TargetName}");
    }

    private static bool IsProgrammable(PackageArtifactManifest artifact) =>
        artifact.Phase is DeploymentPhase.Functions or DeploymentPhase.PreDataFunctions or
            DeploymentPhase.Procedures or DeploymentPhase.Triggers;

    private static bool RequiresCreationTimeResolution(PackageArtifactManifest artifact) =>
        artifact.Phase is DeploymentPhase.Types or DeploymentPhase.Sequences or DeploymentPhase.Tables or
            DeploymentPhase.PreDataFunctions or DeploymentPhase.DefaultsAndGeneratedColumns or
            DeploymentPhase.PrimaryKeys or DeploymentPhase.UniqueConstraints or DeploymentPhase.CheckConstraints or
            DeploymentPhase.ForeignKeys or DeploymentPhase.Indexes or DeploymentPhase.Functions or
            DeploymentPhase.Views or DeploymentPhase.Triggers;

    private static bool IsRuntimeReferenceTarget(PackageArtifactManifest artifact) =>
        artifact.Phase is DeploymentPhase.Functions or DeploymentPhase.PreDataFunctions or
            DeploymentPhase.Procedures or DeploymentPhase.Views or DeploymentPhase.Tables;

    private static ArtifactEntry? ResolveDependency(
        InventoryObjectId id,
        IReadOnlyDictionary<InventoryObjectId, ArtifactEntry[]> bySource,
        Dictionary<InventoryObjectId, IdentifierMappingEntry[]> mappings,
        IReadOnlyDictionary<string, ArtifactEntry[]> byTarget)
    {
        if (bySource.TryGetValue(id, out var direct))
        {
            return direct.FirstOrDefault(item => item.Artifact.IsExecutable && !item.Artifact.RequiresManualReview)
                   ?? direct[0];
        }
        if (!mappings.TryGetValue(id, out var mapped))
        {
            return null;
        }
        foreach (var mapping in mapped.OrderBy(item => item.TargetQualifiedName, StringComparer.Ordinal))
        {
            if (byTarget.TryGetValue(NormalizeQualifiedName(mapping.TargetQualifiedName), out var candidates))
            {
                return candidates.FirstOrDefault(item => item.Artifact.IsExecutable && !item.Artifact.RequiresManualReview)
                       ?? candidates[0];
            }
        }
        return null;
    }

    private static void AddParentOwnershipEdge(
        ArtifactEntry entry,
        IReadOnlyList<ArtifactEntry> artifacts,
        ICollection<DeploymentGraphEdge> edges)
    {
        if (string.IsNullOrWhiteSpace(entry.Artifact.TargetParentObject))
        {
            return;
        }
        var parent = NormalizeQualifiedName(entry.Artifact.TargetParentObject);
        var target = artifacts.FirstOrDefault(item =>
            item.Artifact.Phase == DeploymentPhase.Tables &&
            NormalizeQualifiedName($"{item.Artifact.TargetSchema}.{item.Artifact.TargetName}") == parent);
        if (target is not null)
        {
            AddEdge(edges, new DeploymentGraphEdge(entry.ArtifactId, target.ArtifactId,
                DeploymentDependencyKind.HardDeploymentDependency, true, false,
                "Relation-scoped artifact requires its parent relation.", target.Artifact.SourceObjectId,
                $"{target.Artifact.TargetSchema}.{target.Artifact.TargetName}"));
        }
    }

    private static void AddSchemaOwnershipEdge(
        ArtifactEntry entry,
        IReadOnlyList<ArtifactEntry> artifacts,
        ICollection<DeploymentGraphEdge> edges)
    {
        if (entry.Artifact.Phase == DeploymentPhase.Schemas)
        {
            return;
        }
        var schema = artifacts.FirstOrDefault(item => item.Artifact.Phase == DeploymentPhase.Schemas &&
            string.Equals(item.Artifact.TargetName.Trim('"'), entry.Artifact.TargetSchema.Trim('"'),
                StringComparison.OrdinalIgnoreCase));
        if (schema is not null)
        {
            AddEdge(edges, new DeploymentGraphEdge(entry.ArtifactId, schema.ArtifactId,
                DeploymentDependencyKind.PhaseOrderingDependency, true, false,
                "Contained object requires its target schema.", schema.Artifact.SourceObjectId,
                schema.Artifact.TargetName));
        }
    }

    private static void AddGeneratedOwnershipEdges(
        IReadOnlyDictionary<InventoryObjectId, ArtifactEntry[]> bySource,
        ICollection<DeploymentGraphEdge> edges)
    {
        foreach (var group in bySource.Values.Where(items => items.Length > 1))
        {
            var owner = group.FirstOrDefault(item => item.Artifact.IsExecutable && !item.Artifact.RequiresManualReview);
            if (owner is null)
            {
                continue;
            }
            foreach (var generated in group.Where(item => item != owner && item.Artifact.IsExecutable))
            {
                AddEdge(edges, new DeploymentGraphEdge(generated.ArtifactId, owner.ArtifactId,
                    DeploymentDependencyKind.PhaseOrderingDependency, true, false,
                    "Generated artifact is ordered after the canonical artifact with the same source owner.",
                    owner.Artifact.SourceObjectId,
                    $"{owner.Artifact.TargetSchema}.{owner.Artifact.TargetName}"));
            }
        }
    }

    private static void AddEdge(ICollection<DeploymentGraphEdge> edges, DeploymentGraphEdge edge)
    {
        if (!string.Equals(edge.FromArtifactId, edge.ToArtifactId, StringComparison.Ordinal))
        {
            edges.Add(edge);
        }
    }

    private static List<DeploymentStronglyConnectedComponent> FindComponents(
        IReadOnlyList<ArtifactEntry> artifacts,
        IReadOnlyList<DeploymentGraphEdge> edges,
        CancellationToken cancellationToken)
    {
        var ids = artifacts.Select(item => item.ArtifactId).Order(StringComparer.Ordinal).ToArray();
        var internalEdges = edges.Where(item => item.ToArtifactId is not null &&
                                                item.DependencyKind is not DeploymentDependencyKind.ExternalDependency and
                                                    not DeploymentDependencyKind.OptionalCompatibilityDependency)
            .ToArray();
        var adjacency = internalEdges.GroupBy(item => item.FromArtifactId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.ToArtifactId!).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var reverse = internalEdges.GroupBy(item => item.ToArtifactId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.Select(item => item.FromArtifactId).Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var finish = new List<string>(ids.Length);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(root)) continue;
            var stack = new Stack<(string Id, int NextTarget)>();
            stack.Push((root, 0));
            while (stack.TryPop(out var current))
            {
                var targets = adjacency.GetValueOrDefault(current.Id) ?? [];
                if (current.NextTarget >= targets.Length)
                {
                    finish.Add(current.Id);
                    continue;
                }
                stack.Push((current.Id, current.NextTarget + 1));
                var target = targets[current.NextTarget];
                if (visited.Add(target))
                {
                    stack.Push((target, 0));
                }
            }
        }
        var entries = artifacts.ToDictionary(item => item.ArtifactId, StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DeploymentStronglyConnectedComponent>();
        for (var index = finish.Count - 1; index >= 0; index--)
        {
            var root = finish[index];
            if (!assigned.Add(root)) continue;
            var members = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.TryPop(out var current))
            {
                members.Add(current);
                foreach (var source in reverse.GetValueOrDefault(current) ?? [])
                {
                    if (assigned.Add(source)) stack.Push(source);
                }
            }
            members.Sort(StringComparer.Ordinal);
            var memberSet = members.ToHashSet(StringComparer.Ordinal);
            var componentEdges = internalEdges.Where(item => memberSet.Contains(item.FromArtifactId) &&
                                                              memberSet.Contains(item.ToArtifactId!)).ToArray();
            var isCycle = members.Count > 1 || componentEdges.Any(item => item.FromArtifactId == item.ToArtifactId);
            var (resolution, reason) = ClassifyCycle(members, componentEdges, entries, isCycle);
            result.Add(new DeploymentStronglyConnectedComponent(result.Count, members, isCycle, resolution, reason));
        }
        return result;
    }

    private static (DeploymentCycleResolution? Resolution, string Reason) ClassifyCycle(
        IReadOnlyList<string> members,
        IReadOnlyList<DeploymentGraphEdge> edges,
        IReadOnlyDictionary<string, ArtifactEntry> entries,
        bool isCycle)
    {
        if (!isCycle) return (null, "Acyclic component.");
        if (members.Any(id => entries[id].Artifact.RequiresManualReview) ||
            edges.Any(edge => edge.DependencyKind == DeploymentDependencyKind.ManualReviewDependency))
            return (DeploymentCycleResolution.ManualReviewCycle, "The cycle contains a manual-review artifact or dependency.");
        if (edges.All(edge => edge.DependencyKind == DeploymentDependencyKind.RuntimeDependency))
            return (DeploymentCycleResolution.RuntimeOnlyCycle, "All cycle edges are runtime references and do not constrain creation order.");
        var phases = members.Select(id => entries[id].Artifact.Phase).ToHashSet();
        if (phases.Contains(DeploymentPhase.ForeignKeys) || phases.Contains(DeploymentPhase.Triggers))
            return (DeploymentCycleResolution.ResolvableByPhaseSeparation,
                "Foreign-key or trigger creation can be separated from base relation creation by deployment phase.");
        var componentPhases = members.Select(id => entries[id].Artifact.Phase).ToHashSet();
        if (members.All(id => IsProgrammable(entries[id].Artifact)) ||
            componentPhases.Contains(DeploymentPhase.Views) &&
            componentPhases.Any(phase => phase is DeploymentPhase.Functions or DeploymentPhase.PreDataFunctions) ||
            edges.Any(edge => edge.DependencyKind == DeploymentDependencyKind.RuntimeDependency))
            return (DeploymentCycleResolution.ResolvableByCompatibilityStub,
                "The programmable-object cycle requires runtime deferral or a compatibility stub.");
        return (DeploymentCycleResolution.HardUnresolvableDeploymentCycle,
            "The component contains a hard creation-time dependency cycle.");
    }

    private static IEnumerable<(DeploymentPhase Phase, IReadOnlyList<DeploymentGraphNode> Items)>
        ConsecutivePhaseGroups(List<DeploymentGraphNode> ordered)
    {
        var index = 0;
        while (index < ordered.Count)
        {
            var phase = ordered[index].DeploymentPhase;
            var values = new List<DeploymentGraphNode>();
            while (index < ordered.Count && ordered[index].DeploymentPhase == phase)
            {
                values.Add(ordered[index++]);
            }
            yield return (phase, values);
        }
    }

    private static string CreateArtifactId(PackageArtifactManifest artifact)
    {
        var identity = $"{artifact.SourceObjectId.Value:N}|{artifact.Phase}|{TargetIdentity(artifact)}|{artifact.SqlSha256}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return $"artifact-{hash}";
    }

    private static Guid CreatePlanId(DeploymentGraph graph)
    {
        var canonical = string.Join('\n', graph.Nodes.Select(item => item.ArtifactId)
            .Concat(graph.Edges.Select(item => $"{item.FromArtifactId}>{item.ToArtifactId}:{item.DependencyKind}")));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))[..16]);
    }

    private static string TargetIdentity(PackageArtifactManifest artifact) =>
        $"{artifact.TargetObjectType}|{NormalizeQualifiedName($"{artifact.TargetSchema}.{artifact.TargetName}")}|" +
        $"{NormalizeQualifiedName(artifact.TargetParentObject)}|{artifact.RoutineIdentityArguments}";

    private static string NormalizeQualifiedName(string value) =>
        value.Replace("\"", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();

    private sealed record ArtifactEntry(PackageArtifactManifest Artifact, int OriginalIndex, string ArtifactId);

    private sealed class ArtifactEntryComparer : IComparer<ArtifactEntry>
    {
        public static ArtifactEntryComparer Instance { get; } = new();
        public int Compare(ArtifactEntry? left, ArtifactEntry? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = DeploymentPhaseOrdering.GetRank(left.Artifact.Phase, left.Artifact.TargetObjectType)
                .CompareTo(DeploymentPhaseOrdering.GetRank(right.Artifact.Phase, right.Artifact.TargetObjectType));
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Artifact.TargetSchema, right.Artifact.TargetSchema);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.Artifact.TargetName, right.Artifact.TargetName);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.ArtifactId, right.ArtifactId);
            return result != 0 ? result : left.OriginalIndex.CompareTo(right.OriginalIndex);
        }
    }

    private sealed class DeploymentNodeComparer : IComparer<DeploymentGraphNode>
    {
        public static DeploymentNodeComparer Instance { get; } = new();
        public int Compare(DeploymentGraphNode? left, DeploymentGraphNode? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var result = DeploymentPhaseOrdering.GetRank(left.DeploymentPhase, left.ObjectType)
                .CompareTo(DeploymentPhaseOrdering.GetRank(right.DeploymentPhase, right.ObjectType));
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.TargetSchema, right.TargetSchema);
            if (result != 0) return result;
            result = StringComparer.Ordinal.Compare(left.TargetName, right.TargetName);
            return result != 0 ? result : StringComparer.Ordinal.Compare(left.ArtifactId, right.ArtifactId);
        }
    }
}
