using Microsoft.Data.SqlClient;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed partial class SqlServerInventoryDiscoveryService
{
    private static async Task ReadConstraintsAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var keyRows = new List<KeyConstraintRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keyRows.Add(new KeyConstraintRow(
                reader.Int32("object_id"),
                reader.Int32("parent_object_id"),
                reader.Text("name"),
                reader.Text("type"),
                reader.Int32("ordinal"),
                reader.Text("column_name"),
                reader.Boolean("is_descending_key"),
                reader.Text("type_desc"),
                reader.Int32("fill_factor"),
                reader.NullableText("data_space_name"),
                reader.NullableText("filter_definition")));
        }

        foreach (var group in keyRows.GroupBy(row => row.ObjectId))
        {
            var first = group.First();
            if (!accumulator.ObjectsBySqlId.ContainsKey(first.ObjectId) ||
                !accumulator.ObjectsBySqlId.ContainsKey(first.ParentObjectId))
            {
                continue;
            }

            accumulator.Constraints.Add(new ConstraintInventory(
                accumulator.GetObject(first.ObjectId).Id,
                accumulator.GetObject(first.ParentObjectId).Id,
                first.Type == "PK" ? ConstraintKind.PrimaryKey : ConstraintKind.Unique,
                first.Name,
                group.OrderBy(row => row.Ordinal)
                    .Select(row => new ConstraintColumn(row.Ordinal, row.ColumnName, row.IsDescending))
                    .ToArray(),
                null,
                [],
                null,
                null,
                null,
                false,
                false,
                false,
                first.IndexType.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase),
                first.DataSpaceName,
                first.FillFactor,
                first.FilterDefinition));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            var parentId = reader.Int32("parent_object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId) ||
                !accumulator.ObjectsBySqlId.ContainsKey(parentId))
            {
                continue;
            }

            var definition = reader.NullableText("definition");
            accumulator.Constraints.Add(new ConstraintInventory(
                accumulator.GetObject(objectId).Id,
                accumulator.GetObject(parentId).Id,
                ConstraintKind.Check,
                reader.Text("name"),
                reader.Int32("parent_column_id") is var columnId && columnId > 0
                    ? [new ConstraintColumn(1, FindColumnName(accumulator, parentId, columnId), false)]
                    : [],
                null,
                [],
                definition,
                null,
                null,
                reader.Boolean("is_disabled"),
                reader.Boolean("is_not_trusted"),
                reader.Boolean("is_not_for_replication"),
                false,
                null,
                0,
                null));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var foreignKeyRows = new List<ForeignKeyRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            foreignKeyRows.Add(new ForeignKeyRow(
                reader.Int32("object_id"),
                reader.Int32("parent_object_id"),
                reader.Int32("referenced_object_id"),
                reader.Text("name"),
                reader.Int32("ordinal"),
                reader.Text("parent_column"),
                reader.Text("referenced_column"),
                reader.Text("delete_referential_action_desc"),
                reader.Text("update_referential_action_desc"),
                reader.Boolean("is_disabled"),
                reader.Boolean("is_not_trusted"),
                reader.Boolean("is_not_for_replication")));
        }

        foreach (var group in foreignKeyRows.GroupBy(row => row.ObjectId))
        {
            var first = group.First();
            if (!accumulator.ObjectsBySqlId.ContainsKey(first.ObjectId) ||
                !accumulator.ObjectsBySqlId.ContainsKey(first.ParentObjectId))
            {
                continue;
            }

            var referencedId = accumulator.TryGetObjectId(first.ReferencedObjectId);
            accumulator.Constraints.Add(new ConstraintInventory(
                accumulator.GetObject(first.ObjectId).Id,
                accumulator.GetObject(first.ParentObjectId).Id,
                ConstraintKind.ForeignKey,
                first.Name,
                group.OrderBy(row => row.Ordinal)
                    .Select(row => new ConstraintColumn(row.Ordinal, row.ParentColumn, false))
                    .ToArray(),
                referencedId,
                group.OrderBy(row => row.Ordinal)
                    .Select(row => new ConstraintColumn(row.Ordinal, row.ReferencedColumn, false))
                    .ToArray(),
                null,
                first.DeleteAction,
                first.UpdateAction,
                first.IsDisabled,
                first.IsNotTrusted,
                first.IsNotForReplication,
                false,
                null,
                0,
                null));
            accumulator.Dependencies.Add(new InventoryDependency(
                accumulator.GetObject(first.ParentObjectId).Id,
                referencedId,
                DependencyKind.ForeignKey,
                referencedId is { } resolved ? resolved.ToString() : first.ReferencedObjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                referencedId is not null,
                false,
                Evidence: first.Name));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var objectId = reader.Int32("object_id");
            var parentId = reader.Int32("parent_object_id");
            if (!accumulator.ObjectsBySqlId.ContainsKey(objectId) ||
                !accumulator.ObjectsBySqlId.ContainsKey(parentId))
            {
                continue;
            }

            var columnId = reader.Int32("parent_column_id");
            accumulator.Constraints.Add(new ConstraintInventory(
                accumulator.GetObject(objectId).Id,
                accumulator.GetObject(parentId).Id,
                ConstraintKind.Default,
                reader.Text("name"),
                columnId > 0 ? [new ConstraintColumn(1, FindColumnName(accumulator, parentId, columnId), false)] : [],
                null,
                [],
                reader.NullableText("definition"),
                null,
                null,
                false,
                false,
                false,
                false,
                null,
                0,
                null));
        }
    }

    private static async Task ReadIndexesAsync(
        SqlDataReader reader,
        InventoryAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var indexRows = new List<IndexRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            indexRows.Add(new IndexRow(
                reader.Int32("object_id"),
                reader.Int32("index_id"),
                reader.Text("name"),
                reader.Int32("type"),
                reader.Text("type_desc"),
                reader.Boolean("is_unique"),
                reader.Boolean("is_primary_key"),
                reader.Boolean("is_unique_constraint"),
                reader.Boolean("is_disabled"),
                reader.Boolean("has_filter"),
                reader.NullableText("filter_definition"),
                reader.Int32("fill_factor"),
                reader.NullableText("data_space_name"),
                reader.Int32("key_ordinal"),
                reader.NullableText("column_name"),
                reader.Boolean("is_descending_key"),
                reader.Boolean("is_included_column")));
        }

        await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        var partitions = new Dictionary<(int ObjectId, int IndexId), List<PartitionInventory>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var key = (reader.Int32("object_id"), reader.Int32("index_id"));
            if (!partitions.TryGetValue(key, out var list))
            {
                list = [];
                partitions[key] = list;
            }

            list.Add(new PartitionInventory(
                reader.Int32("partition_number"),
                reader.Int64("rows"),
                reader.Text("data_compression_desc"),
                reader.NullableText("data_space_name"),
                reader.NullableText("partition_scheme"),
                reader.NullableText("partition_column")));
        }

        foreach (var group in indexRows.GroupBy(row => (row.ObjectId, row.IndexId)))
        {
            var first = group.First();
            if (!accumulator.ObjectsBySqlId.ContainsKey(first.ObjectId))
            {
                continue;
            }

            var parent = accumulator.GetObject(first.ObjectId);
            var kind = MapIndexKind(first.Type);
            var indexObject = accumulator.AddSyntheticObject(
                InventoryObjectType.Index,
                parent.SourceSchema,
                first.Name,
                parent.Id,
                InventoryClassification.ForObject(InventoryObjectType.Index, indexKind: kind),
                new
                {
                    first.IndexId,
                    first.TypeDescription,
                    first.IsUnique,
                    first.IsDisabled,
                    first.FilterDefinition,
                    first.DataSpaceName
                });
            var partitionList = partitions.GetValueOrDefault(group.Key) ?? [];
            accumulator.Indexes.Add(new IndexInventory(
                indexObject.Id,
                parent.Id,
                first.IndexId,
                first.Name,
                kind,
                first.IsUnique,
                first.IsPrimaryKey,
                first.IsUniqueConstraint,
                first.IsDisabled,
                first.HasFilter,
                first.FilterDefinition,
                first.FillFactor,
                first.DataSpaceName,
                group.Where(row => row.ColumnName is not null)
                    .OrderBy(row => row.KeyOrdinal == 0 ? int.MaxValue : row.KeyOrdinal)
                    .Select(row => new IndexColumn(
                        row.KeyOrdinal,
                        row.ColumnName!,
                        row.IsDescending,
                        row.IsIncluded))
                    .ToArray(),
                partitionList,
                InventoryClassification.ForObject(InventoryObjectType.Index, indexKind: kind)));

            if (kind is IndexKind.Xml or IndexKind.Spatial or IndexKind.Hash or
                IndexKind.ClusteredColumnstore or IndexKind.NonClusteredColumnstore)
            {
                accumulator.Findings.Add(new InventoryFinding(
                    "INDEX.MANUAL_CONVERSION",
                    FindingSeverity.Warning,
                    $"Index type {first.TypeDescription} requires a PostgreSQL-specific design.",
                    indexObject.Id));
            }
        }

        var tableIndexPartitions = partitions
            .Where(pair => pair.Key.IndexId is 0 or 1)
            .ToDictionary(pair => pair.Key.ObjectId, pair => (IReadOnlyList<PartitionInventory>)pair.Value);
        for (var index = 0; index < accumulator.Tables.Count; index++)
        {
            var table = accumulator.Tables[index];
            var sqlObjectId = accumulator.ObjectsBySqlId
                .FirstOrDefault(pair => pair.Value.Id == table.ObjectId).Key;
            if (tableIndexPartitions.TryGetValue(sqlObjectId, out var tablePartitions))
            {
                accumulator.Tables[index] = table with { Partitions = tablePartitions };
            }
        }
    }

    private static string FindColumnName(InventoryAccumulator accumulator, int objectId, int columnId) =>
        accumulator.Columns.FirstOrDefault(
            column => column.ParentObjectId == accumulator.GetObject(objectId).Id && column.ColumnId == columnId)?.Name
        ?? $"ColumnId:{columnId}";

    private static IndexKind MapIndexKind(int type) => type switch
    {
        0 => IndexKind.Heap,
        1 => IndexKind.Clustered,
        2 => IndexKind.NonClustered,
        3 => IndexKind.Xml,
        4 => IndexKind.Spatial,
        5 => IndexKind.ClusteredColumnstore,
        6 => IndexKind.NonClusteredColumnstore,
        7 => IndexKind.Hash,
        _ => IndexKind.NonClustered
    };

    private sealed record KeyConstraintRow(
        int ObjectId,
        int ParentObjectId,
        string Name,
        string Type,
        int Ordinal,
        string ColumnName,
        bool IsDescending,
        string IndexType,
        int FillFactor,
        string? DataSpaceName,
        string? FilterDefinition);

    private sealed record ForeignKeyRow(
        int ObjectId,
        int ParentObjectId,
        int ReferencedObjectId,
        string Name,
        int Ordinal,
        string ParentColumn,
        string ReferencedColumn,
        string DeleteAction,
        string UpdateAction,
        bool IsDisabled,
        bool IsNotTrusted,
        bool IsNotForReplication);

    private sealed record IndexRow(
        int ObjectId,
        int IndexId,
        string Name,
        int Type,
        string TypeDescription,
        bool IsUnique,
        bool IsPrimaryKey,
        bool IsUniqueConstraint,
        bool IsDisabled,
        bool HasFilter,
        string? FilterDefinition,
        int FillFactor,
        string? DataSpaceName,
        int KeyOrdinal,
        string? ColumnName,
        bool IsDescending,
        bool IsIncluded);
}
