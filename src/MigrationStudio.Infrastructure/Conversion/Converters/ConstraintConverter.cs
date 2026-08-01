using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using System.Text;
using System.Text.RegularExpressions;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed class ConstraintConverter : IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType is InventoryObjectType.PrimaryKey or InventoryObjectType.UniqueConstraint or
            InventoryObjectType.CheckConstraint or InventoryObjectType.ForeignKey or InventoryObjectType.DefaultConstraint;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.InventoryIndex.ConstraintsByObjectId.TryGetValue(source.Id, out var constraint) ||
            !context.ObjectsById.TryGetValue(constraint.TableObjectId, out var table))
        {
            return Task.FromResult(ConversionRuleSupport.Manual(
                source,
                "Constraint or owning-table metadata is incomplete.",
                $"-- Manual constraint definition required for {source.QualifiedSourceName}.",
                "missing constraint metadata"));
        }

        var targetTable = context.Identifiers.MapObject(table);
        var name = context.Identifiers.MapChildIdentifier(
            table.Id,
            "constraint",
            table.SourceSchema,
            constraint.Name);
        var columns = string.Join(
            ", ",
            constraint.Columns.OrderBy(item => item.Ordinal)
                .Select(item => context.Identifiers.MapChildIdentifier(table.Id, "column", table.SourceSchema, item.Name)));
        var findings = new List<InventoryFinding>();
        var classification = ConversionClassification.Automatic;
        string clause;

        switch (constraint.Kind)
        {
            case ConstraintKind.PrimaryKey:
                clause = $"PRIMARY KEY ({columns})";
                break;
            case ConstraintKind.Unique:
                clause = $"UNIQUE ({columns})";
                break;
            case ConstraintKind.Check:
                if (string.IsNullOrWhiteSpace(constraint.Definition))
                {
                    return Task.FromResult(ConversionRuleSupport.Manual(
                        source, "Check definition is unavailable.", $"-- CHECK {name} definition unavailable.", "missing check definition"));
                }
                var tableColumns = context.InventoryIndex.ColumnsByParentObjectId
                    .GetValueOrDefault(table.Id, []);
                var translated = context.Expressions.Translate(
                    constraint.Definition,
                    new ExpressionTranslationContext(
                        source.Id,
                        tableColumns.ToDictionary(
                            item => item.Name,
                            item => item.SystemTypeName,
                            StringComparer.OrdinalIgnoreCase),
                        context.Options,
                        false)
                    {
                        TargetColumnNames = tableColumns.ToDictionary(
                            item => item.Name,
                            item => context.Identifiers.MapChildIdentifier(
                                table.Id, "column", table.SourceSchema, item.Name),
                            StringComparer.OrdinalIgnoreCase),
                        TargetObjectNames = context.TargetObjectNames,
                        TargetColumnTypes = tableColumns.ToDictionary(
                            item => item.Name,
                            item => context.TypeMappings.Map(item, table, context.Options).TargetType,
                            StringComparer.OrdinalIgnoreCase)
                    });
                findings.AddRange(translated.Findings);
                classification = translated.Classification;
                var checkExpression = NormalizeBooleanRoutineComparisons(
    translated.Sql,
    context);

                clause = $"CHECK ({checkExpression})";
                //clause = $"CHECK ({translated.Sql})";
                break;
            case ConstraintKind.ForeignKey:
                if (constraint.ReferencedTableObjectId is not { } referencedId ||
                    !context.ObjectsById.TryGetValue(referencedId, out var referencedTable))
                {
                    return Task.FromResult(ConversionRuleSupport.Manual(
                        source,
                        "Foreign key target is outside the resolved inventory.",
                        $"-- Foreign key {name} requires an external target mapping.",
                        "unresolved foreign key"));
                }
                var targetReference = context.Identifiers.MapObject(referencedTable);
                var referencedColumns = string.Join(
                    ", ",
                    constraint.ReferencedColumns.OrderBy(item => item.Ordinal)
                        .Select(item => context.Identifiers.MapChildIdentifier(
                            referencedTable.Id,
                            "column",
                            referencedTable.SourceSchema,
                            item.Name)));
                clause = $"FOREIGN KEY ({columns}) REFERENCES {targetReference.QualifiedName} ({referencedColumns})" +
                         Action("DELETE", constraint.DeleteAction) +
                         Action("UPDATE", constraint.UpdateAction);
                break;
            case ConstraintKind.Default:
                return Task.FromResult(ConversionRuleSupport.Success(
                    "-- Default constraint converted with its owning column.",
                    "CONSTRAINT.DEFAULT.INLINE",
                    classification: ConversionClassification.Automatic));
            default:
                throw new InvalidOperationException($"Unsupported constraint kind {constraint.Kind}.");
        }

        if (constraint.IsDisabled || constraint.IsNotTrusted)
        {
            classification = ConversionClassification.AutomaticWithWarning;
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "CONSTRAINT.SOURCE_NOT_ENFORCED",
                FindingSeverity.Warning,
                "The SQL Server constraint is disabled or untrusted; generated PostgreSQL enforcement may reject existing data."));
        }
        if (constraint.IsClustered)
        {
            findings.Add(ConversionRuleSupport.Finding(
                source,
                "CONSTRAINT.CLUSTERED_IGNORED",
                FindingSeverity.Information,
                "SQL Server clustered storage semantics are not reproduced."));
        }

        var sql = new StringBuilder("ALTER TABLE ")
            .Append(targetTable.QualifiedName)
            .Append(" ADD CONSTRAINT ")
            .Append(name)
            .Append(' ')
            .Append(clause)
            .Append(';')
            .ToString();
        return Task.FromResult(ConversionRuleSupport.Success(
            sql,
            $"CONSTRAINT.{constraint.Kind.ToString().ToUpperInvariant()}",
            findings,
            classification: classification,
            confidence: classification == ConversionClassification.Automatic ? 1m : 0.8m));
    }


    private static string NormalizeBooleanRoutineComparisons(
        string expression,
        ConversionContext context)
    {
        var result = expression;

        foreach (var module in context.Inventory.Modules)
        {
            if (module.Kind != ModuleKind.ScalarFunction)
            {
                continue;
            }

            if (!context.ObjectsById.TryGetValue(module.ObjectId, out var function))
            {
                continue;
            }

            var returnType = GetFunctionReturnType(module, context);
            if (!string.Equals(
                    returnType,
                    "boolean",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetFunction = context.Identifiers.MapObject(function).QualifiedName;
            result = RewriteBooleanFunctionComparison(result, targetFunction);
        }

        return result;
    }

    private static string? GetFunctionReturnType(
        ModuleInventory module,
        ConversionContext context)
    {
        if (module.ResultColumns.Count > 0)
        {
            var resultColumn = module.ResultColumns[0];

            return context.TypeMappings.Map(
                resultColumn.SystemTypeName,
                resultColumn.MaximumLength,
                resultColumn.Precision,
                resultColumn.Scale,
                context.Options).TargetType;
        }

        ModuleParameterInventory? returnParameter = null;

        foreach (var parameter in module.Parameters)
        {
            if (parameter.ParameterId == 0)
            {
                returnParameter = parameter;
                break;
            }
        }

        if (returnParameter is null)
        {
            return null;
        }

        return context.TypeMappings.Map(
            returnParameter.TypeName,
            returnParameter.MaximumLength,
            returnParameter.Precision,
            returnParameter.Scale,
            context.Options).TargetType;
    }

    private static string RewriteBooleanFunctionComparison(
        string expression,
        string qualifiedFunctionName)
    {
        var escapedName = Regex.Escape(qualifiedFunctionName);

        // Matches a function call with simple arguments or one level of nested parentheses.
        var functionCall =
            $@"(?<call>{escapedName}\s*\((?:[^()]|\([^()]*\))*\))";

        // fn(...) = 1  -> fn(...)
        expression = Regex.Replace(
            expression,
            $@"{functionCall}\s*=\s*\(?\s*1\s*\)?",
            "${call}",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        // fn(...) = 0  -> NOT (fn(...))
        expression = Regex.Replace(
            expression,
            $@"{functionCall}\s*=\s*\(?\s*0\s*\)?",
            "NOT (${call})",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        // fn(...) <> 0 or fn(...) != 0  -> fn(...)
        expression = Regex.Replace(
            expression,
            $@"{functionCall}\s*(?:<>|!=)\s*\(?\s*0\s*\)?",
            "${call}",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        // fn(...) <> 1 or fn(...) != 1  -> NOT (fn(...))
        expression = Regex.Replace(
            expression,
            $@"{functionCall}\s*(?:<>|!=)\s*\(?\s*1\s*\)?",
            "NOT (${call})",
            RegexOptions.IgnoreCase |
            RegexOptions.CultureInvariant);

        return expression;
    }

    private static string Action(string operation, string? action) =>
        action?.ToUpperInvariant() switch
        {
            "CASCADE" => $" ON {operation} CASCADE",
            "SET_NULL" or "SET NULL" => $" ON {operation} SET NULL",
            "SET_DEFAULT" or "SET DEFAULT" => $" ON {operation} SET DEFAULT",
            "NO_ACTION" or "NO ACTION" => $" ON {operation} NO ACTION",
            _ => string.Empty
        };
}
