using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Deployment;

public sealed record DeploymentPublicationPlanningResult(
    DeploymentGraph Graph,
    DeploymentPlan Plan,
    BlockedDependencyReconciliation Reconciliation);

public static class DeploymentPublicationReconciler
{
    public static DeploymentPublicationPlanningResult Reconcile(ConversionRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var manifest = CreatePlanningManifest(run);
        var (graph, initialPlan) = DeploymentGraphPlanner.Build(manifest);
        var blocked = run.Artifacts
            .Where(item => item.Validation.Outcome == LiveSqlValidationOutcome.BlockedByDependency)
            .Select(item => new PersistedBlockedArtifact(
                item.SourceObjectId,
                item.TargetObjectId.QualifiedName,
                item.Validation.BlockingDependencies.Count > 0
                    ? item.Validation.BlockingDependencies
                    : item.Dependencies))
            .ToArray();
        var analyses = BlockedDependencyAnalyzer.Analyze(graph, initialPlan, blocked);
        var plan = initialPlan with { PersistedBlockedArtifacts = analyses };
        var deferred = plan.DeferredArtifacts.Select(item => item.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        var decisions = analyses.Select(analysis => CreateDecision(analysis, deferred)).ToArray();
        var reconciliation = new BlockedDependencyReconciliation(
            blocked.Length,
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.HardBlocked),
            decisions.Count(item => !item.IsFatal),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.RuntimeOnly),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.Optional),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.ManualReviewDependency),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.ExternalDependency),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.FalseOrCascadingBlock),
            decisions.Count(item => item.ReconciledClassification == ReconciledBlockedClassification.DeferredByDeploymentPlan),
            decisions)
        {
            DirectValidationFailureCount = run.Artifacts.Count(item =>
                item.Validation.Outcome == LiveSqlValidationOutcome.Failed),
            NotRunExecutableCount = run.Artifacts.Count(item =>
                ConversionArtifactReconciler.IsDeployableExecutable(item) &&
                item.Validation.Outcome is not LiveSqlValidationOutcome.Failed and
                    not LiveSqlValidationOutcome.BlockedByDependency &&
                !ConversionArtifactReconciler.HasCurrentSuccessfulLiveValidation(item)),
            HardCycleCount = plan.Statistics.HardCycleCount,
            UnresolvedInternalDependencyCount = plan.Statistics.UnresolvedInternalDependencies,
            DeploymentPlanId = plan.PlanId
        };
        return new DeploymentPublicationPlanningResult(graph, plan, reconciliation);
    }

    public static IReadOnlyList<ConversionArtifact> OrderForPackage(
        ConversionRun run,
        DeploymentPublicationPlanningResult planning)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(planning);
        var byIdentity = run.Artifacts.ToDictionary(ArtifactIdentity, StringComparer.Ordinal);
        var nodeIdentity = planning.Graph.Nodes.ToDictionary(
            item => item.ArtifactId,
            item => NodeIdentity(item.SourceObjectId, item.ObjectType, item.TargetQualifiedName,
                item.DeploymentPhase),
            StringComparer.Ordinal);
        var ordered = new List<ConversionArtifact>(run.Artifacts.Count);
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in planning.Plan.OrderedArtifacts.Concat(
                     planning.Plan.DeferredArtifacts.Select(item => item.ArtifactId)))
        {
            if (nodeIdentity.TryGetValue(id, out var identity) &&
                byIdentity.TryGetValue(identity, out var artifact) &&
                included.Add(identity))
            {
                ordered.Add(artifact);
            }
        }
        ordered.AddRange(run.Artifacts
            .Where(item => included.Add(ArtifactIdentity(item)))
            .OrderBy(item => DeploymentPhaseOrdering.GetRank(
                item.DeploymentPhase, item.TargetObjectId.ObjectType))
            .ThenBy(item => item.TargetObjectId.Schema, StringComparer.Ordinal)
            .ThenBy(item => item.TargetObjectId.Name, StringComparer.Ordinal)
            .ThenBy(item => item.SourceObjectId.Value));
        return ordered;
    }

    public static bool IsDeferred(
        ConversionArtifact artifact,
        DeploymentPublicationPlanningResult planning)
    {
        var identity = ArtifactIdentity(artifact);
        var node = planning.Graph.Nodes.FirstOrDefault(item =>
            NodeIdentity(item.SourceObjectId, item.ObjectType, item.TargetQualifiedName,
                item.DeploymentPhase) == identity);
        return node is not null && planning.Plan.DeferredArtifacts.Any(item =>
            item.ArtifactId == node.ArtifactId);
    }

    private static MigrationPackageManifest CreatePlanningManifest(ConversionRun run)
    {
        var mappings = run.IdentifierMappings.GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        return new MigrationPackageManifest
        {
            MigrationRunId = run.RunId,
            ObjectMappings = run.IdentifierMappings,
            Artifacts = run.Artifacts.Select(item => new PackageArtifactManifest(
                item.SourceObjectId,
                item.TargetObjectId.ObjectType,
                item.TargetObjectId.Schema,
                item.TargetObjectId.Name,
                item.DeploymentPhase,
                item.ScriptFileName,
                item.PostgreSqlDefinition,
                item.ContentHash,
                item.Classification,
                item.Dependencies,
                item.RequiredExtensions,
                item.RequiresManualReview,
                item.UnsupportedConstructs,
                -1)
            {
                TargetParentObject = ResolveTargetParent(
                    item.SourceObjectId, item.TargetObjectId.ObjectType, mappings),
                IsExecutable = ConversionArtifactReconciler.IsDeployableExecutable(item),
                LiveValidation = item.Validation
            }).ToArray()
        };
    }

    private static BlockedDependencyArtifactDecision CreateDecision(
        BlockedArtifactAnalysis analysis,
        HashSet<string> deferred)
    {
        ReconciledBlockedClassification classification;
        var fatal = false;
        if (analysis.RemainsBlocked && deferred.Contains(analysis.ArtifactId))
        {
            classification = ReconciledBlockedClassification.DeferredByDeploymentPlan;
        }
        else if (analysis.RemainsBlocked)
        {
            classification = ReconciledBlockedClassification.HardBlocked;
            fatal = true;
        }
        else if (analysis.BlockingDependencies.Any(item =>
                     item.DependencyKind == DeploymentDependencyKind.RuntimeDependency))
        {
            classification = ReconciledBlockedClassification.RuntimeOnly;
        }
        else if (analysis.BlockingDependencies.Any(item =>
                     item.DependencyKind == DeploymentDependencyKind.ManualReviewDependency))
        {
            classification = ReconciledBlockedClassification.ManualReviewDependency;
        }
        else if (analysis.BlockingDependencies.Any(item =>
                     item.DependencyKind == DeploymentDependencyKind.ExternalDependency))
        {
            classification = ReconciledBlockedClassification.ExternalDependency;
        }
        else if (analysis.BlockingDependencies.Any(item =>
                     item.DependencyKind == DeploymentDependencyKind.OptionalCompatibilityDependency))
        {
            classification = ReconciledBlockedClassification.Optional;
        }
        else
        {
            classification = ReconciledBlockedClassification.FalseOrCascadingBlock;
        }

        return new BlockedDependencyArtifactDecision(
            analysis.ArtifactId,
            analysis.SourceObjectId,
            analysis.TargetQualifiedName,
            LiveSqlValidationOutcome.BlockedByDependency,
            classification,
            fatal,
            analysis.BlockingDependencies.Select(item => item.BlockingSourceObjectId.ToString())
                .Order(StringComparer.Ordinal).ToArray(),
            string.Join(" ", analysis.BlockingDependencies.Select(item => item.Reason)));
    }

    private static string ResolveTargetParent(
        InventoryObjectId sourceObjectId,
        string objectType,
        Dictionary<InventoryObjectId, IdentifierMappingEntry[]> mappings)
    {
        if (!mappings.TryGetValue(sourceObjectId, out var entries)) return string.Empty;
        return entries.FirstOrDefault(item => string.Equals(
                   item.ObjectType, objectType, StringComparison.OrdinalIgnoreCase))?.TargetParentObject
               ?? entries.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.TargetParentObject))
                   ?.TargetParentObject
               ?? string.Empty;
    }

    private static string ArtifactIdentity(ConversionArtifact artifact) =>
        NodeIdentity(
            artifact.SourceObjectId,
            artifact.TargetObjectId.ObjectType,
            artifact.TargetObjectId.QualifiedName,
            artifact.DeploymentPhase);

    private static string NodeIdentity(
        InventoryObjectId sourceObjectId,
        string objectType,
        string qualifiedName,
        DeploymentPhase phase) =>
        $"{sourceObjectId}|{objectType}|{qualifiedName}|{phase}";
}
