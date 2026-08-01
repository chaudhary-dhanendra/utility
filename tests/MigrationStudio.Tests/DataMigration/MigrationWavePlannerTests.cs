using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.DataMigration;

namespace MigrationStudio.Tests.DataMigration;

public sealed class MigrationWavePlannerTests
{
    [Fact]
    public void GroupsLargeCyclicAndProgrammableObjectsWithoutChangingInclusion()
    {
        var reference = Object("reference", InventoryObjectType.Table, 1);
        var large = Object("large", InventoryObjectType.Table, 2);
        var cyclic = Object("cyclic", InventoryObjectType.Table, 3);
        var procedure = Object("load_data", InventoryObjectType.StoredProcedure, 4);
        var snapshot = TestInventory.CreateSnapshot([reference, large, cyclic, procedure]) with
        {
            Tables =
            [
                Table(reference.Id, 100, 4096),
                Table(large.Id, 20_000_000, 4L * 1024 * 1024 * 1024),
                Table(cyclic.Id, 5000, 1_000_000)
            ],
            Columns =
            [
                Column(cyclic.Id, "Payload", "nvarchar", -1)
            ],
            DependencyComponents =
            [
                new DependencyComponent(7, [cyclic.Id], true)
            ]
        };

        var plan = new MigrationWavePlanner().CreatePlan(snapshot);

        Assert.Contains(
            plan.Waves.Single(item => item.Kind == MigrationWaveKind.ReferenceData).Items,
            item => item.ObjectId == reference.Id);
        Assert.Contains(
            plan.Waves.Single(item => item.Kind == MigrationWaveKind.LargeTables).Items,
            item => item.ObjectId == large.Id);
        Assert.Contains(
            plan.Waves.Single(item => item.Kind == MigrationWaveKind.CyclicGroups).Items,
            item => item.ObjectId == cyclic.Id && item.HasLargeObjects);
        Assert.Contains(
            plan.Waves.Single(item => item.Kind == MigrationWaveKind.ProgrammableObjects).Items,
            item => item.ObjectId == procedure.Id);
        Assert.Equal(snapshot.Objects.Count + 1, plan.Waves.Sum(item => item.Items.Count));
    }

    private static InventoryObject Object(
        string name,
        InventoryObjectType type,
        int id)
    {
        var objectId = InventoryObjectId.Create("fixture", type, "dbo", name, id);
        return new InventoryObject(
            objectId, "fixture", "dbo", name, $"[dbo].[{name}]", type, id, null, null, null,
            false, true, SelectionReason.CompleteDatabase, 0, 0, [],
            ConversionClassification.Automatic, null, null, $"hash-{id}", [],
            DiscoveryStatus.Discovered);
    }

    private static TableInventory Table(InventoryObjectId id, long rows, long bytes) =>
        new(id, TableKind.Ordinary, false, null, false, 0, null, false, false, false,
            false, false, false, false, rows, bytes, bytes, []);

    private static ColumnInventory Column(
        InventoryObjectId table,
        string name,
        string type,
        short length)
    {
        var id = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Column, "dbo", name, 1, table);
        return new ColumnInventory(
            id, table, 1, 1, name, type, type, "sys", length, 0, 0, null,
            true, false, null, null, null, false, false, null, false, null,
            false, false, false, false, 0, false, false, null, null, null, null,
            null, null, null, null, []);
    }
}
