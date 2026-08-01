using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed partial class TableConverter(
    ILogger<TableConverter>? logger = null) : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType is InventoryObjectType.Table or InventoryObjectType.ExternalTable;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.InventoryIndex.TablesByObjectId.TryGetValue(source.Id, out var table))
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Table metadata is missing from the inventory.",
                $"-- Manual table definition required for {context.Identifiers.MapObject(source).QualifiedName}.",
                "missing table metadata"));
        }

        if (table.IsExternal)
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "SQL Server external tables require a configured PostgreSQL foreign-data wrapper.",
                $"-- Configure a foreign table for {context.Identifiers.MapObject(source).QualifiedName}.",
                "external table"));
        }

        var columns = context.InventoryIndex.ColumnsByParentObjectId
            .GetValueOrDefault(source.Id, [])
            .Where(item => !item.IsHidden)
            .ToArray();
        LogDiagnosticColumn(source, columns, context);
        var findings = new List<InventoryFinding>();
        var unsupported = new List<string>();
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classification = table.Kind == TableKind.Ordinary
            ? ConversionClassification.Automatic
            : ConversionClassification.AutomaticWithWarning;
        var target = context.Identifiers.MapObject(source);
        var sql = new StringBuilder()
            .Append("CREATE TABLE ").Append(target.QualifiedName).AppendLine()
            .AppendLine("(");
        var definitions = new List<string>();
        var columnTypes = columns.ToDictionary(item => item.Name, item => item.SystemTypeName, StringComparer.OrdinalIgnoreCase);
        var columnNames = columns.ToDictionary(
            item => item.Name,
            item => context.Identifiers.MapChildIdentifier(source.Id, "column", source.SourceSchema, item.Name),
            StringComparer.OrdinalIgnoreCase);
        var targetColumnTypes = columns.ToDictionary(
            item => item.Name,
            item => context.TypeMappings.Map(item, source, context.Options).TargetType,
            StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            var mapping = context.TypeMappings.Map(column, source, context.Options);
            findings.AddRange(mapping.Findings.Select(item => item with { ObjectId = source.Id }));
            extensions.UnionWith(mapping.RequiredExtensions);
            classification = ConversionRuleSupport.Worst(classification, mapping.Classification);
            var name = context.Identifiers.MapChildIdentifier(source.Id, "column", source.SourceSchema, column.Name);
            var definition = new StringBuilder("    ").Append(name).Append(' ').Append(mapping.TargetType);

            if (column.IsIdentity)
            {
                var requiresManualIdentity = AppendIdentity(
                    definition,
                    column,
                    context.Options.IdentityStrategy,
                    findings,
                    source,
                    target,
                    context.Identifiers);
                if (requiresManualIdentity)
                {
                    classification = ConversionClassification.ManualConversion;
                    unsupported.Add($"identity column {column.Name}");
                }
            }

            if (column.IsComputed && !string.IsNullOrWhiteSpace(column.ComputedDefinition))
            {
                var translated = context.Expressions.Translate(
                    column.ComputedDefinition,
                    new ExpressionTranslationContext(source.Id, columnTypes, context.Options, true)
                    {
                        TargetColumnNames = columnNames,
                        TargetObjectNames = context.TargetObjectNames,
                        TargetColumnTypes = targetColumnTypes,
                        ExpectedTargetType = mapping.TargetType
                    });
                findings.AddRange(translated.Findings);
                extensions.UnionWith(translated.RequiredExtensions);
                if (translated.IsImmutable &&
                    translated.Classification is ConversionClassification.Automatic or
                        ConversionClassification.AutomaticWithWarning)
                {
                    definition.Append(" GENERATED ALWAYS AS (").Append(translated.Sql).Append(") STORED");
                    classification = ConversionRuleSupport.Worst(classification, translated.Classification);
                    findings.Add(ConversionRuleSupport.Finding(
                        source,
                        "COMPUTED.GENERATED",
                        FindingSeverity.Information,
                        $"Computed column '{column.Name}' was converted to a stored generated column.",
                        $"Source: {column.ComputedDefinition}{Environment.NewLine}Target: {translated.Sql}"));
                }
                else
                {
                    classification = ConversionRuleSupport.Worst(
                        classification,
                        ConversionClassification.AutomaticWithWarning);
                    findings.Add(ConversionRuleSupport.Finding(
                        source,
                        "COMPUTED.DATA_MIGRATION",
                        FindingSeverity.Warning,
                        $"Computed column '{column.Name}' is emitted as an ordinary nullable column and must be populated during data migration.",
                        column.ComputedDefinition));
                }
            }
            else if (!string.IsNullOrWhiteSpace(column.DefaultDefinition) && !column.IsIdentity)
            {
                var translated = context.Expressions.Translate(
                    column.DefaultDefinition,
                    new ExpressionTranslationContext(source.Id, columnTypes, context.Options, false)
                    {
                        TargetColumnNames = columnNames,
                        TargetObjectNames = context.TargetObjectNames,
                        TargetColumnTypes = targetColumnTypes,
                        ExpectedTargetType = mapping.TargetType
                    });
                findings.AddRange(translated.Findings);
                extensions.UnionWith(translated.RequiredExtensions);
                if (translated.Classification != ConversionClassification.ManualConversion)
                {
                    var defaultSql = mapping.TargetType == "boolean"
                        ? translated.Sql switch
                        {
                            "0" => "false",
                            "1" => "true",
                            _ => translated.Sql
                        }
                        : translated.Sql;
                    definition.Append(" DEFAULT ").Append(defaultSql);
                }
                else
                {
                    classification = ConversionClassification.ManualConversion;
                    unsupported.Add($"default for {column.Name}");
                }
            }

            if (!column.IsNullable && !column.IsComputed)
            {
                definition.Append(" NOT NULL");
            }
            definitions.Add(definition.ToString());
        }

        sql.AppendLine(string.Join($",{Environment.NewLine}", definitions))
            .AppendLine(");");

        if (table.Kind != TableKind.Ordinary)
        {
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "TABLE.SPECIAL_FEATURE",
                FindingSeverity.Warning,
                $"SQL Server table kind '{table.Kind}' has storage or behavior semantics that PostgreSQL DDL does not reproduce."));
        }

        if (extensions.Count > 0)
        {
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "TABLE.EXTENSIONS",
                FindingSeverity.Information,
                $"Required extensions: {string.Join(", ", extensions.Order(StringComparer.OrdinalIgnoreCase))}."));
        }

        return Task.FromResult(ConversionRuleSupport.Success(
            sql.ToString().TrimEnd(),
            "TABLE.CATALOG",
            findings,
            unsupported,
            extensions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            classification,
            classification == ConversionClassification.Automatic ? 1m : 0.75m));
    }

    private static bool AppendIdentity(
        StringBuilder definition,
        ColumnInventory column,
        IdentityConversionStrategy strategy,
        List<InventoryFinding> findings,
        InventoryObject source,
        TargetObjectIdentifier targetTable,
        IIdentifierMapper identifiers)
    {
        var seed = column.IdentitySeed?.ToString(CultureInfo.InvariantCulture) ?? "1";
        var increment = column.IdentityIncrement?.ToString(CultureInfo.InvariantCulture) ?? "1";
        switch (strategy)
        {
            case IdentityConversionStrategy.GeneratedByDefaultAsIdentity:
                definition.Append(" GENERATED BY DEFAULT AS IDENTITY (START WITH ")
                    .Append(seed).Append(" INCREMENT BY ").Append(increment).Append(')');
                return false;
            case IdentityConversionStrategy.GeneratedAlwaysAsIdentity:
                definition.Append(" GENERATED ALWAYS AS IDENTITY (START WITH ")
                    .Append(seed).Append(" INCREMENT BY ").Append(increment).Append(')');
                return false;
            case IdentityConversionStrategy.SequenceAndDefault:
                var sequence = identifiers.MapChildIdentifier(
                    source.Id,
                    "sequence",
                    source.SourceSchema,
                    $"{source.SourceName}_{column.Name}_seq");
                var qualifiedSequence = $"{targetTable.Schema}.{sequence}";
                definition.Append(" DEFAULT nextval('")
                    .Append(qualifiedSequence.Replace("'", "''", StringComparison.Ordinal))
                    .Append("'::regclass)");
                findings.Add(ConversionRuleSupport.Finding(
                    source,
                    "IDENTITY.SEQUENCE_REQUIRED",
                    FindingSeverity.Warning,
                    $"Identity column '{column.Name}' requires a generated sequence and post-load reset."));
                return false;
            case IdentityConversionStrategy.PlainIntegerManual:
                findings.Add(ConversionRuleSupport.Finding(
                    source,
                    "IDENTITY.MANUAL",
                    FindingSeverity.Warning,
                    $"Identity behavior for '{column.Name}' is intentionally not generated."));
                return true;
        }
        return false;
    }

    private void LogDiagnosticColumn(
        InventoryObject source,
        IReadOnlyList<ColumnInventory> columns,
        ConversionContext context)
    {
        if (logger is null ||
            !source.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) ||
            !source.SourceName.Equals("verify_observe1819", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var column = columns.FirstOrDefault(item =>
            item.Name.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase));
        if (column is null || !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }
        var key = new ColumnIdentifierKey(source.Id, column.ColumnId);
        var mapping = context.Identifiers.Mappings.SingleOrDefault(item =>
            item.SourceKey.ColumnKey == key);
        var canonicalKey = key.ToString();
        LogIdentifierUse(
            logger,
            "ColumnConverter",
            context.Identifiers.MappingSetId,
            context.Identifiers.SchemaVersion,
            canonicalKey,
            mapping?.TargetName ?? string.Empty,
            mapping is not null);
    }

    [LoggerMessage(
        EventId = 2220,
        Level = LogLevel.Information,
        Message = "Identifier map use Stage={Stage}, MappingSetId={MappingSetId}, MappingVersion={MappingVersion}, CanonicalKey={CanonicalKey}, Target={Target}, Exists={Exists}.")]
    private static partial void LogIdentifierUse(
        ILogger logger,
        string stage,
        Guid mappingSetId,
        int mappingVersion,
        string canonicalKey,
        string target,
        bool exists);
}
