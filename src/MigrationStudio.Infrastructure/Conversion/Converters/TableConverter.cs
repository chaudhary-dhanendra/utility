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
        var routineSignatures = columns.Any(item => item.IsComputed)
            ? BuildTargetRoutineSignatures(context)
            : new Dictionary<string, TargetRoutineSignature>(StringComparer.OrdinalIgnoreCase);
        var compatibilityAssignments = new List<ComputedCompatibilityAssignment>();

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
                        TargetRoutineSignatures = routineSignatures,
                        ExpectedTargetType = mapping.TargetType
                    });
                findings.AddRange(translated.Findings);
                extensions.UnionWith(translated.RequiredExtensions);
                if (column.IsComputedDeterministic != false &&
                    translated.IsImmutable &&
                    !ContainsNonImmutablePostgreSqlExpression(translated.Sql) &&
                    !ContainsTargetRoutineCall(translated.Sql, routineSignatures) &&
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
                    var compatibilitySql = translated.Sql;
                    if (!string.IsNullOrWhiteSpace(compatibilitySql) &&
                        translated.UnsupportedFunctions.All(item =>
                            item.Equals("RAND", StringComparison.OrdinalIgnoreCase) ||
                            item.Equals("RAND(seed)", StringComparison.OrdinalIgnoreCase)))
                    {
                        compatibilityAssignments.Add(new ComputedCompatibilityAssignment(
                            name,
                            compatibilitySql,
                            translated.ReferencedColumns
                                .Concat(FindSourceReferencedColumns(column.ComputedDefinition, columnNames.Keys))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray()));
                    }
                    classification = ConversionRuleSupport.Worst(
                        classification,
                        ConversionClassification.AutomaticWithWarning);
                    findings.Add(ConversionRuleSupport.Finding(
                        source,
                        compatibilityAssignments.Any(item => item.TargetColumn == name)
                            ? "COMPUTED.TRIGGER_COMPATIBILITY"
                            : "COMPUTED.DATA_MIGRATION",
                        FindingSeverity.Warning,
                        compatibilityAssignments.Any(item => item.TargetColumn == name)
                            ? $"Computed column '{column.Name}' uses a BEFORE INSERT/relevant-column UPDATE compatibility trigger because PostgreSQL generated columns require immutable expressions; like SQL Server computed columns, an explicitly supplied value is overwritten."
                            : $"Computed column '{column.Name}' is emitted as an ordinary nullable column and must be populated during data migration.",
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

        if (compatibilityAssignments.Count > 0)
        {
            AppendComputedCompatibilityTrigger(
                sql,
                source,
                target,
                compatibilityAssignments,
                columnNames,
                context.Identifiers);
        }

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

    private static bool ContainsNonImmutablePostgreSqlExpression(string expression)
    {
        var nonImmutable = new HashSet<string>(
            ["random", "now", "clock_timestamp", "statement_timestamp", "transaction_timestamp", "timeofday"],
            StringComparer.OrdinalIgnoreCase);
        return TSqlTokenizer.Tokenize(expression).Any(item =>
            item.Kind == TSqlTokenKind.Word &&
            (nonImmutable.Contains(item.Text) ||
             item.Text.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) ||
             item.Text.Equals("CURRENT_DATE", StringComparison.OrdinalIgnoreCase) ||
             item.Text.Equals("CURRENT_TIME", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool ContainsTargetRoutineCall(
        string expression,
        Dictionary<string, TargetRoutineSignature> signatures)
    {
        var tokens = TSqlTokenizer.Tokenize(expression);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is not TSqlTokenKind.Word and not TSqlTokenKind.QuotedIdentifier)
            {
                continue;
            }
            var dot = NextSignificantIndex(tokens, index);
            var name = dot >= 0 ? NextSignificantIndex(tokens, dot) : -1;
            var open = name >= 0 ? NextSignificantIndex(tokens, name) : -1;
            if (dot >= 0 && name >= 0 && open >= 0 &&
                tokens[dot].Text == "." &&
                tokens[name].Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier &&
                tokens[open].Text == "(" &&
                signatures.ContainsKey(
                    $"{tokens[index].Text.Trim('"')}.{tokens[name].Text.Trim('"')}"))
            {
                return true;
            }
        }
        return false;
    }

    private static int NextSignificantIndex(IReadOnlyList<TSqlToken> tokens, int index)
    {
        for (var cursor = index + 1; cursor < tokens.Count; cursor++)
        {
            if (tokens[cursor].Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            {
                return cursor;
            }
        }
        return -1;
    }

    private static Dictionary<string, TargetRoutineSignature> BuildTargetRoutineSignatures(
        ConversionContext context)
    {
        var signatures = new Dictionary<string, TargetRoutineSignature>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in context.Inventory.Modules)
        {
            if (!context.ObjectsById.TryGetValue(module.ObjectId, out var routine) ||
                routine.ObjectType != InventoryObjectType.Function)
            {
                continue;
            }
            var parameters = module.Parameters
                .Where(item => item.ParameterId != 0)
                .OrderBy(item => item.ParameterId)
                .Select(item => context.TypeMappings.Map(
                    item.TypeName,
                    item.MaximumLength,
                    item.Precision,
                    item.Scale,
                    context.Options).TargetType)
                .ToArray();
            var result = module.ResultColumns.Count == 0 ? null : module.ResultColumns[0];
            var returnParameter = module.Parameters.FirstOrDefault(item => item.ParameterId == 0);
            var returnType = result is not null
                ? context.TypeMappings.Map(
                    result.SystemTypeName,
                    result.MaximumLength,
                    result.Precision,
                    result.Scale,
                    context.Options).TargetType
                : returnParameter is null
                    ? null
                    : context.TypeMappings.Map(
                        returnParameter.TypeName,
                        returnParameter.MaximumLength,
                        returnParameter.Precision,
                        returnParameter.Scale,
                        context.Options).TargetType;
            signatures[context.Identifiers.MapObject(routine).QualifiedName] =
                new TargetRoutineSignature(parameters, returnType);
        }
        return signatures;
    }

    private static void AppendComputedCompatibilityTrigger(
        StringBuilder sql,
        InventoryObject source,
        TargetObjectIdentifier target,
        IReadOnlyList<ComputedCompatibilityAssignment> assignments,
        Dictionary<string, string> columnNames,
        IIdentifierMapper identifiers)
    {
        var functionName = identifiers.MapChildIdentifier(
            source.Id,
            "helper",
            source.SourceSchema,
            $"{source.SourceName}_computed_compat_fn");
        var triggerName = identifiers.MapChildIdentifier(
            source.Id,
            "helper",
            source.SourceSchema,
            $"{source.SourceName}_computed_compat_trg");
        sql.AppendLine()
            .Append("CREATE OR REPLACE FUNCTION ").Append(target.Schema).Append('.').Append(functionName)
            .AppendLine("() RETURNS trigger")
            .AppendLine("LANGUAGE plpgsql")
            .AppendLine("AS $migrationstudio$")
            .AppendLine("BEGIN");
        foreach (var assignment in assignments)
        {
            sql.Append("    NEW.").Append(assignment.TargetColumn).Append(" := ")
                .Append(PrefixTriggerColumnReferences(assignment.Sql, columnNames))
                .AppendLine(";");
        }
        sql.AppendLine("    RETURN NEW;")
            .AppendLine("END;")
            .AppendLine("$migrationstudio$;")
            .Append("CREATE TRIGGER ").Append(triggerName)
            .Append(" BEFORE INSERT OR UPDATE");
        var updateColumns = assignments
            .SelectMany(item =>
                item.ReferencedColumns
                    .Where(columnNames.ContainsKey)
                    .Select(column => columnNames[column])
                    .Concat(FindReferencedTargetColumns(item.Sql, columnNames)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (updateColumns.Length > 0)
        {
            sql.Append(" OF ").Append(string.Join(", ", updateColumns));
        }
        sql.Append(" ON ").Append(target.QualifiedName).AppendLine()
            .Append("FOR EACH ROW EXECUTE FUNCTION ").Append(target.Schema).Append('.').Append(functionName)
            .AppendLine("();");
    }

    private static string PrefixTriggerColumnReferences(
        string expression,
        Dictionary<string, string> columnNames)
    {
        var mappedReferences = columnNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tokens = TSqlTokenizer.Tokenize(expression).ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is not TSqlTokenKind.Word and not TSqlTokenKind.QuotedIdentifier ||
                !mappedReferences.Contains(tokens[index].Text.Trim('"')))
            {
                continue;
            }
            var previous = PreviousSignificantIndex(tokens, index);
            var next = NextSignificantIndex(tokens, index);
            if ((previous < 0 || tokens[previous].Text != ".") &&
                (next < 0 || tokens[next].Text != "("))
            {
                tokens[index] = tokens[index] with { Text = $"NEW.{tokens[index].Text}" };
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static string[] FindReferencedTargetColumns(
        string expression,
        Dictionary<string, string> columnNames)
    {
        var mappedReferences = columnNames.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TSqlTokenizer.Tokenize(expression)
            .Where(item =>
                item.Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier &&
                mappedReferences.Contains(item.Text.Trim('"')))
            .Select(item => item.Text.Trim('"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] FindSourceReferencedColumns(
        string expression,
        IEnumerable<string> sourceColumnNames)
    {
        var knownColumns = sourceColumnNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return TSqlTokenizer.Tokenize(expression)
            .Where(item =>
                item.Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier &&
                knownColumns.Contains(item.Text.Trim('[', ']', '"')))
            .Select(item => item.Text.Trim('[', ']', '"'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int PreviousSignificantIndex(List<TSqlToken> tokens, int index)
    {
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            if (tokens[cursor].Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            {
                return cursor;
            }
        }
        return -1;
    }

    private sealed record ComputedCompatibilityAssignment(
        string TargetColumn,
        string Sql,
        IReadOnlyList<string> ReferencedColumns);

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
