using System.Text;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class IndexConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType == InventoryObjectType.Index;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.InventoryIndex.IndexesByObjectId.TryGetValue(source.Id, out var index) ||
            !context.ObjectsById.TryGetValue(index.TableObjectId, out var table))
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source, "Index metadata is incomplete.", $"-- Manual index definition required for {source.QualifiedSourceName}.", "missing index metadata"));
        }

        if (index.IsPrimaryKey || index.IsUniqueConstraint)
        {
            return Task.FromResult(ConversionRuleSupport.Success(
                $"-- Index {source.QualifiedSourceName} is created by its PostgreSQL constraint.",
                "INDEX.CONSTRAINT_OWNED"));
        }

        if (index.Kind == IndexKind.Heap)
        {
            return Task.FromResult(ConversionRuleSupport.Success(
                $"-- {TargetTableName(context, table)} is a SQL Server heap; PostgreSQL creates no separate index object.",
                "INDEX.HEAP.NO_OBJECT",
                [
                    ConversionRuleSupport.Finding(
                        source,
                        "INDEX.HEAP.NO_OBJECT",
                        FindingSeverity.Information,
                        "SQL Server heap metadata does not represent an index and no PostgreSQL CREATE INDEX statement was generated.")
                ]));
        }

        if (index.Kind is not IndexKind.Clustered and not IndexKind.NonClustered)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                $"{index.Kind} has no safe automatic PostgreSQL index equivalent.",
                $"-- Manual {index.Kind} index required for {context.Identifiers.MapObject(table).QualifiedName}.",
                index.Kind.ToString()));
        }

        if (index.IsDisabled)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Disabled indexes are not emitted as active PostgreSQL indexes.",
                $"-- Source index {source.QualifiedSourceName} is disabled.",
                "disabled index"));
        }

        var targetTable = context.Identifiers.MapObject(table);
        var name = context.Identifiers.MapChildIdentifier(table.Id, "index", table.SourceSchema, index.Name);
        var keyColumns = index.Columns
            .Where(item => !item.IsIncluded && item.KeyOrdinal > 0)
            .OrderBy(item => item.KeyOrdinal)
            .ToArray();
        if (keyColumns.Length == 0)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "The SQL Server index contains no key columns; emitting CREATE INDEX would produce an empty PostgreSQL key list.",
                $"-- Index {name} on {targetTable.QualifiedName} has no key columns and was not emitted.",
                "empty index key list"));
        }
        const int maximumPostgreSqlIndexColumns = 32;
        if (keyColumns.Length > maximumPostgreSqlIndexColumns)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                $"The index has {keyColumns.Length} key columns, exceeding PostgreSQL's {maximumPostgreSqlIndexColumns}-column limit; key columns cannot be dropped safely.",
                $"-- Manual index redesign required for {name} on {targetTable.QualifiedName}.",
                "oversized index key list"));
        }
        var keys = string.Join(
            ", ",
            keyColumns.Select(item =>
                $"{context.Identifiers.MapChildIdentifier(table.Id, "column", table.SourceSchema, item.Name)}{(item.IsDescending ? " DESC" : string.Empty)}"));
        var allIncluded = index.Columns.Where(item => item.IsIncluded)
            .Select(item => context.Identifiers.MapChildIdentifier(table.Id, "column", table.SourceSchema, item.Name))
            .ToArray();
        var includeCapacity = Math.Max(0, maximumPostgreSqlIndexColumns - keyColumns.Length);
        var included = allIncluded.Take(includeCapacity).ToArray();
        var sql = new StringBuilder("CREATE ")
            .Append(index.IsUnique ? "UNIQUE " : string.Empty)
            .Append("INDEX ").Append(name)
            .Append(" ON ").Append(targetTable.QualifiedName)
            .Append(" USING btree (").Append(keys).Append(')');
        if (included.Length > 0)
        {
            sql.Append(" INCLUDE (").Append(string.Join(", ", included)).Append(')');
        }

        var findings = new List<InventoryFinding>();
        var classification = ConversionClassification.Automatic;
        if (allIncluded.Length > included.Length)
        {
            classification = ConversionClassification.AutomaticWithWarning;
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "INDEX.INCLUDE_COLUMNS_OMITTED",
                FindingSeverity.Warning,
                $"PostgreSQL permits at most {maximumPostgreSqlIndexColumns} index columns; " +
                $"{allIncluded.Length - included.Length} trailing INCLUDE column(s) were omitted: " +
                string.Join(", ", allIncluded.Skip(included.Length))));
        }
        if (index.IsFiltered && !string.IsNullOrWhiteSpace(index.FilterDefinition))
        {
            var translated = context.Expressions.Translate(
                index.FilterDefinition,
                new ExpressionTranslationContext(
                    source.Id,
                    context.InventoryIndex.ColumnsByParentObjectId.GetValueOrDefault(table.Id, [])
                        .ToDictionary(item => item.Name, item => item.SystemTypeName, StringComparer.OrdinalIgnoreCase),
                    context.Options,
                    false)
                {
                    TargetColumnNames = context.InventoryIndex.ColumnsByParentObjectId
                        .GetValueOrDefault(table.Id, [])
                        .ToDictionary(
                            item => item.Name,
                            item => context.Identifiers.MapChildIdentifier(
                                table.Id, "column", table.SourceSchema, item.Name),
                            StringComparer.OrdinalIgnoreCase),
                    TargetObjectNames = context.TargetObjectNames,
                    TargetColumnTypes = context.InventoryIndex.ColumnsByParentObjectId
                        .GetValueOrDefault(table.Id, [])
                        .ToDictionary(
                            item => item.Name,
                            item => context.TypeMappings.Map(item, table, context.Options).TargetType,
                            StringComparer.OrdinalIgnoreCase)
                });
            if (translated.Classification == ConversionClassification.ManualConversion)
            {
                return Task.FromResult(ConversionRuleSupport.Manual(
                    source,
                    "Filtered-index predicate could not be translated safely.",
                    $"-- Partial index {name} requires manual predicate conversion.",
                    translated.UnsupportedFunctions.ToArray()));
            }
            sql.Append(" WHERE ").Append(translated.Sql);
            findings.AddRange(translated.Findings);
            classification = ConversionRuleSupport.Worst(classification, translated.Classification);
        }
        sql.Append(';');

        if (index.Kind == IndexKind.Clustered)
        {
            classification = ConversionClassification.AutomaticWithWarning;
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "INDEX.CLUSTERED",
                FindingSeverity.Warning,
                "The index is created, but SQL Server clustered storage semantics are not preserved."));
        }
        if (index.Partitions.Count > 1)
        {
            classification = ConversionClassification.AutomaticWithWarning;
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "INDEX.PARTITIONING_REVIEW",
                FindingSeverity.Warning,
                "SQL Server partition placement is not recreated automatically for this index."));
        }

        return Task.FromResult(ConversionRuleSupport.Success(
            sql.ToString(),
            "INDEX.BTREE",
            findings,
            classification: classification,
            confidence: classification == ConversionClassification.Automatic ? 1m : 0.85m));
    }

    private static string TargetTableName(ConversionContext context, InventoryObject table) =>
        context.Identifiers.MapObject(table).QualifiedName;
}
