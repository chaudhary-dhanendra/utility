using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Conversion.Converters;

public sealed partial class ProgrammableObjectConverter(
    ILogger<ProgrammableObjectConverter>? logger = null) :
    IObjectConverter<InventoryObject, string>
{
    public bool CanConvert(InventoryObject source, ConversionContext context) =>
        source.ObjectType is InventoryObjectType.View or InventoryObjectType.StoredProcedure or
            InventoryObjectType.Function or InventoryObjectType.Trigger or InventoryObjectType.DatabaseTrigger or
            InventoryObjectType.ServerTrigger;

    public Task<ConversionResult<string>> ConvertAsync(
        InventoryObject source,
        ConversionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogDiagnosticTrigger(source, context);
        var module = context.Inventory.Modules.FirstOrDefault(item => item.ObjectId == source.Id);
        if (module is null || string.IsNullOrWhiteSpace(source.SourceDefinition) || module.IsEncrypted)
        {
            return Task.FromResult(ManualSkeleton(
                source,
                context,
                module,
                module?.IsEncrypted == true
                    ? "Encrypted source definition is unavailable."
                    : "Programmable-object definition is unavailable.",
                "definition unavailable"));
        }

        return Task.FromResult(module.Kind switch
        {
            ModuleKind.View => ConvertView(source, module, context),
            ModuleKind.ScalarFunction => ConvertScalarFunction(source, module, context),
            ModuleKind.InlineTableValuedFunction => ConvertInlineFunction(source, module, context),
            ModuleKind.MultiStatementTableValuedFunction => ManualSkeleton(
                source, context, module, "Multi-statement table-valued functions require procedural and return-table review.", "multi-statement TVF"),
            ModuleKind.StoredProcedure => ConvertProcedure(source, module, context),
            ModuleKind.DmlTrigger => ConvertTrigger(source, module, context),
            ModuleKind.DdlTrigger or ModuleKind.ServerTrigger => ManualSkeleton(
                source, context, module, "Database/server DDL triggers have no direct portable PostgreSQL equivalent.", "DDL trigger"),
            _ when module.Kind.ToString().StartsWith("Clr", StringComparison.Ordinal) ||
                   module.Kind == ModuleKind.AggregateFunction => ManualSkeleton(
                source, context, module, "CLR programmable objects require an explicit extension implementation.", "CLR module"),
            _ => ManualSkeleton(source, context, module, $"Module kind {module.Kind} is not automatically converted.", module.Kind.ToString())
        });
    }

    private static ConversionResult<string> ConvertView(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var body = ExtractAfterAs(source.SourceDefinition!);
        if (body is null)
        {
            return ManualSkeleton(source, context, null, "View query body could not be identified.", "view header");
        }

        var transformed = TransformBody(body, source, module, context, out var unsupported);
        if (ContainsAny(
                body,
                "TOP PERCENT",
                "WITH TIES",
                "FOR XML",
                "OPENQUERY",
                "OPENROWSET",
                "CROSS APPLY",
                "OUTER APPLY",
                "NOLOCK",
                "UPDLOCK",
                "HOLDLOCK",
                "#") ||
            ContainsWord(transformed, "TOP") ||
            HasMultipartName(body, minimumParts: 3))
        {
            unsupported.Add("non-portable view construct");
        }
        if (unsupported.Count > 0)
        {
            return ConversionRuleSupport.Manual(
                source,
                $"View contains constructs requiring review: {string.Join(", ", unsupported)}.",
                $"CREATE OR REPLACE VIEW {context.Identifiers.MapObject(source).QualifiedName} AS{Environment.NewLine}SELECT NULL::text AS manual_review WHERE false;",
                unsupported.ToArray());
        }

        return ConversionRuleSupport.Success(
            $"CREATE OR REPLACE VIEW {context.Identifiers.MapObject(source).QualifiedName} AS{Environment.NewLine}{transformed.Trim().TrimEnd(';')};",
            "VIEW.STRUCTURED",
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.8m);
    }

    private static ConversionResult<string> ConvertScalarFunction(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var body = ExtractAfterAs(source.SourceDefinition!);
        if (body is not null && ContainsProceduralScalarStatements(body))
        {
            return ConvertProceduralScalarFunction(source, module, context, body);
        }

        var returnExpression = ExtractReturnExpression(source.SourceDefinition!);
        if (returnExpression is null || ContainsAny(source.SourceDefinition!, "BEGIN TRY", "BEGIN CATCH", "EXEC(", "CURSOR", "#"))
        {
            return ManualSkeleton(source, context, module, "Scalar function body is not a single safely translatable return expression.", "procedural scalar function");
        }

        var returnType = MapReturnType(module, context);
        if (returnType is null)
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Scalar function return type could not be mapped.",
                "unmapped return type");
        }

        if (TryUnwrapQueryReturn(returnExpression, out var query))
        {
            var transformedQuery = TransformBody(
                query,
                source,
                module,
                context,
                out var queryUnsupported);
            if (queryUnsupported.Count > 0)
            {
                return ManualSkeleton(
                    source,
                    context,
                    module,
                    "Scalar query-returning function contains unsupported SQL.",
                    queryUnsupported.ToArray());
            }

            var querySql = RewriteVariables(
                transformedQuery.Trim().TrimEnd(';'),
                module,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return ConversionRuleSupport.Success(
                $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
                $"({BuildParameters(module, context)}) RETURNS {returnType}{Environment.NewLine}" +
                $"LANGUAGE sql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
                $"    {querySql};{Environment.NewLine}$migrationstudio$;",
                "FUNCTION.SCALAR.QUERY",
                classification: ConversionClassification.AutomaticWithWarning,
                confidence: 0.85m);
        }

        var parameterTypes = BuildParameterTypeMap(module);
        var translated = context.Expressions.Translate(
            returnExpression,
            new ExpressionTranslationContext(source.Id, parameterTypes, context.Options, false)
            {
                TargetObjectNames = context.TargetObjectNames,
                TargetColumnTypes = BuildTargetExpressionTypeMap(source, module, context),
                ExpectedTargetType = returnType
            });
        if (translated.Classification == ConversionClassification.ManualConversion || returnType is null)
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Scalar function return expression or return type requires manual conversion.",
                translated.UnsupportedFunctions.ToArray());
        }

        var sql = $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
                  $"({BuildParameters(module, context)}) RETURNS {returnType}{Environment.NewLine}" +
                  $"LANGUAGE sql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
                  $"    SELECT {RewriteVariables(
                      translated.Sql,
                      module,
                      new HashSet<string>(StringComparer.OrdinalIgnoreCase))};{Environment.NewLine}$migrationstudio$;";
        return ConversionRuleSupport.Success(
            sql,
            "FUNCTION.SCALAR.SQL",
            translated.Findings,
            extensions: translated.RequiredExtensions,
            classification: translated.Classification,
            confidence: translated.Confidence);
    }

    private static ConversionResult<string> ConvertInlineFunction(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var returnExpression = ExtractReturnExpression(source.SourceDefinition!);
        if (returnExpression is null)
        {
            return ManualSkeleton(source, context, module, "Inline TVF query could not be identified.", "inline TVF body");
        }
        var transformed = TransformBody(
            returnExpression.Trim().Trim('(', ')'),
            source,
            module,
            context,
            out var unsupported);
        if (unsupported.Count > 0)
        {
            return ManualSkeleton(source, context, module, "Inline TVF contains unsupported constructs.", unsupported.ToArray());
        }

        var columns = module.ResultColumns.OrderBy(item => item.OrdinalPosition).Select(column =>
        {
            var mapped = context.TypeMappings.Map(
                column.SystemTypeName,
                column.MaximumLength,
                column.Precision,
                column.Scale,
                context.Options);
            return $"{context.Identifiers.MapChildIdentifier(
                source.Id, "field", source.SourceSchema, column.Name)} {mapped.TargetType}";
        });
        var returns = module.ResultColumns.Count > 0
            ? $"TABLE ({string.Join(", ", columns)})"
            : "SETOF record";
        var sql = $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
                  $"({BuildParameters(module, context)}) RETURNS {returns}{Environment.NewLine}" +
                  $"LANGUAGE sql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
                  $"{RewriteVariables(
                      transformed,
                      module,
                      new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Trim().TrimEnd(';')};{Environment.NewLine}$migrationstudio$;";
        return ConversionRuleSupport.Success(
            sql,
            "FUNCTION.INLINE_TVF",
            classification: module.ResultColumns.Count > 0
                ? ConversionClassification.AutomaticWithWarning
                : ConversionClassification.ManualConversion,
            confidence: module.ResultColumns.Count > 0 ? 0.75m : 0.3m);
    }

    private static ConversionResult<string> ConvertProcedure(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var body = ExtractAfterAs(source.SourceDefinition!);
        var unsupported = new List<string>();
        if (body is null ||
            ContainsAny(body, "RAISERROR", "THROW", "BEGIN TRY", "BEGIN CATCH", "CURSOR", "MERGE", "OUTPUT", "EXEC(", "SP_EXECUTESQL", "#", " TABLE ", "WHILE "))
        {
            unsupported.Add("procedural construct requiring PL/pgSQL review");
        }
        if (module.ContainsDynamicSql)
        {
            unsupported.Add("dynamic SQL");
        }
        if (module.UsesTemporaryTables)
        {
            unsupported.Add("temporary tables");
        }
        if (unsupported.Count > 0 || body is null)
        {
            return ManualSkeleton(source, context, module, "Stored procedure semantics cannot be converted safely without manual review.", unsupported.ToArray());
        }

        var transformed = TransformBody(
            body,
            source,
            module,
            context,
            out var translationUnsupported);
        if (translationUnsupported.Count > 0)
        {
            return ManualSkeleton(source, context, module, "Stored procedure contains unsupported expressions.", translationUnsupported.ToArray());
        }
        if (ContainsResultSetSelect(body))
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Stored procedure returns a SQL Server result set; choose a PostgreSQL refcursor, OUT parameters, or a set-returning function.",
                "result-set SELECT interface");
        }
        transformed = RemoveSqlServerSessionStatements(transformed);
        transformed = TranslateSelectAssignments(StripBeginEnd(transformed));
        var sql = $"CREATE OR REPLACE PROCEDURE {context.Identifiers.MapObject(source).QualifiedName}" +
                  $"({BuildParameters(module, context)}){Environment.NewLine}" +
                  $"LANGUAGE plpgsql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
                  $"BEGIN{Environment.NewLine}{Indent(
                      RewriteVariables(
                          transformed,
                          module,
                          new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                      4)}{Environment.NewLine}" +
                  $"END;{Environment.NewLine}$migrationstudio$;";
        return ConversionRuleSupport.Success(
            sql,
            "PROCEDURE.PLPGSQL",
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.65m);
    }

    private static bool ContainsResultSetSelect(string body) =>
        Regex.IsMatch(
            body,
            @"(?im)^\s*SELECT\s+(?!@\w+\s*=)",
            RegexOptions.CultureInvariant);

    private static string TranslateSelectAssignments(string body) =>
        Regex.Replace(
            body,
            @"(?im)^\s*SELECT\s+@(?<variable>\w+)\s*=\s*(?<expression>.+?);\s*$",
            match =>
                $"SELECT {match.Groups["expression"].Value} INTO p_{match.Groups["variable"].Value};",
            RegexOptions.CultureInvariant);

    private static ConversionResult<string> ConvertTrigger(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var trigger = context.Inventory.Triggers.FirstOrDefault(item => item.ObjectId == source.Id);
        if (trigger?.ParentObjectId is not { } parentId ||
            !context.ObjectsById.TryGetValue(parentId, out var table) ||
            trigger.IsInsteadOf ||
            trigger.IsDisabled)
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Only enabled AFTER DML triggers with a resolved parent are converted automatically.",
                "INSTEAD OF, disabled, or unresolved trigger");
        }

        var body = ExtractAfterAs(source.SourceDefinition!);
        if (body is null || ContainsAny(body, "UPDATE(", "COLUMNS_UPDATED", "CURSOR", "EXEC(", "COMMIT", "ROLLBACK"))
        {
            return ManualSkeleton(source, context, module, "Trigger contains SQL Server-specific statement semantics.", "trigger semantics");
        }

        var transformed = TransformBody(
            StripBeginEnd(body),
            source,
            module,
            context,
            out var unsupported);
        if (unsupported.Count > 0)
        {
            return ManualSkeleton(source, context, module, "Trigger body contains unsupported expressions.", unsupported.ToArray());
        }

        var target = context.Identifiers.MapObject(source);
        var tableTarget = context.Identifiers.MapObject(table);
        var functionName = context.Identifiers.MapChildIdentifier(source.Id, "trigger_function", source.SourceSchema, $"{source.SourceName}_fn");
        var events = string.Join(" OR ", trigger.Events.Select(item => item.ToUpperInvariant()));
        var referencing = new List<string>();
        if (trigger.Events.Any(item => item.Equals("INSERT", StringComparison.OrdinalIgnoreCase) ||
                                       item.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)))
        {
            referencing.Add("NEW TABLE AS inserted");
        }
        if (trigger.Events.Any(item => item.Equals("DELETE", StringComparison.OrdinalIgnoreCase) ||
                                       item.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)))
        {
            referencing.Add("OLD TABLE AS deleted");
        }

        var sql = $"CREATE OR REPLACE FUNCTION {target.Schema}.{functionName}() RETURNS trigger{Environment.NewLine}" +
                  $"LANGUAGE plpgsql AS $migrationstudio${Environment.NewLine}BEGIN{Environment.NewLine}" +
                  $"{Indent(
                      RewriteVariables(
                          transformed,
                          module,
                          new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
                      4)}{Environment.NewLine}    RETURN NULL;{Environment.NewLine}" +
                  $"END;{Environment.NewLine}$migrationstudio$;{Environment.NewLine}{Environment.NewLine}" +
                  $"CREATE TRIGGER {target.Name} AFTER {events} ON {tableTarget.QualifiedName}{Environment.NewLine}" +
                  $"{(referencing.Count > 0 ? $"REFERENCING {string.Join(" ", referencing)}{Environment.NewLine}" : string.Empty)}" +
                  $"FOR EACH STATEMENT EXECUTE FUNCTION {target.Schema}.{functionName}();";
        return ConversionRuleSupport.Success(
            sql,
            "TRIGGER.STATEMENT_TRANSITION_TABLES",
            [
                ConversionRuleSupport.Finding(
                    source,
                    "TRIGGER.SEMANTICS_REVIEW",
                    FindingSeverity.Warning,
                    "The trigger uses PostgreSQL statement-level transition tables; ordering and recursive-trigger behavior require validation.")
            ],
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.6m);
    }

    private static ConversionResult<string> ManualSkeleton(
        InventoryObject source,
        ConversionContext context,
        ModuleInventory? module,
        string reason,
        params string[] unsupported)
    {
        var target = context.Identifiers.MapObject(source);
        var sourceComment = EscapeComment(source.SourceDefinition ?? "Definition unavailable");
        var skeleton = source.ObjectType switch
        {
            InventoryObjectType.View =>
                $"CREATE OR REPLACE VIEW {target.QualifiedName} AS SELECT NULL::text AS manual_review WHERE false;{Environment.NewLine}/* Source T-SQL:{Environment.NewLine}{sourceComment}{Environment.NewLine}*/",
            InventoryObjectType.StoredProcedure =>
                $"CREATE OR REPLACE PROCEDURE {target.QualifiedName}({BuildParameters(module, context)}) LANGUAGE plpgsql AS $migrationstudio${Environment.NewLine}BEGIN{Environment.NewLine}    RAISE EXCEPTION 'Manual conversion required for {ConversionRuleSupport.EscapeLiteral(source.QualifiedSourceName)}';{Environment.NewLine}END;{Environment.NewLine}$migrationstudio$;{Environment.NewLine}/* Source T-SQL:{Environment.NewLine}{sourceComment}{Environment.NewLine}*/",
            InventoryObjectType.Function =>
                $"CREATE OR REPLACE FUNCTION {target.QualifiedName}({BuildParameters(module, context)}) RETURNS void LANGUAGE plpgsql AS $migrationstudio${Environment.NewLine}BEGIN{Environment.NewLine}    RAISE EXCEPTION 'Manual conversion required for {ConversionRuleSupport.EscapeLiteral(source.QualifiedSourceName)}';{Environment.NewLine}END;{Environment.NewLine}$migrationstudio$;{Environment.NewLine}/* Source T-SQL:{Environment.NewLine}{sourceComment}{Environment.NewLine}*/",
            _ => $"-- Manual trigger conversion required for {target.QualifiedName}.{Environment.NewLine}/* Source T-SQL:{Environment.NewLine}{sourceComment}{Environment.NewLine}*/"
        };
        return ConversionRuleSupport.Manual(source, reason, skeleton, unsupported);
    }

    private static string BuildParameters(ModuleInventory? module, ConversionContext context)
    {
        if (module is null)
        {
            return string.Empty;
        }
        return string.Join(
            ", ",
            module.Parameters.OrderBy(item => item.ParameterId).Select(parameter =>
            {
                var mapped = context.TypeMappings.Map(
                    parameter.TypeName,
                    parameter.MaximumLength,
                    parameter.Precision,
                    parameter.Scale,
                    context.Options);
                if (parameter.ParameterId == 0)
                {
                    return null;
                }
                var direction = parameter.IsOutput ? "INOUT " : string.Empty;
                var name = parameter.Name.TrimStart('@');
                var owner = context.ObjectsById[module.ObjectId];
                var targetName = context.Identifiers.MapChildIdentifier(
                    module.ObjectId,
                    "parameter",
                    owner.SourceSchema,
                    $"p_{name}");
                var defaultValue = parameter.HasDefaultValue && parameter.DefaultValue is not null
                    ? $" DEFAULT {parameter.DefaultValue}"
                    : string.Empty;
                return $"{direction}{targetName} {mapped.TargetType}{defaultValue}";
            }).Where(item => item is not null));
    }

    private static string? MapReturnType(ModuleInventory module, ConversionContext context)
    {
        var result = module.ResultColumns.Count == 0 ? null : module.ResultColumns[0];
        if (result is not null)
        {
            return context.TypeMappings.Map(
                result.SystemTypeName,
                result.MaximumLength,
                result.Precision,
                result.Scale,
                context.Options).TargetType;
        }
        var returnParameter = module.Parameters.FirstOrDefault(item => item.ParameterId == 0);
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

    private static Dictionary<string, string> BuildParameterTypeMap(ModuleInventory module) =>
        module.Parameters.ToDictionary(
            item => item.Name,
            item => item.TypeName,
            StringComparer.OrdinalIgnoreCase);

    private static string TransformBody(
        string body,
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        out List<string> unsupported)
    {
        var translated = context.Expressions.Translate(
            body,
            new ExpressionTranslationContext(source.Id, new Dictionary<string, string>(), context.Options, false)
            {
                TargetObjectNames = context.TargetObjectNames,
                TargetColumnTypes = BuildTargetExpressionTypeMap(source, module, context)
            });
        unsupported = translated.UnsupportedFunctions.ToList();
        var sql = MapKnownIdentifiers(
            RemoveNoLockHints(translated.Sql),
            source,
            context);
        if (TryRemoveSimpleTop(sql, out var withoutTop, out var limit))
        {
            sql = $"{withoutTop.Trim().TrimEnd(';')} LIMIT {limit};";
        }
        return sql;
    }

    private static bool ContainsProceduralScalarStatements(string body)
    {
        var tokens = TSqlTokenizer.Tokenize(body);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TSqlTokenKind.Word)
            {
                continue;
            }

            if (tokens[index].Text.Equals("DECLARE", StringComparison.OrdinalIgnoreCase) ||
                tokens[index].Text.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
                tokens[index].Text.Equals("IF", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (tokens[index].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var variable = NextSignificantIndex(tokens, index);
                var equals = variable >= 0 ? NextSignificantIndex(tokens, variable) : -1;
                if (variable >= 0 && equals >= 0 &&
                    tokens[variable].Kind == TSqlTokenKind.Word &&
                    tokens[variable].Text.StartsWith('@') &&
                    tokens[equals].Text == "=")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static ConversionResult<string> ConvertProceduralScalarFunction(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        string body)
    {
        if (ContainsAny(body, "BEGIN TRY", "BEGIN CATCH", "CURSOR", "EXEC(", "SP_EXECUTESQL", "#", "WHILE "))
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Procedural scalar function contains unsupported control-flow or dynamic SQL.",
                "unsupported procedural scalar construct");
        }

        if (TryConvertGuardReturnBody(body, source, module, context, out var guardSql))
        {
            return ConversionRuleSupport.Success(
                guardSql,
                "FUNCTION.SCALAR.PLPGSQL.GUARD",
                classification: ConversionClassification.AutomaticWithWarning,
                confidence: 0.8m);
        }

        if (TryConvertSimpleIfReturnBody(body, source, module, context, out var simpleIfSql))
        {
            return ConversionRuleSupport.Success(
                simpleIfSql,
                "FUNCTION.SCALAR.PLPGSQL.IF",
                classification: ConversionClassification.AutomaticWithWarning,
                confidence: 0.8m);
        }

        if (!TryParseProceduralScalarBody(
                body,
                source,
                module,
                context,
                out var declarations,
                out var statements,
                out var unsupportedReason))
        {
            return ManualSkeleton(
                source,
                context,
                module,
                $"Procedural scalar function could not be converted safely: {unsupportedReason}",
                unsupportedReason);
        }

        var returnType = MapReturnType(module, context);
        if (returnType is null)
        {
            return ManualSkeleton(
                source,
                context,
                module,
                "Procedural scalar function return type could not be mapped.",
                "unmapped return type");
        }

        var declarationSql = string.Join(
            Environment.NewLine,
            declarations.Select(item =>
                $"    {item.TargetName} {item.TargetType}" +
                (item.Initializer is null ? ";" : $" := {item.Initializer};")));
        var statementSql = string.Join(
            Environment.NewLine,
            statements.Select(item => $"    {item.Trim().TrimEnd(';')};"));
        var sql =
            $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
            $"({BuildParameters(module, context)}) RETURNS {returnType}{Environment.NewLine}" +
            $"LANGUAGE plpgsql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
            (declarations.Count == 0
                ? string.Empty
                : $"DECLARE{Environment.NewLine}{declarationSql}{Environment.NewLine}") +
            $"BEGIN{Environment.NewLine}{statementSql}{Environment.NewLine}" +
            $"END;{Environment.NewLine}$migrationstudio$;";
        return ConversionRuleSupport.Success(
            sql,
            "FUNCTION.SCALAR.PLPGSQL",
            classification: ConversionClassification.AutomaticWithWarning,
            confidence: 0.75m);
    }

    private static bool TryConvertGuardReturnBody(
        string body,
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        out string sql)
    {
        sql = string.Empty;
        var tokens = TSqlTokenizer.Tokenize(body);
        var ifIndex = FindWord(tokens, 0, tokens.Count, "IF");
        if (ifIndex < 0 ||
            FindWord(tokens, 0, ifIndex, "DECLARE") >= 0 ||
            FindWord(tokens, 0, tokens.Count, "SET") >= 0 ||
            FindWord(tokens, 0, tokens.Count, "SELECT") >= 0)
        {
            return false;
        }

        var guardReturn = FindWord(tokens, ifIndex + 1, tokens.Count, "RETURN");
        var guardTerminator = guardReturn >= 0
            ? FindSymbol(tokens, guardReturn + 1, tokens.Count, ";")
            : -1;
        var finalReturn = guardTerminator >= 0
            ? FindWord(tokens, guardTerminator + 1, tokens.Count, "RETURN")
            : -1;
        if (guardReturn < 0 || guardTerminator < 0 || finalReturn < 0 ||
            FindWord(tokens, guardTerminator + 1, finalReturn, "ELSE") >= 0)
        {
            return false;
        }

        var finalEnd = TrimTrailingStatementTerminator(
            tokens,
            finalReturn + 1,
            FindRoutineEndAfterExpression(tokens, finalReturn + 1));
        var returnType = MapReturnType(module, context);
        if (returnType is null)
        {
            return false;
        }

        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var condition = TranslateProceduralExpression(
            TokenText(tokens, ifIndex + 1, guardReturn),
            source,
            module,
            context,
            locals,
            "boolean");
        var guardExpression = TranslateProceduralExpression(
            TokenText(tokens, guardReturn + 1, guardTerminator),
            source,
            module,
            context,
            locals,
            returnType);
        var finalExpression = TranslateProceduralExpression(
            TokenText(tokens, finalReturn + 1, finalEnd),
            source,
            module,
            context,
            locals,
            returnType);
        if (string.IsNullOrWhiteSpace(condition) ||
            string.IsNullOrWhiteSpace(guardExpression) ||
            string.IsNullOrWhiteSpace(finalExpression))
        {
            return false;
        }

        sql =
            $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
            $"({BuildParameters(module, context)}) RETURNS {returnType}{Environment.NewLine}" +
            $"LANGUAGE plpgsql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
            $"BEGIN{Environment.NewLine}" +
            $"    IF {condition} THEN{Environment.NewLine}" +
            $"        RETURN {guardExpression};{Environment.NewLine}" +
            $"    END IF;{Environment.NewLine}" +
            $"    RETURN {finalExpression};{Environment.NewLine}" +
            $"END;{Environment.NewLine}$migrationstudio$;";
        return true;
    }

    private static bool TryConvertSimpleIfReturnBody(
        string body,
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        out string sql)
    {
        sql = string.Empty;
        var tokens = TSqlTokenizer.Tokenize(body);
        var ifIndex = FindWord(tokens, 0, tokens.Count, "IF");
        if (ifIndex < 0)
        {
            return false;
        }
        if (FindWord(tokens, 0, ifIndex, "DECLARE") >= 0 ||
            FindWord(tokens, 0, tokens.Count, "SET") >= 0 ||
            FindWord(tokens, 0, tokens.Count, "SELECT") >= 0)
        {
            return false;
        }

        var trueReturn = FindWord(tokens, ifIndex + 1, tokens.Count, "RETURN");
        var elseIndex = trueReturn >= 0
            ? FindWord(tokens, trueReturn + 1, tokens.Count, "ELSE")
            : -1;
        var falseReturn = elseIndex >= 0
            ? FindWord(tokens, elseIndex + 1, tokens.Count, "RETURN")
            : -1;
        if (trueReturn < 0 || elseIndex < 0 || falseReturn < 0)
        {
            return false;
        }

        var end = TrimTrailingControlTokens(tokens, falseReturn + 1, tokens.Count);
        var locals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var condition = TranslateProceduralExpression(
            TokenText(tokens, ifIndex + 1, trueReturn),
            source,
            module,
            context,
            locals,
            "boolean");
        var whenTrue = TranslateProceduralExpression(
            TokenText(tokens, trueReturn + 1, elseIndex),
            source,
            module,
            context,
            locals,
            MapReturnType(module, context));
        var whenFalse = TranslateProceduralExpression(
            TokenText(tokens, falseReturn + 1, end),
            source,
            module,
            context,
            locals,
            MapReturnType(module, context));
        var returnType = MapReturnType(module, context);
        if (returnType is null ||
            string.IsNullOrWhiteSpace(condition) ||
            string.IsNullOrWhiteSpace(whenTrue) ||
            string.IsNullOrWhiteSpace(whenFalse))
        {
            return false;
        }

        sql =
            $"CREATE OR REPLACE FUNCTION {context.Identifiers.MapObject(source).QualifiedName}" +
            $"({BuildParameters(module, context)}) RETURNS {returnType}{Environment.NewLine}" +
            $"LANGUAGE plpgsql{Environment.NewLine}AS $migrationstudio${Environment.NewLine}" +
            $"BEGIN{Environment.NewLine}" +
            $"    IF {condition} THEN{Environment.NewLine}" +
            $"        RETURN {whenTrue};{Environment.NewLine}" +
            $"    ELSE{Environment.NewLine}" +
            $"        RETURN {whenFalse};{Environment.NewLine}" +
            $"    END IF;{Environment.NewLine}" +
            $"END;{Environment.NewLine}$migrationstudio$;";
        return true;
    }

    private static int FindWord(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end,
        string word)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Word &&
                tokens[index].Text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindSymbol(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end,
        string symbol)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Text == symbol)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindRoutineEndAfterExpression(
        IReadOnlyList<TSqlToken> tokens,
        int start)
    {
        var caseDepth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TSqlTokenKind.Word)
            {
                continue;
            }
            if (tokens[index].Text.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            {
                caseDepth++;
            }
            else if (tokens[index].Text.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                if (caseDepth > 0)
                {
                    caseDepth--;
                }
                else
                {
                    return index;
                }
            }
        }
        return tokens.Count;
    }

    private static int TrimTrailingStatementTerminator(
        IReadOnlyList<TSqlToken> tokens,
        int minimum,
        int end)
    {
        var cursor = end;
        while (cursor > minimum &&
               tokens[cursor - 1].Kind is TSqlTokenKind.Whitespace or TSqlTokenKind.Comment)
        {
            cursor--;
        }
        if (cursor > minimum && tokens[cursor - 1].Text == ";")
        {
            cursor--;
        }
        while (cursor > minimum &&
               tokens[cursor - 1].Kind is TSqlTokenKind.Whitespace or TSqlTokenKind.Comment)
        {
            cursor--;
        }
        return cursor;
    }

    private static bool TryParseProceduralScalarBody(
        string body,
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        out List<ProceduralLocal> declarations,
        out List<string> statements,
        out string unsupportedReason)
    {
        declarations = [];
        statements = [];
        unsupportedReason = string.Empty;
        var tokens = TSqlTokenizer.Tokenize(body);
        var starts = FindProceduralStatementStarts(tokens);
        var localTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (start, ordinal) in starts.Select((value, index) => (value, index)))
        {
            var end = ordinal + 1 < starts.Count ? starts[ordinal + 1] : tokens.Count;
            end = TrimTrailingControlTokens(tokens, start + 1, end);
            var keyword = tokens[start].Text.ToUpperInvariant();
            if (keyword == "DECLARE")
            {
                var variableIndex = NextSignificantIndex(tokens, start);
                if (variableIndex < 0 || variableIndex >= end ||
                    !tokens[variableIndex].Text.StartsWith('@'))
                {
                    unsupportedReason = "malformed DECLARE statement";
                    return false;
                }

                var equalsIndex = FindTopLevelSymbol(tokens, variableIndex + 1, end, "=");
                var typeEnd = equalsIndex >= 0 ? equalsIndex : end;
                var sourceType = TokenText(tokens, variableIndex + 1, typeEnd).Trim();
                if (!TryMapLocalType(sourceType, context, out var targetType))
                {
                    unsupportedReason = $"unsupported local variable type '{sourceType}'";
                    return false;
                }

                var sourceName = tokens[variableIndex].Text[1..];
                var targetName = $"v_{sourceName.ToLowerInvariant()}";
                if (!localNames.Add(sourceName))
                {
                    unsupportedReason = $"duplicate local variable '@{sourceName}'";
                    return false;
                }
                localTypes[sourceName] = targetType;
                string? initializer = null;
                if (equalsIndex >= 0)
                {
                    initializer = TranslateProceduralExpression(
                        TokenText(tokens, equalsIndex + 1, end),
                        source,
                        module,
                        context,
                        localNames,
                        targetType);
                }
                declarations.Add(new ProceduralLocal(
                    sourceName,
                    targetName,
                    targetType,
                    initializer));
                continue;
            }

            if (keyword == "SET")
            {
                var variableIndex = NextSignificantIndex(tokens, start);
                var equalsIndex = variableIndex >= 0
                    ? NextSignificantIndex(tokens, variableIndex)
                    : -1;
                if (!TryResolveLocal(tokens, variableIndex, localNames, out var local) ||
                    equalsIndex < 0 || tokens[equalsIndex].Text != "=")
                {
                    unsupportedReason = "SET must assign a declared local variable";
                    return false;
                }

                var expression = TranslateProceduralExpression(
                    TokenText(tokens, equalsIndex + 1, end),
                    source,
                    module,
                    context,
                    localNames,
                    localTypes[local]);
                statements.Add($"v_{local.ToLowerInvariant()} := {expression}");
                continue;
            }

            if (keyword == "SELECT")
            {
                var variableIndex = NextSignificantIndex(tokens, start);
                var equalsIndex = variableIndex >= 0
                    ? NextSignificantIndex(tokens, variableIndex)
                    : -1;
                if (!TryResolveLocal(tokens, variableIndex, localNames, out var local) ||
                    equalsIndex < 0 || tokens[equalsIndex].Text != "=")
                {
                    unsupportedReason = "only SELECT assignment to a declared local is supported";
                    return false;
                }

                var fromIndex = FindTopLevelWord(tokens, equalsIndex + 1, end, "FROM");
                var expressionEnd = fromIndex >= 0 ? fromIndex : end;
                var expression = TranslateProceduralExpression(
                    TokenText(tokens, equalsIndex + 1, expressionEnd),
                    source,
                    module,
                    context,
                    localNames,
                    localTypes[local]);
                var tailUnsupported = new List<string>();
                var tail = fromIndex < 0
                    ? string.Empty
                    : TransformBody(
                        TokenText(tokens, fromIndex, end),
                        source,
                        module,
                        context,
                        out tailUnsupported);
                if (fromIndex >= 0 && tailUnsupported.Count > 0)
                {
                    unsupportedReason =
                        $"unsupported SELECT assignment: {string.Join(", ", tailUnsupported)}";
                    return false;
                }
                tail = RewriteVariables(tail, module, localNames);
                statements.Add(
                    $"SELECT {expression} INTO v_{local.ToLowerInvariant()}" +
                    (string.IsNullOrWhiteSpace(tail) ? string.Empty : $" {tail.Trim()}"));
                continue;
            }

            if (keyword == "RETURN")
            {
                var expression = TranslateProceduralExpression(
                    TokenText(tokens, start + 1, end),
                    source,
                    module,
                    context,
                    localNames,
                    returnType: null);
                if (string.IsNullOrWhiteSpace(expression))
                {
                    unsupportedReason = "empty RETURN expression";
                    return false;
                }
                statements.Add($"RETURN {expression}");
                continue;
            }

            unsupportedReason = $"unsupported procedural statement '{tokens[start].Text}'";
            return false;
        }

        if (statements.Count == 0 ||
            !statements.Any(item => item.TrimStart().StartsWith("RETURN ", StringComparison.OrdinalIgnoreCase)))
        {
            unsupportedReason = "procedural function has no supported RETURN statement";
            return false;
        }

        return true;
    }

    private static string TranslateProceduralExpression(
        string expression,
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context,
        HashSet<string> localNames,
        string? returnType)
    {
        var translated = context.Expressions.Translate(
            expression.Trim(),
            new ExpressionTranslationContext(
                source.Id,
                BuildParameterTypeMap(module),
                context.Options,
                false)
            {
                TargetObjectNames = context.TargetObjectNames,
                TargetColumnTypes = BuildTargetExpressionTypeMap(source, module, context),
                ExpectedTargetType = returnType
            });
        var rewritten = RewriteVariables(
            MapKnownIdentifiers(RemoveNoLockHints(translated.Sql), source, context),
            module,
            localNames);
        return NormalizeBooleanAssignment(rewritten, returnType);
    }

    private static List<int> FindProceduralStatementStarts(
        IReadOnlyList<TSqlToken> tokens)
    {
        var result = new List<int>();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TSqlTokenKind.Word)
            {
                continue;
            }

            var keyword = tokens[index].Text.ToUpperInvariant();
            if (keyword is "DECLARE" or "SET" or "RETURN")
            {
                result.Add(index);
                continue;
            }
            if (keyword == "SELECT")
            {
                var variable = NextSignificantIndex(tokens, index);
                var equals = variable >= 0 ? NextSignificantIndex(tokens, variable) : -1;
                if (variable >= 0 && equals >= 0 &&
                    tokens[variable].Text.StartsWith('@') &&
                    tokens[equals].Text == "=")
                {
                    result.Add(index);
                }
            }
        }
        return result;
    }

    private static int TrimTrailingControlTokens(
        IReadOnlyList<TSqlToken> tokens,
        int minimum,
        int end)
    {
        var cursor = end;
        while (cursor > minimum)
        {
            var token = tokens[cursor - 1];
            if (token.Kind is TSqlTokenKind.Whitespace or TSqlTokenKind.Comment ||
                token.Text == ";" ||
                token.Kind == TSqlTokenKind.Word &&
                (token.Text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                 token.Text.Equals("END", StringComparison.OrdinalIgnoreCase)))
            {
                cursor--;
                continue;
            }
            break;
        }
        return cursor;
    }

    private static int FindTopLevelSymbol(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end,
        string symbol)
    {
        var depth = 0;
        for (var index = start; index < end; index++)
        {
            depth += tokens[index].Text switch
            {
                "(" => 1,
                ")" => -1,
                _ => 0
            };
            if (depth == 0 && tokens[index].Text == symbol)
            {
                return index;
            }
        }
        return -1;
    }

    private static int FindTopLevelWord(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end,
        string word)
    {
        var depth = 0;
        var caseDepth = 0;
        for (var index = start; index < end; index++)
        {
            var token = tokens[index];
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                depth--;
            }
            else if (token.Kind == TSqlTokenKind.Word &&
                     token.Text.Equals("CASE", StringComparison.OrdinalIgnoreCase))
            {
                caseDepth++;
            }
            else if (token.Kind == TSqlTokenKind.Word &&
                     token.Text.Equals("END", StringComparison.OrdinalIgnoreCase) &&
                     caseDepth > 0)
            {
                caseDepth--;
            }
            else if (depth == 0 && caseDepth == 0 &&
                     token.Kind == TSqlTokenKind.Word &&
                     token.Text.Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static bool TryResolveLocal(
        IReadOnlyList<TSqlToken> tokens,
        int index,
        HashSet<string> locals,
        out string local)
    {
        local = string.Empty;
        if (index < 0 || index >= tokens.Count ||
            tokens[index].Kind != TSqlTokenKind.Word ||
            !tokens[index].Text.StartsWith('@'))
        {
            return false;
        }
        local = tokens[index].Text[1..];
        return locals.Contains(local);
    }

    private static bool TryMapLocalType(
        string sourceType,
        ConversionContext context,
        out string targetType)
    {
        targetType = string.Empty;
        var match = Regex.Match(
            sourceType,
            @"^\s*(?<type>[\w.]+)\s*(?:\(\s*(?<size>max|\d+)(?:\s*,\s*(?<scale>\d+))?\s*\))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var size = match.Groups["size"].Value;
        var maximumLength = size.Equals("max", StringComparison.OrdinalIgnoreCase)
            ? (short)-1
            : short.TryParse(size, out var parsedSize)
                ? parsedSize
                : (short)0;
        var scale = byte.TryParse(match.Groups["scale"].Value, out var parsedScale)
            ? parsedScale
            : (byte)0;
        var mapped = context.TypeMappings.Map(
            match.Groups["type"].Value,
            maximumLength,
            18,
            scale,
            context.Options);
        if (mapped.Classification is ConversionClassification.ManualConversion or
            ConversionClassification.Unsupported)
        {
            return false;
        }
        targetType = mapped.TargetType;
        return true;
    }

    private static string NormalizeBooleanAssignment(
        string expression,
        string? targetType)
    {
        if (!string.Equals(targetType, "boolean", StringComparison.OrdinalIgnoreCase))
        {
            return expression;
        }

        var tokens = TSqlTokenizer.Tokenize(expression).ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Number &&
                tokens[index].Text is "0" or "1")
            {
                var previous = PreviousSignificant(tokens.ToList(), index);
                if (tokens.Length == 1 ||
                    previous >= 0 &&
                    tokens[previous].Kind == TSqlTokenKind.Word &&
                    tokens[previous].Text.ToUpperInvariant() is "THEN" or "ELSE")
                {
                    tokens[index] = tokens[index] with
                    {
                        Kind = TSqlTokenKind.Word,
                        Text = tokens[index].Text == "1" ? "true" : "false"
                    };
                }
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static string RemoveNoLockHints(string sql)
    {
        var tokens = TSqlTokenizer.Tokenize(sql).ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != "(")
            {
                continue;
            }
            var hint = NextSignificant(tokens, index);
            var close = hint >= 0 ? NextSignificant(tokens, hint) : -1;
            if (hint >= 0 && close >= 0 &&
                tokens[hint].Kind == TSqlTokenKind.Word &&
                tokens[hint].Text.Equals("NOLOCK", StringComparison.OrdinalIgnoreCase) &&
                tokens[close].Text == ")")
            {
                tokens.RemoveRange(index, close - index + 1);
                index--;
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static bool TryUnwrapQueryReturn(
        string expression,
        out string query)
    {
        var tokens = TSqlTokenizer.Tokenize(expression).ToList();
        var first = NextSignificant(tokens, -1);
        var last = PreviousSignificant(tokens, tokens.Count);
        while (first >= 0 && last > first &&
               tokens[first].Text == "(" &&
               tokens[last].Text == ")" &&
               MatchingCloseParenthesis(tokens, first) == last)
        {
            tokens.RemoveAt(last);
            tokens.RemoveAt(first);
            first = NextSignificant(tokens, -1);
            last = PreviousSignificant(tokens, tokens.Count);
        }
        query = string.Concat(tokens.Select(item => item.Text)).Trim();
        first = NextSignificant(tokens, -1);
        return first >= 0 &&
               tokens[first].Kind == TSqlTokenKind.Word &&
               tokens[first].Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase);
    }

    private static int MatchingCloseParenthesis(
        List<TSqlToken> tokens,
        int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")" && --depth == 0)
            {
                return index;
            }
        }
        return -1;
    }

    private static string TokenText(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end) =>
        string.Concat(tokens.Skip(start).Take(Math.Max(0, end - start))
            .Select(item => item.Text));

    private sealed record ProceduralLocal(
        string SourceName,
        string TargetName,
        string TargetType,
        string? Initializer);

    private static Dictionary<string, string> BuildTargetExpressionTypeMap(
        InventoryObject source,
        ModuleInventory module,
        ConversionContext context)
    {
        var candidates = new List<(string Name, string TargetType)>();
        candidates.AddRange(module.Parameters
            .Where(item => item.ParameterId != 0)
            .Select(item => (
                item.Name,
                context.TypeMappings.Map(
                    item.TypeName,
                    item.MaximumLength,
                    item.Precision,
                    item.Scale,
                    context.Options).TargetType)));

        var dependencyIds = context.Inventory.Dependencies
            .Where(item => item.SourceObjectId == source.Id && item.TargetObjectId is not null)
            .Select(item => item.TargetObjectId!.Value)
            .ToHashSet();
        var relatedTableIds = context.Inventory.Objects
            .Where(item =>
                item.ObjectType is InventoryObjectType.Table or InventoryObjectType.ExternalTable &&
                (dependencyIds.Contains(item.Id) ||
                 item.SourceSchema.Equals(source.SourceSchema, StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Id)
            .ToHashSet();
        foreach (var column in context.Inventory.Columns.Where(item =>
                     relatedTableIds.Contains(item.ParentObjectId)))
        {
            if (!context.ObjectsById.TryGetValue(column.ParentObjectId, out var table))
            {
                continue;
            }

            candidates.Add((
                column.Name,
                context.TypeMappings.Map(column, table, context.Options).TargetType));
        }

        return candidates
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(item => item.TargetType)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1)
            .ToDictionary(
                group => group.Key,
                group => group.First().TargetType,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string MapKnownIdentifiers(
        string sql,
        InventoryObject source,
        ConversionContext context)
    {
        var tokens = TSqlTokenizer.Tokenize(sql).ToList();
        var dependencyIds = context.Inventory.Dependencies
            .Where(item => item.SourceObjectId == source.Id && item.TargetObjectId is not null)
            .Select(item => item.TargetObjectId!.Value)
            .ToHashSet();
        var relatedObjects = context.Inventory.Objects
            .Where(item =>
                item.Id != source.Id &&
                item.ObjectType is InventoryObjectType.Table or InventoryObjectType.View &&
                (dependencyIds.Contains(item.Id) ||
                 item.SourceSchema.Equals(source.SourceSchema, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        for (var index = 0; index < tokens.Count; index++)
        {
            if (!IsIdentifier(tokens[index]))
            {
                continue;
            }

            var dot = NextSignificant(tokens, index);
            var nameIndex = dot >= 0 ? NextSignificant(tokens, dot) : -1;
            if (dot >= 0 && nameIndex >= 0 && tokens[dot].Text == "." && IsIdentifier(tokens[nameIndex]))
            {
                var secondDot = NextSignificant(tokens, nameIndex);
                var thirdIndex = secondDot >= 0 ? NextSignificant(tokens, secondDot) : -1;
                if (secondDot >= 0 && thirdIndex >= 0 &&
                    tokens[secondDot].Text == "." &&
                    IsIdentifier(tokens[thirdIndex]))
                {
                    var schema = Unquote(tokens[nameIndex].Text);
                    var objectName = Unquote(tokens[thirdIndex].Text);
                    var threePartObject = FindObject(context, schema, objectName);
                    if (threePartObject is not null)
                    {
                        ReplaceRange(
                            tokens,
                            index,
                            thirdIndex,
                            context.Identifiers.MapObject(threePartObject).QualifiedName);
                        continue;
                    }
                }

                var qualifier = Unquote(tokens[index].Text);
                var identifier = Unquote(tokens[nameIndex].Text);
                var qualifiedObject = FindObject(context, qualifier, identifier);
                if (qualifiedObject is not null)
                {
                    ReplaceRange(
                        tokens,
                        index,
                        nameIndex,
                        context.Identifiers.MapObject(qualifiedObject).QualifiedName);
                    continue;
                }

                var qualifiedTable = relatedObjects
                    .Where(item => item.SourceName.Equals(
                        qualifier,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (qualifiedTable.Length == 1 &&
                    FindColumn(context, qualifiedTable, identifier) is { } qualifiedColumn)
                {
                    tokens[index] = tokens[index] with
                    {
                        Text = context.Identifiers.MapObject(qualifiedTable[0]).Name
                    };
                    tokens[nameIndex] = tokens[nameIndex] with
                    {
                        Text = MapColumn(context, qualifiedTable[0], qualifiedColumn)
                    };
                    continue;
                }

                if (FindColumn(context, relatedObjects, identifier) is { } aliasColumn &&
                    context.ObjectsById.TryGetValue(aliasColumn.ParentObjectId, out var aliasTable))
                {
                    tokens[index] = tokens[index] with
                    {
                        Text = QuoteUnmappedIdentifier(context, qualifier)
                    };
                    tokens[nameIndex] = tokens[nameIndex] with
                    {
                        Text = MapColumn(context, aliasTable, aliasColumn)
                    };
                    continue;
                }
            }

            var singleIdentifier = Unquote(tokens[index].Text);
            if (IsObjectReferencePosition(tokens, index))
            {
                var matchingObjects = relatedObjects.Where(item =>
                        item.SourceName.Equals(singleIdentifier, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matchingObjects.Length == 1)
                {
                    tokens[index] = tokens[index] with
                    {
                        Text = context.Identifiers.MapObject(matchingObjects[0]).QualifiedName
                    };
                    continue;
                }
            }

            if (FindColumn(context, relatedObjects, singleIdentifier) is { } column &&
                context.ObjectsById.TryGetValue(column.ParentObjectId, out var table))
            {
                tokens[index] = tokens[index] with { Text = MapColumn(context, table, column) };
            }
            else if (tokens[index].Kind == TSqlTokenKind.QuotedIdentifier)
            {
                tokens[index] = tokens[index] with
                {
                    Text = QuoteUnmappedIdentifier(context, singleIdentifier)
                };
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static InventoryObject? FindObject(
        ConversionContext context,
        string schema,
        string name)
    {
        var matches = context.Inventory.Objects.Where(item =>
                item.SourceSchema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                item.SourceName.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static ColumnInventory? FindColumn(
        ConversionContext context,
        IReadOnlyCollection<InventoryObject> tables,
        string name)
    {
        var tableIds = tables.Select(item => item.Id).ToHashSet();
        var matches = context.Inventory.Columns.Where(item =>
                tableIds.Contains(item.ParentObjectId) &&
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string MapColumn(
        ConversionContext context,
        InventoryObject table,
        ColumnInventory column) =>
        context.Identifiers.MapChildIdentifier(
            table.Id,
            "column",
            table.SourceSchema,
            column.Name);

    private static bool IsIdentifier(TSqlToken token) =>
        token.Kind is TSqlTokenKind.QuotedIdentifier or TSqlTokenKind.Word;

    private static bool IsObjectReferencePosition(
        List<TSqlToken> tokens,
        int index)
    {
        var previous = PreviousSignificant(tokens, index);
        return previous >= 0 &&
               tokens[previous].Kind == TSqlTokenKind.Word &&
               tokens[previous].Text.ToUpperInvariant() is
                   "FROM" or "JOIN" or "UPDATE" or "INTO" or "REFERENCES" or "CALL" or "EXEC";
    }

    private static int PreviousSignificant(List<TSqlToken> tokens, int index)
    {
        for (var previous = index - 1; previous >= 0; previous--)
        {
            if (tokens[previous].Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            {
                return previous;
            }
        }
        return -1;
    }

    private static void ReplaceRange(
        List<TSqlToken> tokens,
        int start,
        int end,
        string replacement)
    {
        tokens[start] = tokens[start] with { Text = replacement };
        for (var remove = start + 1; remove <= end; remove++)
        {
            tokens[remove] = tokens[remove] with { Text = string.Empty };
        }
    }

    private static string QuoteUnmappedIdentifier(
        ConversionContext context,
        string identifier) =>
        context.Options.IdentifierCaseMode is IdentifierCaseMode.LowercaseUnquoted
            or IdentifierCaseMode.QuoteOnlyWhenRequired
            ? context.Identifiers.QuoteIdentifier(identifier.ToLowerInvariant())
            : context.Identifiers.QuoteIdentifier(identifier);

    private static string Unquote(string value) =>
        PostgreSqlIdentifierQuoter.Unquote(value);

    private static bool TryRemoveSimpleTop(string sql, out string transformed, out string limit)
    {
        var tokens = TSqlTokenizer.Tokenize(sql).ToList();
        transformed = sql;
        limit = string.Empty;
        var select = tokens.FindIndex(item => item.Kind == TSqlTokenKind.Word &&
                                              item.Text.Equals("SELECT", StringComparison.OrdinalIgnoreCase));
        if (select < 0)
        {
            return false;
        }
        var top = NextSignificant(tokens, select);
        if (top < 0 || !tokens[top].Text.Equals("TOP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var value = NextSignificant(tokens, top);
        if (value < 0 || tokens[value].Kind != TSqlTokenKind.Number)
        {
            return false;
        }
        limit = tokens[value].Text;
        tokens[top] = tokens[top] with { Text = string.Empty };
        tokens[value] = tokens[value] with { Text = string.Empty };
        transformed = string.Concat(tokens.Select(item => item.Text));
        return true;
    }

    private static int NextSignificant(List<TSqlToken> tokens, int index)
    {
        for (var next = index + 1; next < tokens.Count; next++)
        {
            if (tokens[next].Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            {
                return next;
            }
        }
        return -1;
    }

    private static string? ExtractAfterAs(string definition)
    {
        var tokens = TSqlTokenizer.Tokenize(definition);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Word &&
                tokens[index].Text.Equals("AS", StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(tokens.Skip(index + 1).Select(item => item.Text)).Trim();
            }
        }
        return null;
    }

    private static string? ExtractReturnExpression(string definition)
    {
        var tokens = TSqlTokenizer.Tokenize(definition);
        var returnIndex = -1;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Word &&
                tokens[index].Text.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
            {
                returnIndex = index;
            }
        }
        if (returnIndex < 0)
        {
            return null;
        }
        var result = new StringBuilder();
        foreach (var token in tokens.Skip(returnIndex + 1))
        {
            if (token.Text == ";" ||
                token.Kind == TSqlTokenKind.Word && token.Text.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            result.Append(token.Text);
        }
        return result.ToString().Trim();
    }

    private static string RewriteVariables(
        string sql,
        ModuleInventory module,
        HashSet<string> localNames)
    {
        var tokens = TSqlTokenizer.Tokenize(sql);
        var output = new StringBuilder(sql.Length);
        var parameterNames = module.Parameters
            .Where(item => item.ParameterId != 0)
            .Select(item => item.Name.TrimStart('@'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind == TSqlTokenKind.Word && token.Text.Equals("SET", StringComparison.OrdinalIgnoreCase))
            {
                var variable = NextSignificantIndex(tokens, index);
                var equals = variable >= 0 ? NextSignificantIndex(tokens, variable) : -1;
                if (variable >= 0 && equals >= 0 &&
                    tokens[variable].Kind == TSqlTokenKind.Word &&
                    tokens[variable].Text.StartsWith('@') &&
                    tokens[equals].Text == "=")
                {
                    var sourceName = tokens[variable].Text[1..];
                    output.Append(localNames.Contains(sourceName) ? "v_" : "p_")
                        .Append(sourceName.ToLowerInvariant())
                        .Append(" :=");
                    index = equals;
                    continue;
                }
            }
            if (token.Kind == TSqlTokenKind.Word && token.Text.StartsWith('@'))
            {
                var sourceName = token.Text[1..];
                if (localNames.Contains(sourceName))
                {
                    output.Append("v_").Append(sourceName.ToLowerInvariant());
                }
                else if (parameterNames.Contains(sourceName))
                {
                    output.Append("p_").Append(sourceName.ToLowerInvariant());
                }
                else
                {
                    output.Append(token.Text);
                }
            }
            else
            {
                output.Append(token.Text);
            }
        }
        return output.ToString();
    }

    private static int NextSignificantIndex(IReadOnlyList<TSqlToken> tokens, int index)
    {
        for (var next = index + 1; next < tokens.Count; next++)
        {
            if (tokens[next].Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            {
                return next;
            }
        }
        return -1;
    }

    private static string StripBeginEnd(string sql)
    {
        var trimmed = sql.Trim();
        if (trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[5..].TrimStart();
        }
        if (trimmed.EndsWith("END", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd();
        }
        return trimmed;
    }

    private static string RemoveSqlServerSessionStatements(string sql) =>
        Regex.Replace(
            sql,
            @"(?im)^\s*SET\s+(?:NOCOUNT|XACT_ABORT)\s+ON\s*;\s*$",
            string.Empty,
            RegexOptions.CultureInvariant);

    private static bool ContainsAny(string value, params string[] fragments) =>
        fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    private static bool ContainsWord(string sql, string word) =>
        TSqlTokenizer.Tokenize(sql).Any(token =>
            token.Kind == TSqlTokenKind.Word &&
            token.Text.Equals(word, StringComparison.OrdinalIgnoreCase));

    private static bool HasMultipartName(string sql, int minimumParts)
    {
        var tokens = TSqlTokenizer.Tokenize(sql);
        var parts = 0;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is not TSqlTokenKind.Word and not TSqlTokenKind.QuotedIdentifier)
            {
                continue;
            }
            parts = 1;
            var cursor = index;
            while (true)
            {
                var dot = NextSignificantIndex(tokens, cursor);
                var name = dot >= 0 ? NextSignificantIndex(tokens, dot) : -1;
                if (dot < 0 || name < 0 || tokens[dot].Text != "." ||
                    tokens[name].Kind is not TSqlTokenKind.Word and not TSqlTokenKind.QuotedIdentifier)
                {
                    break;
                }
                parts++;
                cursor = name;
            }
            if (parts >= minimumParts)
            {
                return true;
            }
        }
        return false;
    }

    private static string Indent(string value, int spaces)
    {
        var prefix = new string(' ', spaces);
        return prefix + value.Replace(Environment.NewLine, Environment.NewLine + prefix, StringComparison.Ordinal);
    }

    private static string EscapeComment(string value) =>
        value.Replace("*/", "* /", StringComparison.Ordinal).Length <= 32_000
            ? value.Replace("*/", "* /", StringComparison.Ordinal)
            : value.Replace("*/", "* /", StringComparison.Ordinal)[..32_000] + Environment.NewLine + "-- truncated";

    private void LogDiagnosticTrigger(
        InventoryObject source,
        ConversionContext context)
    {
        if (logger is null ||
            source.ObjectType != InventoryObjectType.Trigger ||
            !source.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) ||
            !source.SourceName.Equals(
                "TRG_DigiPay_TrainerDetailsHistory_Del",
                StringComparison.OrdinalIgnoreCase) ||
            !logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var mapping = context.Identifiers.Mappings.SingleOrDefault(item =>
            item.SourceKey.ObjectId == source.Id);
        var canonicalKey = mapping?.SourceKey.TriggerKey?.ToString() ?? string.Empty;
        LogIdentifierUse(
            logger,
            "TriggerConverter",
            context.Identifiers.MappingSetId,
            context.Identifiers.SchemaVersion,
            canonicalKey,
            mapping?.TargetQualifiedName ?? string.Empty,
            mapping is not null);
    }

    [LoggerMessage(
        EventId = 2221,
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
