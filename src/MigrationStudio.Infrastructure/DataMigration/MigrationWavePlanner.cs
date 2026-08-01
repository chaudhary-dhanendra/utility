using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class MigrationWavePlanner : IMigrationWavePlanner
{
    private const long LargeTableBytes = 1L * 1024 * 1024 * 1024;
    private const long LargeTableRows = 10_000_000;

    public MigrationWavePlan CreatePlan(InventorySnapshot inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var tables = inventory.Tables.ToDictionary(item => item.ObjectId);
        var objects = inventory.Objects.Where(item => item.IsIncluded && !item.IsSystemObject).ToArray();
        var dependencyCounts = inventory.Dependencies
            .Where(item => item.IsResolved && item.TargetObjectId is not null)
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.Count());
        var componentByObject = inventory.DependencyComponents
            .Where(item => item.IsCycle)
            .SelectMany(item => item.Members.Select(member => (member, item.Id)))
            .ToDictionary(item => item.member, item => item.Id);
        var lobTables = inventory.Columns
            .Where(item => item.SystemTypeName is "text" or "ntext" or "image" ||
                           item.MaximumLength == -1)
            .Select(item => item.ParentObjectId)
            .ToHashSet();

        var groups = Enum.GetValues<MigrationWaveKind>().ToDictionary(
            kind => kind,
            _ => new List<MigrationWaveItem>());
        foreach (var item in objects)
        {
            tables.TryGetValue(item.Id, out var table);
            var component = componentByObject.GetValueOrDefault(item.Id, -1);
            var wave = Classify(item, table, dependencyCounts.GetValueOrDefault(item.Id), component);
            groups[wave].Add(new MigrationWaveItem(
                item.Id,
                item.QualifiedSourceName,
                item.ObjectType,
                table?.RowCountEstimate ?? 0,
                table?.ReservedBytes ?? 0,
                lobTables.Contains(item.Id),
                component,
                Risk(item, table, lobTables.Contains(item.Id), component)));
        }

        groups[MigrationWaveKind.Validation].Add(new MigrationWaveItem(
            default, "Post-migration validation", InventoryObjectType.Database, 0, 0, false, -1,
            "Validate after every executable wave and run full reconciliation at completion."));

        var waves = groups
            .OrderBy(item => item.Key)
            .Select((group, index) => new MigrationWave(
                index + 1,
                group.Key,
                Name(group.Key),
                group.Value.OrderBy(item => item.DependencyGroup)
                    .ThenBy(item => item.EstimatedBytes)
                    .ThenBy(item => item.QualifiedName, StringComparer.Ordinal)
                    .ToArray(),
                group.Value.Sum(item => item.EstimatedRows),
                group.Value.Sum(item => item.EstimatedBytes)))
            .ToArray();
        return new MigrationWavePlan(DateTimeOffset.UtcNow, waves);
    }

    private static MigrationWaveKind Classify(
        InventoryObject item,
        TableInventory? table,
        int dependencyCount,
        int component)
    {
        if (item.ObjectType is InventoryObjectType.Schema or InventoryObjectType.UserDefinedType
            or InventoryObjectType.TableType or InventoryObjectType.Sequence)
        {
            return MigrationWaveKind.Foundation;
        }
        if (item.ObjectType is InventoryObjectType.User or InventoryObjectType.Role
            or InventoryObjectType.ApplicationRole or InventoryObjectType.Permission
            or InventoryObjectType.SecurityPolicy)
        {
            return MigrationWaveKind.Security;
        }
        if (item.ObjectType is InventoryObjectType.View or InventoryObjectType.Function
            or InventoryObjectType.StoredProcedure or InventoryObjectType.Trigger)
        {
            return MigrationWaveKind.ProgrammableObjects;
        }
        if (table is null)
        {
            return MigrationWaveKind.Foundation;
        }
        if (component >= 0)
        {
            return MigrationWaveKind.CyclicGroups;
        }
        if (table.ReservedBytes >= LargeTableBytes || table.RowCountEstimate >= LargeTableRows)
        {
            return MigrationWaveKind.LargeTables;
        }
        if (dependencyCount == 0 && table.RowCountEstimate <= 1_000_000)
        {
            return MigrationWaveKind.ReferenceData;
        }
        return dependencyCount == 0
            ? MigrationWaveKind.IndependentTransactional
            : MigrationWaveKind.DependentTransactional;
    }

    private static string Risk(
        InventoryObject item,
        TableInventory? table,
        bool lob,
        int component)
    {
        var risks = new List<string>();
        if (component >= 0) risks.Add("cyclic dependency");
        if (lob) risks.Add("LOB");
        if (table?.ReservedBytes >= LargeTableBytes) risks.Add("large allocation");
        if (item.ConversionClassification is ConversionClassification.ManualConversion or
            ConversionClassification.Unsupported) risks.Add("manual conversion");
        return risks.Count == 0 ? "Normal" : string.Join(", ", risks);
    }

    private static string Name(MigrationWaveKind kind) => kind switch
    {
        MigrationWaveKind.Foundation => "Foundation types and schemas",
        MigrationWaveKind.ReferenceData => "Reference and master tables",
        MigrationWaveKind.IndependentTransactional => "Independent transactional tables",
        MigrationWaveKind.DependentTransactional => "Dependent transactional groups",
        MigrationWaveKind.LargeTables => "Large tables",
        MigrationWaveKind.CyclicGroups => "Cyclic dependency groups",
        MigrationWaveKind.ProgrammableObjects => "Programmable objects",
        MigrationWaveKind.Security => "Security",
        MigrationWaveKind.Validation => "Validation",
        _ => kind.ToString()
    };
}
