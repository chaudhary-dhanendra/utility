using Microsoft.Data.SqlClient;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService
{
    private static async Task ReadTablesAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var tablesById = new Dictionary<int, TableInventory>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId))
            {
                continue;
            }

            var kind = DetermineTableKind(
                reader.Boolean("is_memory_optimized"),
                reader.Boolean("is_filetable"),
                reader.Int32("temporal_type"),
                reader.Boolean("is_node"),
                reader.Boolean("is_edge"),
                reader.Boolean("is_ledger"),
                reader.Boolean("is_remote_data_archive_enabled"),
                isExternal: false);
            var historyId = accumulator.TryGetObjectId(reader.NullableInt32("history_table_id"));
            var table = new TableInventory(
                accumulator.GetObject(objectId).Id,
                kind,
                reader.Boolean("is_memory_optimized"),
                reader.NullableText("durability_desc"),
                reader.Boolean("is_filetable"),
                reader.Int32("temporal_type"),
                historyId,
                false,
                reader.Boolean("is_node"),
                reader.Boolean("is_edge"),
                reader.Boolean("is_ledger"),
                reader.Boolean("is_remote_data_archive_enabled"),
                reader.Boolean("lock_escalation_disabled"),
                reader.Boolean("lock_on_bulk_load"),
                reader.Int64("row_count"),
                reader.Int64("reserved_bytes"),
                reader.Int64("used_bytes"),
                []);
            tablesById[objectId] = table;
            accumulator.UpdateObject(objectId, item => item with
            {
                ConversionClassification = InventoryClassification.ForObject(item.ObjectType, tableKind: kind)
            });
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!tablesById.TryGetValue(objectId, out var table))
            {
                continue;
            }

            table = table with { Kind = TableKind.External, IsExternal = true };
            tablesById[objectId] = table;
            accumulator.UpdateObject(objectId, item => item with
            {
                ObjectType = InventoryObjectType.ExternalTable,
                ConversionClassification = ConversionClassification.ManualConversion,
                DiscoveryWarnings = item.DiscoveryWarnings.Concat(
                [
                    new InventoryFinding(
                        "TABLE.EXTERNAL",
                        FindingSeverity.Warning,
                        "External tables require an explicit PostgreSQL foreign-data or ingestion design.",
                        item.Id,
                        Evidence: reader.NullableText("location"))
                ]).ToArray()
            });
        }

        accumulator.Tables.AddRange(tablesById.Values);
    }

    private static async Task ReadColumnsAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId))
            {
                continue;
            }

            var columnId = reader.Int32("column_id");
            var name = reader.Text("name");
            var columnObject = accumulator.AddColumnObject(
                objectId,
                columnId,
                name,
                new
                {
                    SystemType = reader.Text("system_type_name"),
                    UserType = reader.Text("user_type_name"),
                    MaxLength = reader.Int16("max_length"),
                    Precision = reader.Byte("precision"),
                    Scale = reader.Byte("scale"),
                    IsNullable = reader.Boolean("is_nullable"),
                    IsIdentity = reader.Boolean("is_identity"),
                    IsComputed = reader.Boolean("is_computed"),
                    IsSparse = reader.Boolean("is_sparse"),
                    IsEncrypted = reader.NullableText("encryption_type_desc")
                });
            var column = new ColumnInventory(
                columnObject.Id,
                accumulator.GetObject(objectId).Id,
                columnId,
                columnId,
                name,
                reader.Text("system_type_name"),
                reader.Text("user_type_name"),
                reader.Text("type_schema"),
                reader.Int16("max_length"),
                reader.Byte("precision"),
                reader.Byte("scale"),
                reader.NullableText("collation_name"),
                reader.Boolean("is_nullable"),
                reader.Boolean("is_identity"),
                reader.NullableDecimal("seed_value"),
                reader.NullableDecimal("increment_value"),
                reader.NullableDecimal("last_value"),
                reader.Boolean("is_not_for_replication"),
                reader.Boolean("is_computed"),
                reader.NullableText("computed_definition"),
                reader.Boolean("is_persisted"),
                reader.NullableBoolean("is_deterministic"),
                reader.Boolean("is_sparse"),
                reader.Boolean("is_column_set"),
                reader.Boolean("is_rowguidcol"),
                reader.Boolean("is_filestream"),
                reader.Int32("generated_always_type"),
                reader.Boolean("is_hidden"),
                reader.Boolean("is_masked"),
                reader.NullableText("masking_function"),
                reader.NullableText("encryption_type_desc"),
                reader.NullableText("encryption_algorithm_name"),
                reader.NullableText("column_encryption_key"),
                reader.NullableText("xml_schema_collection"),
                reader.NullableText("default_constraint_name"),
                reader.NullableText("default_definition"),
                reader.NullableText("rule_name"),
                []);
            accumulator.Columns.Add(column);

            if (column.IsComputed)
            {
                accumulator.Dependencies.Add(new InventoryDependency(
                    column.ObjectId,
                    null,
                    DependencyKind.ComputedColumn,
                    column.ComputedDefinition ?? string.Empty,
                    false,
                    false,
                    Evidence: column.ComputedDefinition));
            }

            if (column.IsMasked)
            {
                accumulator.Findings.Add(new InventoryFinding(
                    "COLUMN.DYNAMIC_DATA_MASKING",
                    FindingSeverity.Warning,
                    "Dynamic data masking has no direct general PostgreSQL equivalent.",
                    column.ObjectId,
                    column.MaskingFunction));
            }

            if (column.EncryptionType is not null)
            {
                accumulator.Encryption.Add(new EncryptionInventory(
                    column.ObjectId,
                    "ALWAYS_ENCRYPTED_COLUMN",
                    column.Name,
                    column.EncryptionAlgorithm,
                    column.ColumnEncryptionKey,
                    false,
                    column.EncryptionType));
            }
        }
    }

    private static TableKind DetermineTableKind(
        bool memoryOptimized,
        bool fileTable,
        int temporalType,
        bool isNode,
        bool isEdge,
        bool isLedger,
        bool isStretch,
        bool isExternal)
    {
        if (isExternal) return TableKind.External;
        if (isLedger) return TableKind.Ledger;
        if (isNode) return TableKind.GraphNode;
        if (isEdge) return TableKind.GraphEdge;
        if (fileTable) return TableKind.FileTable;
        if (memoryOptimized) return TableKind.MemoryOptimized;
        if (isStretch) return TableKind.Stretch;
        return temporalType switch
        {
            1 => TableKind.TemporalHistory,
            2 => TableKind.TemporalCurrent,
            _ => TableKind.Ordinary
        };
    }
}
