using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Domain;

public sealed class DependencyGraphAnalyzerTests
{
    [Fact]
    public void FindsCycles_AndLeavesUnresolvedEdgesOutsideComponents()
    {
        var first = InventoryObjectId.Create("db", InventoryObjectType.View, "dbo", "a", 1);
        var second = InventoryObjectId.Create("db", InventoryObjectType.View, "dbo", "b", 2);
        var third = InventoryObjectId.Create("db", InventoryObjectType.View, "dbo", "c", 3);
        var edges = new[]
        {
            Edge(first, second),
            Edge(second, first),
            new InventoryDependency(third, null, DependencyKind.SqlExpression, "other.missing", false, false)
        };

        var components = DependencyGraphAnalyzer.FindStronglyConnectedComponents([first, second, third], edges);
        var assigned = DependencyGraphAnalyzer.AssignComponents(edges, components);

        var cycle = Assert.Single(components, item => item.IsCycle);
        Assert.Equal(2, cycle.Members.Count);
        Assert.Equal(cycle.Id, assigned[0].StronglyConnectedComponent);
        Assert.Null(assigned[2].StronglyConnectedComponent);
    }

    [Fact]
    public void LongDependencyChain_DoesNotUseProcessStackRecursion()
    {
        const int objectCount = 10_000;
        var objects = Enumerable.Range(0, objectCount)
            .Select(index => InventoryObjectId.Create(
                "scale", InventoryObjectType.View, "dbo", $"view_{index}", index))
            .ToArray();
        var edges = Enumerable.Range(0, objectCount - 1)
            .Select(index => Edge(objects[index], objects[index + 1]))
            .ToArray();

        var components = DependencyGraphAnalyzer.FindStronglyConnectedComponents(objects, edges);

        Assert.Equal(objectCount, components.Count);
        Assert.DoesNotContain(components, component => component.IsCycle);
    }

    [Fact]
    public void ComponentOrdering_IsIndependentOfInputOrdering()
    {
        var objects = Enumerable.Range(0, 200)
            .Select(index => InventoryObjectId.Create(
                "deterministic", InventoryObjectType.View, "dbo", $"view_{index}", index))
            .ToArray();
        var edges = Enumerable.Range(0, objects.Length)
            .Select(index => Edge(objects[index], objects[(index + 1) % objects.Length]))
            .ToArray();

        var forward = DependencyGraphAnalyzer.FindStronglyConnectedComponents(objects, edges);
        var reverse = DependencyGraphAnalyzer.FindStronglyConnectedComponents(
            objects.Reverse(), edges.Reverse());

        Assert.Equal(
            forward.Select(item => (item.Id, item.IsCycle, Members: string.Join(",", item.Members))),
            reverse.Select(item => (item.Id, item.IsCycle, Members: string.Join(",", item.Members))));
    }

    [Fact]
    public void Cancellation_IsObservedBeforeGraphAllocation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            DependencyGraphAnalyzer.FindStronglyConnectedComponents(
                [InventoryObjectId.Create("cancel", InventoryObjectType.View, "dbo", "v", 1)],
                [],
                cancellation.Token));
    }

    private static InventoryDependency Edge(InventoryObjectId source, InventoryObjectId target) =>
        new(source, target, DependencyKind.SqlExpression, "resolved", true, false);
}
