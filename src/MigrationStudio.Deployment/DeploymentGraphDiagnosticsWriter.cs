using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Deployment;

public static class DeploymentGraphDiagnosticsWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(
        DeploymentGraph graph,
        DeploymentPlan plan,
        string outputDirectory,
        IReadOnlySet<InventoryObjectId>? currentlyBlockedSourceObjects = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        await WriteJsonAsync(Path.Combine(directory, "deployment-graph.json"), graph, cancellationToken)
            .ConfigureAwait(false);
        await WriteJsonAsync(Path.Combine(directory, "deployment-plan.json"), plan, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "deployment-plan.md"),
            BuildPlanMarkdown(graph, plan, currentlyBlockedSourceObjects),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "dependency-cycles.md"),
            BuildCyclesMarkdown(plan),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string BuildPlanMarkdown(
        DeploymentGraph graph,
        DeploymentPlan plan,
        IReadOnlySet<InventoryObjectId>? blocked)
    {
        var stats = plan.Statistics;
        var builder = new StringBuilder();
        builder.AppendLine("# Deterministic deployment plan");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Plan ID: `{plan.PlanId}`");
        builder.AppendLine();
        builder.AppendLine("## Statistics");
        builder.AppendLine();
        builder.AppendLine("| Metric | Count |");
        builder.AppendLine("|---|---:|");
        Append(builder, "Total package artifacts", stats.TotalPackageArtifacts);
        Append(builder, "Executable nodes", stats.ExecutableNodes);
        Append(builder, "Manual-review nodes", stats.ManualReviewNodes);
        Append(builder, "Hard edges", stats.HardEdges);
        Append(builder, "Runtime edges", stats.RuntimeEdges);
        Append(builder, "Optional edges", stats.OptionalEdges);
        Append(builder, "External edges", stats.ExternalEdges);
        Append(builder, "Manual-review edges", stats.ManualReviewEdges);
        Append(builder, "Phase-ordering edges", stats.PhaseOrderingEdges);
        Append(builder, "Unresolved internal dependencies", stats.UnresolvedInternalDependencies);
        Append(builder, "Strongly connected components", stats.StronglyConnectedComponentCount);
        Append(builder, "Cycles", stats.CycleCount);
        Append(builder, "Hard cycles", stats.HardCycleCount);
        Append(builder, "Ordered artifacts", stats.OrderedArtifactCount);
        Append(builder, "Deferred artifacts", stats.DeferredArtifactCount);
        Append(builder, "Persisted dependency-blocked artifacts", plan.PersistedBlockedArtifactCount);
        Append(builder, "Effective artifact-level blocked artifacts", plan.EffectiveBlockedArtifactCount);

        builder.AppendLine();
        builder.AppendLine("## Dependency classifications");
        builder.AppendLine();
        builder.AppendLine("Hard edges are creation-time prerequisites. Runtime, optional, external, and manual-review edges are retained for diagnostics but do not increase topological indegree. Phase-ordering edges represent package ownership/phase requirements and are hard only where the contained artifact cannot exist without its owner.");

        builder.AppendLine();
        builder.AppendLine("## Integrated architecture");
        builder.AppendLine();
        builder.AppendLine("- `MigrationPackageWriter` creates and publishes the manifest; `PackageArtifactManifest` is the authoritative artifact/dependency record and retains target mappings.");
        builder.AppendLine("- `InventorySnapshot` retains discovered source dependencies; `DeploymentPhase` and `DeploymentPhaseOrdering` define authoritative phase precedence.");
        builder.AppendLine("- `ArtifactDependencyPlanner` remains the legacy source-object ordering utility. `DeploymentGraphPlanner` adds package-artifact identities, typed edges, SCC records, and deterministic plan stages without changing execution behavior.");
        builder.AppendLine("- `PreDeploymentAssessmentService` selects and assesses artifacts; `PostgreSqlDeploymentEngine` executes and journals them; `GeneratedSqlValidator` currently assigns live dependency-blocked outcomes.");
        builder.AppendLine("- `MigrationPackageReader` verifies package hashes before deployment. No retry executor or workflow-completion behavior is changed by this plan.");

        builder.AppendLine();
        builder.AppendLine("## Ordered stages");
        builder.AppendLine();
        builder.AppendLine("| Stage | Phase | Artifacts | Prerequisites |");
        builder.AppendLine("|---:|---|---:|---:|");
        foreach (var stage in plan.OrderedStages)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {stage.StageNumber} | {stage.DeploymentPhase} | {stage.ArtifactIds.Count:N0} | {stage.DependencyPrerequisites.Count:N0} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Top dependency roots");
        builder.AppendLine();
        builder.AppendLine("| Target | Dependents | Phase | Executable |");
        builder.AppendLine("|---|---:|---|---|");
        foreach (var node in graph.Nodes.OrderByDescending(item => item.Dependents.Count)
                     .ThenBy(item => item.TargetQualifiedName, StringComparer.Ordinal).Take(25))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"| `{Escape(node.TargetQualifiedName)}` | {node.Dependents.Count:N0} | {node.DeploymentPhase} | {node.IsExecutable} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Currently dependency-blocked artifacts");
        builder.AppendLine();
        var blockedNodes = blocked is null
            ? []
            : graph.Nodes.Where(item => blocked.Contains(item.SourceObjectId))
                .OrderBy(item => item.TargetQualifiedName, StringComparer.Ordinal).ToArray();
        if (blockedNodes.Length == 0)
        {
            builder.AppendLine("None were supplied by the associated run history.");
        }
        else
        {
            foreach (var node in blockedNodes)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Escape(node.TargetQualifiedName)}` (`{node.ArtifactId}`)");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Persisted blocker classification");
        builder.AppendLine();
        builder.AppendLine("| Artifact | Blocking dependency | Type | Indegree | False block | Reason | Suggested resolution |");
        builder.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var artifact in plan.PersistedBlockedArtifacts)
        {
            foreach (var dependency in artifact.BlockingDependencies)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{Escape(artifact.TargetQualifiedName)}` | `{Escape(dependency.BlockingTargetQualifiedName)}` | {dependency.DependencyKind} | {dependency.ContributesToIndegree} | {dependency.IsFalseDependencyBlock} | {Escape(dependency.Reason)} | {Escape(dependency.SuggestedResolution)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Deferred artifacts");
        builder.AppendLine();
        if (plan.DeferredArtifacts.Count == 0)
        {
            builder.AppendLine("None.");
        }
        else
        {
            foreach (var item in plan.DeferredArtifacts)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Escape(item.TargetQualifiedName)}`: {item.Reason}");
            }
        }
        return builder.ToString();
    }

    private static string BuildCyclesMarkdown(DeploymentPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Dependency cycles");
        builder.AppendLine();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Detected cycles: {plan.Cycles.Count:N0}; hard unresolvable cycles: {plan.Statistics.HardCycleCount:N0}.");
        builder.AppendLine();
        if (plan.Cycles.Count == 0)
        {
            builder.AppendLine("No strongly connected dependency cycles were detected.");
            return builder.ToString();
        }

        foreach (var cycle in plan.Cycles)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"## Component {cycle.ComponentId}: {cycle.Resolution}");
            builder.AppendLine();
            builder.AppendLine(cycle.Reason);
            builder.AppendLine();
            foreach (var artifactId in cycle.ArtifactIds)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{artifactId}`");
            }
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string label, int count) =>
        builder.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {count:N0} |");

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
