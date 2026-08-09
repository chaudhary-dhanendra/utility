using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.Deployment;

public enum DeploymentDependencyKind
{
    HardDeploymentDependency,
    RuntimeDependency,
    OptionalCompatibilityDependency,
    ExternalDependency,
    ManualReviewDependency,
    PhaseOrderingDependency
}

public enum DeploymentCycleResolution
{
    ResolvableByPhaseSeparation,
    ResolvableByCompatibilityStub,
    RuntimeOnlyCycle,
    ManualReviewCycle,
    HardUnresolvableDeploymentCycle
}

public sealed record DeploymentGraphNode(
    string ArtifactId,
    InventoryObjectId SourceObjectId,
    string TargetQualifiedName,
    string TargetSchema,
    string TargetName,
    string ObjectType,
    DeploymentPhase DeploymentPhase,
    bool IsExecutable,
    bool RequiresManualReview,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Dependents);

/// <summary>
/// An edge points from the dependent artifact to its prerequisite artifact.
/// </summary>
public sealed record DeploymentGraphEdge(
    string FromArtifactId,
    string? ToArtifactId,
    DeploymentDependencyKind DependencyKind,
    bool IsHardBlocking,
    bool IsExternal,
    string Reason,
    InventoryObjectId? ReferencedSourceObjectId = null,
    string? ReferencedTarget = null);

public sealed record DeploymentUnresolvedDependency(
    string FromArtifactId,
    InventoryObjectId? ReferencedSourceObjectId,
    string ReferencedName,
    DeploymentDependencyKind DependencyKind,
    bool IsHardBlocking,
    string Reason);

public sealed record DeploymentStronglyConnectedComponent(
    int ComponentId,
    IReadOnlyList<string> ArtifactIds,
    bool IsCycle,
    DeploymentCycleResolution? Resolution,
    string Reason);

public sealed record DeploymentGraph(
    IReadOnlyList<DeploymentGraphNode> Nodes,
    IReadOnlyList<DeploymentGraphEdge> Edges,
    IReadOnlyList<DeploymentStronglyConnectedComponent> StronglyConnectedComponents,
    IReadOnlyList<DeploymentUnresolvedDependency> UnresolvedDependencies,
    IReadOnlyList<DeploymentUnresolvedDependency> ExternalDependencies,
    IReadOnlyList<string> DuplicateArtifactIds,
    IReadOnlyList<string> DuplicateTargetIdentities);

public sealed record DeploymentPlanStage(
    int StageNumber,
    DeploymentPhase DeploymentPhase,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> TargetNames,
    IReadOnlyList<string> DependencyPrerequisites);

public sealed record DeferredDeploymentArtifact(
    string ArtifactId,
    string TargetQualifiedName,
    DeploymentPhase DeploymentPhase,
    string Reason);

public sealed record DeploymentPlanStatistics(
    int TotalPackageArtifacts,
    int ExecutableNodes,
    int ManualReviewNodes,
    int HardEdges,
    int RuntimeEdges,
    int OptionalEdges,
    int ExternalEdges,
    int ManualReviewEdges,
    int PhaseOrderingEdges,
    int UnresolvedInternalDependencies,
    int StronglyConnectedComponentCount,
    int CycleCount,
    int HardCycleCount,
    int OrderedArtifactCount,
    int DeferredArtifactCount);

public sealed record DeploymentPlan(
    Guid PlanId,
    IReadOnlyList<DeploymentPlanStage> OrderedStages,
    IReadOnlyList<string> OrderedArtifacts,
    IReadOnlyList<DeferredDeploymentArtifact> DeferredArtifacts,
    IReadOnlyList<DeploymentUnresolvedDependency> ExternalDependencies,
    IReadOnlyList<DeploymentGraphEdge> ManualReviewDependencies,
    IReadOnlyList<DeploymentStronglyConnectedComponent> Cycles,
    IReadOnlyList<DeploymentUnresolvedDependency> UnresolvedDependencies,
    DeploymentPlanStatistics Statistics)
{
    public IReadOnlyList<BlockedArtifactAnalysis> PersistedBlockedArtifacts { get; init; } = [];

    public int PersistedBlockedArtifactCount => PersistedBlockedArtifacts.Count;

    public int EffectiveBlockedArtifactCount =>
        PersistedBlockedArtifacts.Count(item => item.RemainsBlocked);
}

public sealed record PersistedBlockedArtifact(
    InventoryObjectId SourceObjectId,
    string TargetQualifiedName,
    IReadOnlyList<InventoryObjectId> BlockingDependencies);

public sealed record BlockingDependencyAnalysis(
    InventoryObjectId BlockingSourceObjectId,
    string BlockingTargetQualifiedName,
    DeploymentDependencyKind DependencyKind,
    bool ContributesToIndegree,
    bool IsFalseDependencyBlock,
    string Reason,
    string SuggestedResolution);

public sealed record BlockedArtifactAnalysis(
    InventoryObjectId SourceObjectId,
    string TargetQualifiedName,
    string ArtifactId,
    bool RemainsBlocked,
    IReadOnlyList<BlockingDependencyAnalysis> BlockingDependencies);

public enum ReconciledBlockedClassification
{
    HardBlocked,
    RuntimeOnly,
    Optional,
    ManualReviewDependency,
    ExternalDependency,
    FalseOrCascadingBlock,
    DeferredByDeploymentPlan
}

public sealed record BlockedDependencyArtifactDecision(
    string ArtifactId,
    InventoryObjectId SourceObjectId,
    string TargetQualifiedName,
    LiveSqlValidationOutcome OriginalStatus,
    ReconciledBlockedClassification ReconciledClassification,
    bool IsFatal,
    IReadOnlyList<string> BlockingArtifactIds,
    string Reason);

public sealed record BlockedDependencyReconciliation(
    int OriginalBlockedCount,
    int HardBlockedCount,
    int NonFatalBlockedCount,
    int RuntimeOnlyCount,
    int OptionalCount,
    int ManualReviewDependencyCount,
    int ExternalDependencyCount,
    int CascadingOrFalseBlockCount,
    int DeferredByPlanCount,
    IReadOnlyList<BlockedDependencyArtifactDecision> ArtifactDecisions)
{
    public int DirectValidationFailureCount { get; init; }

    public int NotRunExecutableCount { get; init; }

    public int HardCycleCount { get; init; }

    public int UnresolvedInternalDependencyCount { get; init; }

    public Guid DeploymentPlanId { get; init; }

    public bool CanPublish =>
        DirectValidationFailureCount == 0 &&
        NotRunExecutableCount == 0 &&
        HardBlockedCount == 0 &&
        HardCycleCount == 0 &&
        UnresolvedInternalDependencyCount == 0;

    public bool HasWarnings => NonFatalBlockedCount > 0;
}
