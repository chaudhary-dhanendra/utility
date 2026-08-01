using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.SqlServer;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class InventoryDependencyResolutionTests
{
    [Fact]
    public void ResolvesNameOnlyExpressionDependencyAgainstReferencingSchema()
    {
        var accumulator = new InventoryAccumulator("source");
        var function = accumulator.AddObject(
            101,
            0,
            "nrega_sk",
            "fnchksau_dupacc",
            InventoryObjectType.Function,
            null,
            null,
            false,
            "RETURN 0",
            DiscoveryStatus.Discovered);
        accumulator.AddObject(
            102,
            0,
            "nrega_sk",
            "chksau_dupacc",
            InventoryObjectType.CheckConstraint,
            null,
            null,
            false,
            "fnchksau_dupacc([Acc_No]) = 0",
            DiscoveryStatus.Discovered);

        var resolved = accumulator.TryResolveObjectId(
            schema: null,
            entity: "fnchksau_dupacc",
            referencingSchema: "nrega_sk",
            out var ambiguous);

        Assert.False(ambiguous);
        Assert.Equal(function.Id, resolved);
    }

    [Fact]
    public void DoesNotGuessAcrossAmbiguousSchemas()
    {
        var accumulator = new InventoryAccumulator("source");
        accumulator.AddObject(
            101, 0, "one", "same_name", InventoryObjectType.Function,
            null, null, false, "RETURN 1", DiscoveryStatus.Discovered);
        accumulator.AddObject(
            102, 0, "two", "same_name", InventoryObjectType.Function,
            null, null, false, "RETURN 2", DiscoveryStatus.Discovered);

        var resolved = accumulator.TryResolveObjectId(
            schema: null,
            entity: "same_name",
            referencingSchema: null,
            out var ambiguous);

        Assert.Null(resolved);
        Assert.True(ambiguous);
    }
}
