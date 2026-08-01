using System.Text;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion.Converters;

namespace MigrationStudio.Infrastructure.Conversion;

public sealed class StructuredSqlExpressionTranslator : ISqlExpressionTranslator
{
    private static readonly Dictionary<string, string> FunctionMappings =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ISNULL"] = "COALESCE",
            ["LEN"] = "char_length",
            ["DATALENGTH"] = "octet_length",
            ["LEFT"] = "left",
            ["RIGHT"] = "right",
            ["SUBSTRING"] = "substring",
            ["UPPER"] = "upper",
            ["LOWER"] = "lower",
            ["LTRIM"] = "ltrim",
            ["RTRIM"] = "rtrim",
            ["CONCAT"] = "concat",
            ["CHARINDEX"] = "strpos"
        };

    private static readonly HashSet<string> UnsupportedFunctions = new(
        [
            "PATINDEX", "STUFF", "DATEADD", "DATEDIFF", "DATENAME", "IIF", "TRY_CONVERT",
            "TRY_CAST", "@@IDENTITY"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NonImmutableFunctions = new(
        [
            "GETDATE", "GETUTCDATE", "SYSDATETIME", "SYSUTCDATETIME",
            "NEWID", "NEWSEQUENTIALID", "RAND", "SUSER_SNAME"
        ],
        StringComparer.OrdinalIgnoreCase);

    public ExpressionTranslationResult Translate(
        string expression,
        ExpressionTranslationContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        ArgumentNullException.ThrowIfNull(context);

        var tokens = TSqlTokenizer.Tokenize(expression);
        var output = new StringBuilder(expression.Length + 32);
        var findings = new List<InventoryFinding>();
        var unsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var immutable = true;
        var classification = ConversionClassification.Automatic;
        var confidence = 1m;

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (IsIdentifier(token) &&
                TryRenderMappedObjectReference(
                    tokens,
                    index,
                    context.TargetObjectNames,
                    out var mappedObject,
                    out var mappedObjectEnd))
            {
                output.Append(mappedObject);
                index = mappedObjectEnd;
                continue;
            }

            if (token.Kind == TSqlTokenKind.QuotedIdentifier)
            {
                var identifier = UnquoteIdentifier(token.Text);
                if (context.TargetColumnNames.TryGetValue(identifier, out var mappedIdentifier))
                {
                    output.Append(mappedIdentifier);
                }
                else
                {
                    output.Append('"').Append(identifier.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                }
                if (context.ColumnTypes.ContainsKey(identifier))
                {
                    referencedColumns.Add(identifier);
                }
                continue;
            }

            if (token.Kind != TSqlTokenKind.Word)
            {
                if (token.Kind == TSqlTokenKind.Symbol &&
                    token.Text == "+" &&
                    IsStringConcatenation(tokens, index, context.ColumnTypes))
                {
                    output.Append("||");
                    classification = ConversionRuleSupport.Worst(
                        classification,
                        ConversionClassification.AutomaticWithWarning);
                    confidence = Math.Min(confidence, 0.85m);
                    if (!findings.Any(item => item.Code == "EXPRESSION.CONCAT_NULL_SEMANTICS"))
                    {
                        findings.Add(Finding(
                            context.SourceObjectId,
                            "EXPRESSION.CONCAT_NULL_SEMANTICS",
                            FindingSeverity.Warning,
                            "String '+' was converted to '||'; verify SQL Server CONCAT_NULL_YIELDS_NULL semantics."));
                    }
                }
                else
                {
                    output.Append(token.Text);
                }
                continue;
            }

            if (token.Text.Equals("N", StringComparison.OrdinalIgnoreCase) &&
                NextSignificant(tokens, index) is { Kind: TSqlTokenKind.String })
            {
                continue;
            }

            if (context.ColumnTypes.ContainsKey(token.Text))
            {
                referencedColumns.Add(token.Text);
                if (context.TargetColumnNames.TryGetValue(token.Text, out var mappedIdentifier))
                {
                    output.Append(mappedIdentifier);
                    continue;
                }
            }

            if (token.Text.Equals("NEXT", StringComparison.OrdinalIgnoreCase) &&
                TryTranslateNextValue(tokens, index, context, out var nextValueSql, out var sequenceEnd))
            {
                output.Append(nextValueSql);
                index = sequenceEnd;
                continue;
            }

            if (TryTranslateSpecialFunction(
                    tokens,
                    index,
                    context,
                    out var specialSql,
                    out var specialClose,
                    out var specialFinding,
                    out var specialUnsupported))
            {
                output.Append(specialSql);
                index = specialClose;
                if (specialFinding is not null)
                {
                    findings.Add(specialFinding);
                    classification = ConversionRuleSupport.Worst(
                        classification,
                        specialFinding.Severity >= FindingSeverity.Warning
                            ? ConversionClassification.AutomaticWithWarning
                            : ConversionClassification.Automatic);
                    confidence = Math.Min(confidence, 0.8m);
                }
                if (specialUnsupported is not null)
                {
                    unsupported.Add(specialUnsupported);
                    classification = ConversionClassification.ManualConversion;
                    confidence = Math.Min(confidence, 0.3m);
                }
                continue;
            }

            if (FunctionMappings.TryGetValue(token.Text, out var mapped))
            {
                output.Append(mapped);
                continue;
            }

            if (token.Text.Equals("SCOPE_IDENTITY", StringComparison.OrdinalIgnoreCase) &&
                IsEmptyFunctionCall(tokens, index, out var scopeIdentityClose))
            {
                output.Append("lastval()");
                index = scopeIdentityClose;
                immutable = false;
                classification = ConversionRuleSupport.Worst(
                    classification,
                    ConversionClassification.AutomaticWithWarning);
                confidence = Math.Min(confidence, 0.75m);
                findings.Add(Finding(
                    context.SourceObjectId,
                    "EXPRESSION.SCOPE_IDENTITY",
                    FindingSeverity.Warning,
                    "SCOPE_IDENTITY() was mapped to lastval(); validate session and trigger sequence semantics."));
                continue;
            }

            if (UnsupportedFunctions.Contains(token.Text))
            {
                unsupported.Add(token.Text);
                classification = ConversionClassification.ManualConversion;
                confidence = Math.Min(confidence, 0.3m);
                output.Append(token.Text);
                continue;
            }

            if (NonImmutableFunctions.Contains(token.Text))
            {
                immutable = false;
                if (context.IsGeneratedColumn)
                {
                    classification = ConversionClassification.ManualConversion;
                    confidence = Math.Min(confidence, 0.2m);
                }

                if (IsEmptyFunctionCall(tokens, index, out var closeIndex))
                {
                    output.Append(token.Text.ToUpperInvariant() switch
                    {
                        "GETDATE" or "SYSDATETIME" => "CURRENT_TIMESTAMP",
                        "GETUTCDATE" or "SYSUTCDATETIME" =>
                            RenderUtcCurrentTimestamp(tokens, index, closeIndex, context),
                        "NEWID" when context.Options.EnablePgCrypto => "gen_random_uuid()",
                        "NEWSEQUENTIALID" when context.Options.EnablePgCrypto &&
                                                   context.Options.UseRandomUuidForNewSequentialId =>
                            "gen_random_uuid()",
                        "SUSER_SNAME" => "CURRENT_USER",
                        "NEWID" => token.Text,
                        _ => token.Text
                    });
                    if (token.Text.Equals("NEWID", StringComparison.OrdinalIgnoreCase) &&
                        context.Options.EnablePgCrypto)
                    {
                        extensions.Add("pgcrypto");
                    }
                    if (token.Text.Equals("NEWSEQUENTIALID", StringComparison.OrdinalIgnoreCase) &&
                        context.Options.EnablePgCrypto &&
                        context.Options.UseRandomUuidForNewSequentialId)
                    {
                        extensions.Add("pgcrypto");
                        findings.Add(Finding(
                            context.SourceObjectId,
                            "EXPRESSION.NEWSEQUENTIALID",
                            FindingSeverity.Warning,
                            "NEWSEQUENTIALID was mapped to random gen_random_uuid(); ordering characteristics are not preserved."));
                        classification = ConversionRuleSupport.Worst(
                            classification,
                            ConversionClassification.AutomaticWithWarning);
                        confidence = Math.Min(confidence, 0.7m);
                    }
                    else if (token.Text.Equals("NEWSEQUENTIALID", StringComparison.OrdinalIgnoreCase))
                    {
                        unsupported.Add("NEWSEQUENTIALID");
                        classification = ConversionClassification.ManualConversion;
                        confidence = Math.Min(confidence, 0.3m);
                    }
                    index = closeIndex;
                    continue;
                }
            }

            output.Append(token.Text);
        }

        if (unsupported.Count > 0)
        {
            findings.Add(Finding(
                context.SourceObjectId,
                "EXPRESSION.UNSUPPORTED",
                FindingSeverity.Warning,
                $"Manual translation is required for: {string.Join(", ", unsupported.Order(StringComparer.OrdinalIgnoreCase))}."));
        }

        if (context.IsGeneratedColumn && !immutable)
        {
            findings.Add(Finding(
                context.SourceObjectId,
                "COMPUTED.NONIMMUTABLE",
                FindingSeverity.Error,
                "PostgreSQL generated columns require an immutable expression."));
        }

        var sql = RemoveRedundantOuterParentheses(output.ToString().Trim());
        return new ExpressionTranslationResult(
            sql,
            classification,
            confidence,
            findings,
            unsupported.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            referencedColumns.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            extensions.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            immutable);
    }

    private static string RenderUtcCurrentTimestamp(
        IReadOnlyList<TSqlToken> tokens,
        int functionIndex,
        int closeIndex,
        ExpressionTranslationContext context)
    {
        var targetType = context.ExpectedTargetType;
        if (string.IsNullOrWhiteSpace(targetType))
        {
            targetType = FindNearestTargetColumnType(
                tokens,
                functionIndex,
                closeIndex,
                context.TargetColumnTypes);
        }

        return IsTimestampWithTimeZone(targetType)
            ? "CURRENT_TIMESTAMP"
            : "timezone('UTC', CURRENT_TIMESTAMP)";
    }

    private static string? FindNearestTargetColumnType(
        IReadOnlyList<TSqlToken> tokens,
        int functionIndex,
        int closeIndex,
        IReadOnlyDictionary<string, string> targetColumnTypes)
    {
        if (targetColumnTypes.Count == 0)
        {
            return null;
        }

        for (var distance = 1; functionIndex - distance >= 0 ||
             closeIndex + distance < tokens.Count; distance++)
        {
            if (functionIndex - distance >= 0 &&
                TryGetTargetColumnType(
                    tokens[functionIndex - distance],
                    targetColumnTypes,
                    out var before))
            {
                return before;
            }
            if (closeIndex + distance < tokens.Count &&
                TryGetTargetColumnType(
                    tokens[closeIndex + distance],
                    targetColumnTypes,
                    out var after))
            {
                return after;
            }
        }

        return null;
    }

    private static bool TryGetTargetColumnType(
        TSqlToken token,
        IReadOnlyDictionary<string, string> targetColumnTypes,
        out string targetType)
    {
        targetType = string.Empty;
        if (token.Kind is not TSqlTokenKind.Word and not TSqlTokenKind.QuotedIdentifier)
        {
            return false;
        }

        return targetColumnTypes.TryGetValue(IdentifierText(token), out targetType!);
    }

    private static bool IsTimestampWithTimeZone(string? targetType)
    {
        if (string.IsNullOrWhiteSpace(targetType))
        {
            return false;
        }

        var normalized = string.Join(
            ' ',
            targetType.Trim().ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized == "timestamptz" ||
               normalized.StartsWith("timestamp", StringComparison.Ordinal) &&
               normalized.Contains("with time zone", StringComparison.Ordinal);
    }

    private static bool TryRenderMappedObjectReference(
        IReadOnlyList<TSqlToken> tokens,
        int startIndex,
        IReadOnlyDictionary<string, string> targetObjectNames,
        out string mappedObject,
        out int endIndex)
    {
        mappedObject = string.Empty;
        endIndex = startIndex;
        if (targetObjectNames.Count == 0)
        {
            return false;
        }

        var firstDot = NextSignificantIndex(tokens, startIndex);
        var secondIdentifier = firstDot >= 0 ? NextSignificantIndex(tokens, firstDot) : -1;
        if (!IsDot(tokens, firstDot) || !IsIdentifier(tokens, secondIdentifier))
        {
            return false;
        }

        var secondDot = NextSignificantIndex(tokens, secondIdentifier);
        var thirdIdentifier = secondDot >= 0 ? NextSignificantIndex(tokens, secondDot) : -1;
        if (IsDot(tokens, secondDot) && IsIdentifier(tokens, thirdIdentifier))
        {
            var threePartKey = MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey(
                IdentifierText(tokens[secondIdentifier]),
                IdentifierText(tokens[thirdIdentifier]));
            if (targetObjectNames.TryGetValue(threePartKey, out var mappedThreePart))
            {
                mappedObject = mappedThreePart;
                endIndex = thirdIdentifier;
                return true;
            }
        }

        var twoPartKey = MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey(
            IdentifierText(tokens[startIndex]),
            IdentifierText(tokens[secondIdentifier]));
        if (!targetObjectNames.TryGetValue(twoPartKey, out var mappedTwoPart))
        {
            return false;
        }

        mappedObject = mappedTwoPart;
        endIndex = secondIdentifier;
        return true;
    }

    private static bool IsDot(IReadOnlyList<TSqlToken> tokens, int index) =>
        index >= 0 &&
        tokens[index].Kind == TSqlTokenKind.Symbol &&
        tokens[index].Text == ".";

    private static bool IsIdentifier(IReadOnlyList<TSqlToken> tokens, int index) =>
        index >= 0 && IsIdentifier(tokens[index]);

    private static bool IsIdentifier(TSqlToken token) =>
        token.Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier;

    private static string IdentifierText(TSqlToken token) =>
        token.Kind == TSqlTokenKind.QuotedIdentifier
            ? UnquoteIdentifier(token.Text)
            : token.Text;

    private static bool IsEmptyFunctionCall(
        IReadOnlyList<TSqlToken> tokens,
        int functionIndex,
        out int closeIndex)
    {
        var open = NextSignificantIndex(tokens, functionIndex);
        var close = open >= 0 ? NextSignificantIndex(tokens, open) : -1;
        closeIndex = close;
        return open >= 0 && close >= 0 && tokens[open].Text == "(" && tokens[close].Text == ")";
    }

    private static bool TryTranslateNextValue(
        IReadOnlyList<TSqlToken> tokens,
        int nextIndex,
        ExpressionTranslationContext context,
        out string sql,
        out int endIndex)
    {
        sql = string.Empty;
        endIndex = nextIndex;
        var valueIndex = NextSignificantIndex(tokens, nextIndex);
        var forIndex = valueIndex >= 0 ? NextSignificantIndex(tokens, valueIndex) : -1;
        var nameIndex = forIndex >= 0 ? NextSignificantIndex(tokens, forIndex) : -1;
        if (valueIndex < 0 || forIndex < 0 || nameIndex < 0 ||
            !tokens[valueIndex].Text.Equals("VALUE", StringComparison.OrdinalIgnoreCase) ||
            !tokens[forIndex].Text.Equals("FOR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var builder = new StringBuilder();
        for (var index = nameIndex; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind is TSqlTokenKind.Word or TSqlTokenKind.QuotedIdentifier ||
                token.Kind == TSqlTokenKind.Symbol && token.Text == "." ||
                token.Kind == TSqlTokenKind.Whitespace)
            {
                builder.Append(token.Text);
                endIndex = index;
                continue;
            }
            break;
        }
        var sourceName = builder.ToString().Trim();
        if (!SqlObjectName.TryParse(sourceName, out var parsed) || parsed is null)
        {
            return false;
        }

        if (parsed.Schema is not null)
        {
            var sourceKey = MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey(
                parsed.Schema,
                parsed.Name);
            if (context.TargetObjectNames.TryGetValue(sourceKey, out var mappedSequence))
            {
                sql = $"nextval('{mappedSequence.Replace("'", "''", StringComparison.Ordinal)}'::regclass)";
                return true;
            }
        }

        var options = context.Options;
        var schema = parsed.Schema;
        if (schema is not null)
        {
            var rule = options.SchemaMappings.FirstOrDefault(item =>
                string.Equals(item.SourceSchema, schema, StringComparison.OrdinalIgnoreCase));
            schema = rule?.TargetSchema ??
                     (options.SchemaMappingMode == SchemaMappingMode.MapDboToPublic &&
                      schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                         ? "public"
                         : options.SchemaMappingMode == SchemaMappingMode.MapAllToOne
                             ? options.ConsolidatedSchema
                             : schema);
        }
        var normalized = options.IdentifierCaseMode is IdentifierCaseMode.LowercaseUnquoted
            or IdentifierCaseMode.QuoteOnlyWhenRequired
            ? new SqlObjectName(schema?.ToLowerInvariant(), parsed.Name.ToLowerInvariant())
            : new SqlObjectName(schema, parsed.Name);
        var regclass = normalized.Schema is null
            ? normalized.Name
            : $"{normalized.Schema}.{normalized.Name}";
        sql = $"nextval('{regclass.Replace("'", "''", StringComparison.Ordinal)}'::regclass)";
        return true;
    }

    private bool TryTranslateSpecialFunction(
        IReadOnlyList<TSqlToken> tokens,
        int functionIndex,
        ExpressionTranslationContext context,
        out string sql,
        out int closeIndex,
        out InventoryFinding? finding,
        out string? unsupported)
    {
        sql = string.Empty;
        closeIndex = functionIndex;
        finding = null;
        unsupported = null;
        var name = tokens[functionIndex].Text.ToUpperInvariant();
        if (name is not ("IIF" or "DATEPART" or "DATEADD" or "DATEDIFF" or
            "CHARINDEX" or "CONVERT" or "ISJSON"))
        {
            return false;
        }
        if (!TryReadArguments(tokens, functionIndex, out var arguments, out closeIndex))
        {
            unsupported = name;
            sql = tokens[functionIndex].Text;
            return true;
        }
        var translated = arguments.Select(argument => Translate(argument, context).Sql).ToArray();
        switch (name)
        {
            case "IIF" when translated.Length == 3:
                sql = $"(CASE WHEN {translated[0]} THEN {translated[1]} ELSE {translated[2]} END)";
                return true;
            case "DATEPART" when translated.Length == 2:
                sql = $"EXTRACT({NormalizeDatePart(translated[0])} FROM {translated[1]})";
                return true;
            case "DATEADD" when translated.Length == 3:
                var part = NormalizeDatePart(translated[0]);
                sql = $"({translated[2]} + ({translated[1]}) * INTERVAL '1 {part}')";
                finding = Finding(
                    context.SourceObjectId,
                    "EXPRESSION.DATEADD",
                    FindingSeverity.Warning,
                    "DATEADD was converted to interval arithmetic; validate end-of-month behavior.");
                return true;
            case "DATEDIFF" when translated.Length == 3:
                var unit = NormalizeDatePart(translated[0]);
                if (unit == "year")
                {
                    sql =
                        $"(EXTRACT(YEAR FROM {translated[2]})::integer - " +
                        $"EXTRACT(YEAR FROM {translated[1]})::integer)";
                    finding = Finding(
                        context.SourceObjectId,
                        "EXPRESSION.DATEDIFF",
                        FindingSeverity.Warning,
                        "DATEDIFF year boundary semantics were converted to calendar-year subtraction.");
                    return true;
                }
                var divisor = unit switch
                {
                    "day" => "86400",
                    "hour" => "3600",
                    "minute" => "60",
                    "second" => "1",
                    _ => null
                };
                if (divisor is null)
                {
                    sql = $"DATEDIFF({string.Join(", ", translated)})";
                    unsupported = $"DATEDIFF {unit}";
                    return true;
                }
                sql = $"trunc(EXTRACT(EPOCH FROM ({translated[2]} - {translated[1]})) / {divisor})";
                finding = Finding(
                    context.SourceObjectId,
                    "EXPRESSION.DATEDIFF",
                    FindingSeverity.Warning,
                    "DATEDIFF boundary semantics differ from elapsed-time arithmetic and require validation.");
                return true;
            case "CHARINDEX" when translated.Length is 2 or 3:
                sql = translated.Length == 2
                    ? $"strpos({translated[1]}, {translated[0]})"
                    : $"(strpos(substr({translated[1]}, {translated[2]}), {translated[0]}) + {translated[2]} - 1)";
                return true;
            case "CONVERT" when translated.Length >= 2:
                var targetType = MapConvertType(translated[0]);
                sql = targetType == "boolean" &&
                      TryMapBooleanLiteral(translated[1], out var booleanLiteral)
                    ? booleanLiteral
                    : $"CAST({translated[1]} AS {targetType})";
                if (translated.Length > 2)
                {
                    finding = Finding(
                        context.SourceObjectId,
                        "EXPRESSION.CONVERT_STYLE",
                        FindingSeverity.Warning,
                        "SQL Server CONVERT style was not reproduced exactly.");
                }
                return true;
            case "ISJSON" when translated.Length == 1:
                sql = $"(CASE WHEN {translated[0]} IS JSON THEN 1 ELSE 0 END)";
                return true;
            default:
                sql = $"{name}({string.Join(", ", translated)})";
                unsupported = name;
                return true;
        }
    }

    private static bool TryReadArguments(
        IReadOnlyList<TSqlToken> tokens,
        int functionIndex,
        out IReadOnlyList<string> arguments,
        out int closeIndex)
    {
        arguments = [];
        closeIndex = functionIndex;
        var open = NextSignificantIndex(tokens, functionIndex);
        if (open < 0 || tokens[open].Text != "(")
        {
            return false;
        }
        var depth = 0;
        var current = new StringBuilder();
        var result = new List<string>();
        for (var index = open + 1; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind is not TSqlTokenKind.String and not TSqlTokenKind.Comment)
            {
                if (token.Text == "(")
                {
                    depth++;
                }
                else if (token.Text == ")" && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    arguments = result;
                    closeIndex = index;
                    return true;
                }
                else if (token.Text == ")")
                {
                    depth--;
                }
                else if (token.Text == "," && depth == 0)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }
            }
            current.Append(token.Text);
        }
        return false;
    }

    private static bool IsStringConcatenation(
        IReadOnlyList<TSqlToken> tokens,
        int operatorIndex,
        IReadOnlyDictionary<string, string> columnTypes)
    {
        var previous = PreviousSignificantIndex(tokens, operatorIndex);
        var next = NextSignificantIndex(tokens, operatorIndex);
        return previous >= 0 && IsStringOperand(tokens[previous], columnTypes) ||
               next >= 0 &&
               (IsStringOperand(tokens[next], columnTypes) ||
                tokens[next].Kind == TSqlTokenKind.Word &&
                tokens[next].Text.Equals("N", StringComparison.OrdinalIgnoreCase) &&
                NextSignificantIndex(tokens, next) is var stringIndex &&
                stringIndex >= 0 &&
                tokens[stringIndex].Kind == TSqlTokenKind.String);
    }

    private static int PreviousSignificantIndex(IReadOnlyList<TSqlToken> tokens, int index)
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

    private static bool IsStringOperand(
        TSqlToken token,
        IReadOnlyDictionary<string, string> columnTypes)
    {
        if (token.Kind == TSqlTokenKind.String)
        {
            return true;
        }
        var name = token.Kind == TSqlTokenKind.QuotedIdentifier ? UnquoteIdentifier(token.Text) : token.Text;
        return columnTypes.TryGetValue(name, out var type) &&
               (type.Contains("char", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("text", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("xml", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDatePart(string value) =>
        value.Trim().Trim('"', '\'', '[', ']').ToLowerInvariant() switch
        {
            "dd" or "d" => "day",
            "hh" => "hour",
            "mi" or "n" => "minute",
            "ss" or "s" => "second",
            "mm" or "m" => "month",
            "yy" or "yyyy" => "year",
            var part => part
        };

    private static string MapConvertType(string value) =>
        value.Trim().Trim('"', '\'', '[', ']').ToLowerInvariant() switch
        {
            "int" => "integer",
            "bigint" => "bigint",
            "bit" => "boolean",
            "uniqueidentifier" => "uuid",
            "datetime" or "datetime2" => "timestamp",
            "nvarchar(max)" or "varchar(max)" => "text",
            var type => type
        };

    private static bool TryMapBooleanLiteral(string value, out string sql)
    {
        var normalized = value.Trim().Trim('(', ')');
        if (normalized == "1")
        {
            sql = "TRUE";
            return true;
        }
        if (normalized == "0")
        {
            sql = "FALSE";
            return true;
        }
        sql = string.Empty;
        return false;
    }

    private static TSqlToken? NextSignificant(IReadOnlyList<TSqlToken> tokens, int index)
    {
        var next = NextSignificantIndex(tokens, index);
        return next < 0 ? null : tokens[next];
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

    private static string UnquoteIdentifier(string value)
    {
        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            return value[1..^1].Replace("]]", "]", StringComparison.Ordinal);
        }
        return value[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
    }

    private static string RemoveRedundantOuterParentheses(string value)
    {
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')' && WrapsWholeExpression(value))
        {
            value = value[1..^1].Trim();
        }
        return value;
    }

    private static bool WrapsWholeExpression(string value)
    {
        var depth = 0;
        var tokens = TSqlTokenizer.Tokenize(value);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Kind is TSqlTokenKind.String or TSqlTokenKind.Comment)
            {
                continue;
            }
            if (token.Text == "(")
            {
                depth++;
            }
            else if (token.Text == ")")
            {
                depth--;
                if (depth == 0 && index < tokens.Count - 1)
                {
                    return tokens.Skip(index + 1).All(item => item.Kind == TSqlTokenKind.Whitespace);
                }
            }
        }
        return depth == 0;
    }

    private static InventoryFinding Finding(
        InventoryObjectId id,
        string code,
        FindingSeverity severity,
        string message) =>
        new(code, severity, message, id, null);
}
