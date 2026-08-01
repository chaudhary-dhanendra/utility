using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.ScaleTests;

public static class SyntheticInventoryFactory
{
    public const string DatabaseName = "SyntheticCatalog6000";

    public static InventorySnapshot Create(
        int tableCount = 6000,
        int columnsPerTable = 30,
        int schemaCount = 20)
    {
        var objects = new List<InventoryObject>(tableCount + schemaCount);
        var schemas = new List<SchemaInventory>(schemaCount);
        for (var schemaIndex = 0; schemaIndex < schemaCount; schemaIndex++)
        {
            var name = $"scale_{schemaIndex:D3}";
            var schemaObject = Object(InventoryObjectType.Schema, name, name, schemaIndex + 1);
            objects.Add(schemaObject);
            schemas.Add(new SchemaInventory(
                schemaObject, "dbo", tableCount / schemaCount, false, true));
        }

        var tables = new List<TableInventory>(tableCount);
        var columns = new List<ColumnInventory>(tableCount * columnsPerTable);
        var constraints = new List<ConstraintInventory>(tableCount * 3);
        var indexes = new List<IndexInventory>(tableCount);
        var dependencies = new List<InventoryDependency>(tableCount * 2);
        var tableIds = new InventoryObjectId[tableCount];
        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            var schema = $"scale_{tableIndex % schemaCount:D3}";
            var name = tableIndex < schemaCount
                ? "SharedTable"
                : tableIndex % 20 == 0
                ? $"Table_{tableIndex:D6}_Identifier_Exceeding_PostgreSQL_Sixty_Three_Byte_Limit_For_Mapping"
                : $"Table_{tableIndex:D6}";
            var tableObject = Object(InventoryObjectType.Table, schema, name, 10_000 + tableIndex);
            tableIds[tableIndex] = tableObject.Id;
            objects.Add(tableObject);
            var estimatedRows = tableIndex % 100 == 0 ? 12_000_000L : 25_000L + tableIndex;
            var reserved = estimatedRows * (tableIndex % 25 == 0 ? 4096L : 256L);
            tables.Add(new TableInventory(
                tableObject.Id, TableKind.Ordinary, false, null, false, 0, null,
                false, false, false, false, false, false, false,
                estimatedRows, reserved, reserved * 8 / 10, []));
            for (var columnIndex = 0; columnIndex < columnsPerTable; columnIndex++)
            {
                columns.Add(Column(tableObject, columnIndex, tableIndex));
            }
            constraints.Add(Constraint(
                tableObject, InventoryObjectType.PrimaryKey, ConstraintKind.PrimaryKey,
                $"PK_{tableIndex:D6}", null, "[Id]"));
            constraints.Add(Constraint(
                tableObject, InventoryObjectType.CheckConstraint, ConstraintKind.Check,
                $"CK_{tableIndex:D6}_Amount", null, "[Amount] >= 0"));
            indexes.Add(Index(tableObject, tableIndex));
        }

        for (var tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            var target = tableIndex == 0 ? tableCount - 1 : tableIndex - 1;
            var table = objects.First(item => item.Id == tableIds[tableIndex]);
            constraints.Add(Constraint(
                table, InventoryObjectType.ForeignKey, ConstraintKind.ForeignKey,
                $"FK_{tableIndex:D6}_{target:D6}", tableIds[target], null));
            dependencies.Add(new InventoryDependency(
                tableIds[tableIndex], tableIds[target], DependencyKind.ForeignKey,
                tableIds[target].ToString(), true, false));
            var secondary = (tableIndex + 97) % tableCount;
            dependencies.Add(new InventoryDependency(
                tableIds[tableIndex], tableIds[secondary], DependencyKind.SqlExpression,
                tableIds[secondary].ToString(), true, false));
        }

        var components = DependencyGraphAnalyzer.FindStronglyConnectedComponents(
            tableIds, dependencies);
        return new InventorySnapshot
        {
            DiscoveryEngineVersion = "scale-1",
            ApplicationVersion = "1.0.0",
            SnapshotTimestamp = DateTimeOffset.UtcNow,
            ScopeMode = MigrationScopeMode.CompleteDatabase,
            Database = Database(),
            Schemas = schemas,
            Objects = objects,
            Tables = tables,
            Columns = columns,
            Constraints = constraints,
            Indexes = indexes,
            Dependencies = DependencyGraphAnalyzer.AssignComponents(dependencies, components),
            DependencyComponents = components,
            Findings = []
        };
    }

    private static InventoryObject Object(
        InventoryObjectType type,
        string schema,
        string name,
        int objectId)
    {
        var id = InventoryObjectId.Create(DatabaseName, type, schema, name, objectId);
        return new InventoryObject(
            id, DatabaseName, schema, name, $"[{schema}].[{name}]", type, objectId,
            null, null, null, false, true, SelectionReason.CompleteDatabase, 2, 2, [],
            ConversionClassification.Automatic, null, null, $"hash-{objectId:D8}", [],
            DiscoveryStatus.Discovered);
    }

    private static ColumnInventory Column(
        InventoryObject table,
        int index,
        int tableIndex)
    {
        var name = index switch
        {
            0 => "Id",
            1 => "Code",
            2 => "Amount",
            3 => "ApplicationPasswordHash",
            4 when tableIndex % 20 == 0 => "LargeText",
            5 when tableIndex % 25 == 0 => "LargeBinary",
            _ => $"Column_{index:D3}"
        };
        var type = name switch
        {
            "Id" => "bigint",
            "Amount" => "decimal",
            "ApplicationPasswordHash" or "LargeBinary" => "varbinary",
            "LargeText" => "nvarchar",
            _ => index % 4 == 0 ? "datetime2" : "nvarchar"
        };
        var maxLength = name is "LargeText" or "LargeBinary" ? (short)-1 :
            type == "nvarchar" ? (short)512 : (short)8;
        var id = InventoryObjectId.Create(
            DatabaseName, InventoryObjectType.Column, table.SourceSchema, name,
            index + 1, table.Id);
        return new ColumnInventory(
            id, table.Id, index + 1, index + 1, name, type, type, "sys", maxLength,
            type == "decimal" ? (byte)38 : (byte)0, type == "decimal" ? (byte)10 : (byte)0,
            type == "nvarchar" ? "Latin1_General_100_CI_AS_SC_UTF8" : null,
            index != 0, index == 0, index == 0 ? 1 : null, index == 0 ? 1 : null,
            null, false, false, null, false, null, false, false, false, false,
            0, false, false, null, null, null, null, null, null, null, null, []);
    }

    private static ConstraintInventory Constraint(
        InventoryObject table,
        InventoryObjectType objectType,
        ConstraintKind kind,
        string name,
        InventoryObjectId? referenced,
        string? definition)
    {
        var id = InventoryObjectId.Create(DatabaseName, objectType, table.SourceSchema, name, null, table.Id);
        return new ConstraintInventory(
            id, table.Id, kind, name, [new ConstraintColumn(1, "Id", false)],
            referenced, referenced is null ? [] : [new ConstraintColumn(1, "Id", false)],
            definition, "NO_ACTION", "NO_ACTION", false, false, false, false,
            "PRIMARY", 0, null);
    }

    private static IndexInventory Index(InventoryObject table, int tableIndex)
    {
        var name = $"IX_{tableIndex:D6}_Code";
        var id = InventoryObjectId.Create(
            DatabaseName, InventoryObjectType.Index, table.SourceSchema, name, tableIndex + 1, table.Id);
        return new IndexInventory(
            id, table.Id, 2, name, IndexKind.NonClustered, false, false, false, false,
            tableIndex % 10 == 0, tableIndex % 10 == 0 ? "[Code] IS NOT NULL" : null,
            0, "PRIMARY",
            [new IndexColumn(1, "Code", false, false), new IndexColumn(0, "Amount", false, true)],
            [], ConversionClassification.Automatic);
    }

    private static DatabaseMetadata Database() => new(
        "16.0.1000.6", "RTM", "Developer Edition", 3, DatabaseName, 7, "dbo", 160,
        "Latin1_General_100_CI_AS_SC_UTF8", "NONE", "FULL", false, "ON", true,
        false, true, true, true, false, false, true, false, false, false, "READ_WRITE",
        [], [], [], new Dictionary<string, string?>());
}
