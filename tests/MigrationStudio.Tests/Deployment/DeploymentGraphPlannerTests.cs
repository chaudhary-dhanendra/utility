using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using System.IO;

namespace MigrationStudio.Tests.Deployment;

public sealed class DeploymentGraphPlannerTests
{
    [Fact]
    public void LinearChain_IsOrderedByHardDependencies()
    {
        var table = Artifact("table", DeploymentPhase.Tables);
        var index = Artifact("index", DeploymentPhase.Indexes, dependencies: [table.SourceObjectId]);
        var view = Artifact("view", DeploymentPhase.Views, dependencies: [index.SourceObjectId]);

        var (graph, plan) = Build(view, index, table);

        AssertBefore(plan, graph, table, index);
        AssertBefore(plan, graph, index, view);
        Assert.Equal(3, plan.Statistics.OrderedArtifactCount);
        Assert.Equal(0, plan.Statistics.DeferredArtifactCount);
    }

    [Fact]
    public void IndependentChainsAndUnrelatedNodes_HaveStableDeterministicOrder()
    {
        var a = Artifact("a", DeploymentPhase.Tables);
        var b = Artifact("b", DeploymentPhase.Tables, dependencies: [a.SourceObjectId]);
        var c = Artifact("c", DeploymentPhase.Tables);
        var d = Artifact("d", DeploymentPhase.Tables, dependencies: [c.SourceObjectId]);
        var (graph, first) = Build(d, b, c, a);
        var second = Build(a, c, b, d).Plan;

        Assert.Equal(first.PlanId, second.PlanId);
        Assert.Equal(first.OrderedArtifacts, second.OrderedArtifacts);
        AssertBefore(first, graph, a, b);
        AssertBefore(first, graph, c, d);
    }

    [Fact]
    public void SamePhaseAndCrossPhaseDependencies_OverridePreferredPhaseOnlyWhenRequired()
    {
        var function = Artifact("late_function", DeploymentPhase.Functions, type: "Function");
        var table = Artifact("early_table", DeploymentPhase.Tables, dependencies: [function.SourceObjectId]);
        var index = Artifact("same_phase_z", DeploymentPhase.Indexes);
        var otherIndex = Artifact("same_phase_a", DeploymentPhase.Indexes, dependencies: [index.SourceObjectId]);

        var (graph, plan) = Build(table, otherIndex, index, function);

        AssertBefore(plan, graph, function, table);
        AssertBefore(plan, graph, index, otherIndex);
    }

    [Fact]
    public void RuntimeCycle_DoesNotInflateBlockedOrDeferredCounts()
    {
        var firstId = Id("runtime-first");
        var secondId = Id("runtime-second");
        var first = Artifact("first", DeploymentPhase.Procedures, firstId, [secondId], "Procedure");
        var second = Artifact("second", DeploymentPhase.Procedures, secondId, [firstId], "Procedure");

        var (graph, plan) = Build(first, second);

        Assert.All(graph.Edges, edge => Assert.Equal(DeploymentDependencyKind.RuntimeDependency, edge.DependencyKind));
        Assert.Equal(DeploymentCycleResolution.RuntimeOnlyCycle, Assert.Single(plan.Cycles).Resolution);
        Assert.Equal(2, plan.Statistics.OrderedArtifactCount);
        Assert.Equal(0, plan.Statistics.DeferredArtifactCount);
    }

    [Fact]
    public void MixedProgrammableCycle_IsClassifiedForCompatibilityStubResolution()
    {
        var functionId = Id("stub-function");
        var viewId = Id("stub-view");
        var function = Artifact("stub_function", DeploymentPhase.Functions, functionId, [viewId], "Function");
        var view = Artifact("stub_view", DeploymentPhase.Views, viewId, [functionId], "View");

        var (_, plan) = Build(function, view);

        Assert.Equal(DeploymentCycleResolution.ResolvableByCompatibilityStub,
            Assert.Single(plan.Cycles).Resolution);
    }

    [Fact]
    public void ForeignKeyCycle_IsClassifiedAsPhaseSeparable()
    {
        var firstId = Id("fk-first");
        var secondId = Id("fk-second");
        var first = Artifact("fk_first", DeploymentPhase.ForeignKeys, firstId, [secondId], "ForeignKey");
        var second = Artifact("fk_second", DeploymentPhase.ForeignKeys, secondId, [firstId], "ForeignKey");

        var (_, plan) = Build(first, second);

        Assert.Equal(DeploymentCycleResolution.ResolvableByPhaseSeparation, Assert.Single(plan.Cycles).Resolution);
        Assert.Equal(2, plan.Statistics.DeferredArtifactCount);
    }

    [Fact]
    public void ManualExternalOptionalDependencies_DoNotBlockUnrelatedArtifacts()
    {
        var manual = Artifact("manual", DeploymentPhase.Procedures, manual: true, type: "Procedure");
        var trace = Artifact("trace", DeploymentPhase.Indexes, executable: false, type: "Index");
        var dependent = Artifact("dependent", DeploymentPhase.Procedures,
            dependencies: [manual.SourceObjectId, trace.SourceObjectId], extensions: ["postgis"], type: "Procedure");
        var unrelated = Artifact("unrelated", DeploymentPhase.Tables);

        var (graph, plan) = Build(manual, trace, dependent, unrelated);

        Assert.Contains(graph.Edges, edge => edge.DependencyKind == DeploymentDependencyKind.ManualReviewDependency);
        Assert.Contains(graph.Edges, edge => edge.DependencyKind == DeploymentDependencyKind.OptionalCompatibilityDependency);
        Assert.Contains(graph.Edges, edge => edge.DependencyKind == DeploymentDependencyKind.ExternalDependency);
        Assert.Equal(2, plan.Statistics.OrderedArtifactCount);
        Assert.Equal(0, plan.Statistics.DeferredArtifactCount);
    }

    [Fact]
    public void ManualReviewCycle_IsReportedWithoutBlockingExecutableMember()
    {
        var tableId = Id("manual-cycle-procedure");
        var manualId = Id("manual-cycle-manual");
        var table = Artifact("cycle_procedure", DeploymentPhase.Procedures, tableId, [manualId], "Procedure");
        var manual = Artifact("cycle_manual", DeploymentPhase.Procedures, manualId, [tableId],
            "Procedure", manual: true);

        var (_, plan) = Build(table, manual);

        Assert.Equal(DeploymentCycleResolution.ManualReviewCycle, Assert.Single(plan.Cycles).Resolution);
        Assert.Single(plan.OrderedArtifacts);
        Assert.Empty(plan.DeferredArtifacts);
    }

    [Fact]
    public void CreationTimeDependencyOnManualArtifactRemainsDeferred()
    {
        var function = Artifact("manual_function", DeploymentPhase.Functions,
            manual: true, type: "Function");
        var check = Artifact("check", DeploymentPhase.CheckConstraints,
            dependencies: [function.SourceObjectId], type: "CheckConstraint");

        var (graph, plan) = Build(check, function);

        var edge = Assert.Single(graph.Edges, item =>
            item.FromArtifactId == NodeFor(graph, check).ArtifactId &&
            item.ToArtifactId == NodeFor(graph, function).ArtifactId);
        Assert.Equal(DeploymentDependencyKind.HardDeploymentDependency, edge.DependencyKind);
        Assert.True(edge.IsHardBlocking);
        Assert.Single(plan.DeferredArtifacts);
        Assert.Empty(plan.OrderedArtifacts);
    }

    [Fact]
    public void MissingInternalDependency_DefersOnlyItsHardDependent()
    {
        var missing = Id("missing");
        var dependent = Artifact("dependent", DeploymentPhase.Views, dependencies: [missing], type: "View");
        var unrelated = Artifact("unrelated", DeploymentPhase.Tables);

        var (graph, plan) = Build(dependent, unrelated);

        Assert.Single(graph.UnresolvedDependencies);
        Assert.Single(plan.DeferredArtifacts);
        Assert.Single(plan.OrderedArtifacts);
        Assert.Contains(plan.OrderedArtifacts, id => NodeFor(graph, unrelated).ArtifactId == id);
    }

    [Fact]
    public void TargetIdentifierMapping_ResolvesDependencyWhenSourceArtifactIdIsAbsent()
    {
        var mappedSource = Id("mapped-source");
        var table = Artifact("mapped_table", DeploymentPhase.Tables);
        var view = Artifact("mapped_view", DeploymentPhase.Views, dependencies: [mappedSource], type: "View");
        var mapping = new IdentifierMappingEntry(
            mappedSource, "Table", "dbo", "source_table", "dbo.source_table",
            "app", "mapped_table", "app.mapped_table", 12, 12, false, false, null, "test");
        var manifest = new MigrationPackageManifest
        {
            Artifacts = [view, table],
            ObjectMappings = [mapping]
        };

        var (graph, plan) = DeploymentGraphPlanner.Build(manifest);

        Assert.Empty(graph.UnresolvedDependencies);
        AssertBefore(plan, graph, table, view);
    }

    [Fact]
    public void FunctionTableAndCheckFunctionDependenciesAreHardButProcedureFunctionIsRuntime()
    {
        var table = Artifact("table", DeploymentPhase.Tables);
        var function = Artifact("function", DeploymentPhase.Functions,
            dependencies: [table.SourceObjectId], type: "Function");
        var check = Artifact("check", DeploymentPhase.CheckConstraints,
            dependencies: [function.SourceObjectId], type: "CheckConstraint");
        var procedure = Artifact("procedure", DeploymentPhase.Procedures,
            dependencies: [function.SourceObjectId], type: "Procedure");

        var (graph, _) = Build(procedure, check, function, table);
        var functionNode = NodeFor(graph, function);
        var tableNode = NodeFor(graph, table);
        var checkNode = NodeFor(graph, check);
        var procedureNode = NodeFor(graph, procedure);

        Assert.Contains(graph.Edges, edge => edge.FromArtifactId == functionNode.ArtifactId &&
                                             edge.ToArtifactId == tableNode.ArtifactId &&
                                             edge.DependencyKind == DeploymentDependencyKind.HardDeploymentDependency);
        Assert.Contains(graph.Edges, edge => edge.FromArtifactId == checkNode.ArtifactId &&
                                             edge.ToArtifactId == functionNode.ArtifactId &&
                                             edge.DependencyKind == DeploymentDependencyKind.HardDeploymentDependency);
        Assert.Contains(graph.Edges, edge => edge.FromArtifactId == procedureNode.ArtifactId &&
                                             edge.ToArtifactId == functionNode.ArtifactId &&
                                             edge.DependencyKind == DeploymentDependencyKind.RuntimeDependency &&
                                             !edge.IsHardBlocking);
    }

    [Fact]
    public void PersistedSourceLevelBlockIsClearedWhenHardPrerequisiteIsOrdered()
    {
        var table = Artifact("table", DeploymentPhase.Tables);
        var index = Artifact("index", DeploymentPhase.Indexes, dependencies: [table.SourceObjectId], type: "Index");
        var (graph, initialPlan) = Build(index, table);
        var analysis = BlockedDependencyAnalyzer.Analyze(
            graph,
            initialPlan,
            [new PersistedBlockedArtifact(index.SourceObjectId, "app.index", [table.SourceObjectId])]);
        var plan = initialPlan with { PersistedBlockedArtifacts = analysis };

        var blocked = Assert.Single(plan.PersistedBlockedArtifacts);
        var dependency = Assert.Single(blocked.BlockingDependencies);
        Assert.Equal(DeploymentDependencyKind.HardDeploymentDependency, dependency.DependencyKind);
        Assert.True(dependency.ContributesToIndegree);
        Assert.True(dependency.IsFalseDependencyBlock);
        Assert.False(blocked.RemainsBlocked);
        Assert.Equal(0, plan.EffectiveBlockedArtifactCount);
    }

    [Fact]
    public void GeneratedOwnerAndRelationScopedArtifacts_AreOrderedAfterOwners()
    {
        var ownerId = Id("owner");
        var table = Artifact("orders", DeploymentPhase.Tables, ownerId);
        var helper = Artifact("orders_helper", DeploymentPhase.Functions, ownerId, type: "Function");
        var index = Artifact("orders_ix", DeploymentPhase.Indexes, type: "Index") with
        {
            TargetParentObject = "app.orders"
        };

        var (graph, plan) = Build(index, helper, table);

        AssertBefore(plan, graph, table, helper);
        AssertBefore(plan, graph, table, index);
        Assert.Contains(graph.Edges, edge => edge.DependencyKind == DeploymentDependencyKind.PhaseOrderingDependency);
    }

    [Theory]
    [InlineData(DeploymentPhase.PrimaryKeys, "PrimaryKey")]
    [InlineData(DeploymentPhase.UniqueConstraints, "UniqueConstraint")]
    [InlineData(DeploymentPhase.ForeignKeys, "ForeignKey")]
    [InlineData(DeploymentPhase.Indexes, "Index")]
    public void TablePrecedesRelationScopedArtifacts(DeploymentPhase phase, string type)
    {
        var table = Artifact("orders", DeploymentPhase.Tables);
        var child = Artifact($"orders_{type}", phase, type: type) with { TargetParentObject = "app.orders" };

        var (graph, plan) = Build(child, table);

        AssertBefore(plan, graph, table, child);
    }

    [Fact]
    public void SchemaPrecedesContainedObjects()
    {
        var schema = Artifact("app", DeploymentPhase.Schemas, type: "Schema");
        var table = Artifact("orders", DeploymentPhase.Tables);

        var (graph, plan) = Build(table, schema);

        AssertBefore(plan, graph, schema, table);
        Assert.Contains(graph.Edges, edge => edge.DependencyKind == DeploymentDependencyKind.PhaseOrderingDependency);
    }

    [Fact]
    public void TriggerFunctionPrecedesTrigger()
    {
        var function = Artifact("audit_fn", DeploymentPhase.Functions, type: "Function");
        var trigger = Artifact("audit_trg", DeploymentPhase.Triggers,
            dependencies: [function.SourceObjectId], type: "Trigger");

        var (graph, plan) = Build(trigger, function);

        AssertBefore(plan, graph, function, trigger);
    }

    [Fact]
    public void HardCycleAndDuplicateIdentity_AreDetected()
    {
        var firstId = Id("hard-first");
        var secondId = Id("hard-second");
        var first = Artifact("first", DeploymentPhase.Tables, firstId, [secondId]);
        var second = Artifact("second", DeploymentPhase.Tables, secondId, [firstId]);
        var duplicate = first with { SourceObjectId = Id("duplicate") };

        var (graph, plan) = Build(first, second, duplicate);

        Assert.Equal(DeploymentCycleResolution.HardUnresolvableDeploymentCycle,
            Assert.Single(plan.Cycles).Resolution);
        Assert.Single(graph.DuplicateTargetIdentities);
        Assert.True(plan.Statistics.HardCycleCount > 0);
    }

    [Fact]
    public void BranchingDirectedAcyclicGraph_DoesNotProduceFalseComponents()
    {
        var root = Artifact("root", DeploymentPhase.Tables);
        var left = Artifact("left", DeploymentPhase.Tables, dependencies: [root.SourceObjectId]);
        var right = Artifact("right", DeploymentPhase.Tables, dependencies: [root.SourceObjectId]);
        var leaf = Artifact("leaf", DeploymentPhase.Views,
            dependencies: [left.SourceObjectId, right.SourceObjectId], type: "View");

        var (_, plan) = Build(leaf, right, left, root);

        Assert.Empty(plan.Cycles);
        Assert.Equal(4, plan.Statistics.OrderedArtifactCount);
    }

    [Fact]
    public void EveryManifestArtifactHasOneNodeAndExecutableArtifactOnePlanDisposition()
    {
        var artifacts = new[]
        {
            Artifact("table", DeploymentPhase.Tables),
            Artifact("view", DeploymentPhase.Views, type: "View"),
            Artifact("manual", DeploymentPhase.Procedures, manual: true, type: "Procedure"),
            Artifact("trace", DeploymentPhase.Indexes, executable: false, type: "Index")
        };

        var (graph, plan) = Build(artifacts);

        Assert.Equal(artifacts.Length, graph.Nodes.Count);
        Assert.Equal(2, plan.OrderedArtifacts.Count + plan.DeferredArtifacts.Count);
        Assert.Equal(graph.Nodes.Count, graph.Nodes.Select(item => item.ArtifactId).Distinct().Count());
    }

    [Fact]
    public void DuplicateArtifactId_IsRejectedExplicitly()
    {
        var artifact = Artifact("duplicate", DeploymentPhase.Tables);
        var manifest = new MigrationPackageManifest { Artifacts = [artifact, artifact] };

        var exception = Assert.Throws<InvalidDataException>(() => DeploymentGraphPlanner.Build(manifest));

        Assert.Contains("duplicate artifact IDs", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiagnosticsWriter_ProducesSerializableGraphPlanAndCycleReports()
    {
        var table = Artifact("table", DeploymentPhase.Tables);
        var index = Artifact("index", DeploymentPhase.Indexes, dependencies: [table.SourceObjectId]);
        var (graph, plan) = Build(index, table);
        var directory = Path.Combine(Path.GetTempPath(), $"migrationstudio-graph-{Guid.NewGuid():N}");
        try
        {
            await DeploymentGraphDiagnosticsWriter.WriteAsync(graph, plan, directory);

            var expected = new[]
            {
                "deployment-graph.json",
                "deployment-plan.json",
                "deployment-plan.md",
                "dependency-cycles.md"
            };
            Assert.All(expected, file => Assert.True(File.Exists(Path.Combine(directory, file)), file));
            using var graphDocument = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "deployment-graph.json")));
            using var planDocument = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(directory, "deployment-plan.json")));
            Assert.Equal(2, graphDocument.RootElement.GetProperty("nodes").GetArrayLength());
            Assert.Equal(2, planDocument.RootElement.GetProperty("orderedArtifacts").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static (DeploymentGraph Graph, DeploymentPlan Plan) Build(params PackageArtifactManifest[] artifacts) =>
        DeploymentGraphPlanner.Build(new MigrationPackageManifest { Artifacts = artifacts });

    private static PackageArtifactManifest Artifact(
        string name,
        DeploymentPhase phase,
        InventoryObjectId? id = null,
        IReadOnlyList<InventoryObjectId>? dependencies = null,
        string type = "Table",
        bool manual = false,
        bool executable = true,
        IReadOnlyList<string>? extensions = null) =>
        new(
            id ?? Id(name),
            type,
            "app",
            name,
            phase,
            $"{(int)phase:00}.sql",
            "SELECT 1;",
            $"hash-{name}",
            manual ? ConversionClassification.ManualConversion : ConversionClassification.Automatic,
            dependencies ?? [],
            extensions ?? [],
            manual,
            [],
            -1)
        {
            IsExecutable = executable
        };

    private static InventoryObjectId Id(string value) =>
        new(new Guid(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))[..16]));

    private static DeploymentGraphNode NodeFor(DeploymentGraph graph, PackageArtifactManifest artifact) =>
        Assert.Single(graph.Nodes, item => item.SourceObjectId == artifact.SourceObjectId &&
                                           item.TargetName == artifact.TargetName);

    private static void AssertBefore(
        DeploymentPlan plan,
        DeploymentGraph graph,
        PackageArtifactManifest prerequisite,
        PackageArtifactManifest dependent)
    {
        var prerequisiteId = NodeFor(graph, prerequisite).ArtifactId;
        var dependentId = NodeFor(graph, dependent).ArtifactId;
        Assert.True(plan.OrderedArtifacts.IndexOf(prerequisiteId) < plan.OrderedArtifacts.IndexOf(dependentId));
    }
}

internal static class DeploymentPlanTestExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal)) return index;
        }
        return -1;
    }
}
