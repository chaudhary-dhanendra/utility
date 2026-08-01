using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Conversion;

public sealed class ConversionInventoryIndex
{
    private ConversionInventoryIndex(InventorySnapshot inventory)
    {
        TablesByObjectId = IndexByObjectId(inventory.Tables, item => item.ObjectId);
        ColumnsByObjectId = IndexByObjectId(inventory.Columns, item => item.ObjectId);
        ColumnsByParentObjectId = inventory.Columns
            .GroupBy(item => item.ParentObjectId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ColumnInventory>)group
                    .OrderBy(item => item.OrdinalPosition)
                    .ToArray());
        ConstraintsByObjectId =
            IndexByObjectId(inventory.Constraints, item => item.ObjectId);
        IndexesByObjectId = IndexByObjectId(inventory.Indexes, item => item.ObjectId);
        ModulesByObjectId = IndexByObjectId(inventory.Modules, item => item.ObjectId);
        SequencesByObjectId = IndexByObjectId(inventory.Sequences, item => item.ObjectId);
        UserDefinedTypesByObjectId =
            IndexByObjectId(inventory.UserDefinedTypes, item => item.ObjectId);
        SynonymsByObjectId = IndexByObjectId(inventory.Synonyms, item => item.ObjectId);
        SecurityPrincipalsByObjectId =
            IndexByObjectId(inventory.SecurityPrincipals, item => item.ObjectId);
        PermissionsByObjectId =
            IndexByObjectId(inventory.Permissions, item => item.ObjectId);
        TriggersByObjectId = IndexByObjectId(inventory.Triggers, item => item.ObjectId);
    }

    public IReadOnlyDictionary<InventoryObjectId, TableInventory> TablesByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, ColumnInventory> ColumnsByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, IReadOnlyList<ColumnInventory>>
        ColumnsByParentObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, ConstraintInventory>
        ConstraintsByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, IndexInventory> IndexesByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, ModuleInventory> ModulesByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, SequenceInventory> SequencesByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, UserDefinedTypeInventory>
        UserDefinedTypesByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, SynonymInventory> SynonymsByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, SecurityPrincipalInventory>
        SecurityPrincipalsByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, PermissionInventory>
        PermissionsByObjectId { get; }

    public IReadOnlyDictionary<InventoryObjectId, TriggerInventory> TriggersByObjectId { get; }

    public static ConversionInventoryIndex Create(InventorySnapshot inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        return new ConversionInventoryIndex(inventory);
    }

    private static Dictionary<InventoryObjectId, T> IndexByObjectId<T>(
        IEnumerable<T> items,
        Func<T, InventoryObjectId> keySelector) =>
        items.GroupBy(keySelector).ToDictionary(group => group.Key, group => group.First());
}
