using System.Text;
using System.Globalization;
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

        if (IsBareEmptyStringExpression(expression) &&
            context.ExpectedTargetType is { } expectedType &&
            (IsNumericType(expectedType) || IsTemporalType(expectedType)))
        {
            var temporal = IsTemporalType(expectedType);
            return new ExpressionTranslationResult(
                temporal ? "DATE '1900-01-01'" : "0",
                ConversionClassification.AutomaticWithWarning,
                0.9m,
                [Finding(
                    context.SourceObjectId,
                    temporal ? "EXPRESSION.EMPTY_TEMPORAL_DEFAULT" : "EXPRESSION.EMPTY_NUMERIC_DEFAULT",
                    FindingSeverity.Warning,
                    temporal
                        ? "An empty-string temporal default was converted to SQL Server's 1900-01-01 compatibility value."
                        : "An empty-string numeric default was converted to SQL Server's zero compatibility value.")],
                [],
                [],
                [],
                true);
        }

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
            if (TryTranslateTemporalDateLiteralComparison(
                    tokens,
                    index,
                    context,
                    out var dateComparisonSql,
                    out var dateComparisonEnd,
                    out var dateComparisonFinding))
            {
                output.Append(dateComparisonSql);
                index = dateComparisonEnd;
                findings.Add(dateComparisonFinding);
                classification = ConversionRuleSupport.Worst(
                    classification,
                    ConversionClassification.AutomaticWithWarning);
                confidence = Math.Min(confidence, 0.9m);
                continue;
            }
            if (TryTranslateTypedEmptyStringComparison(
                    tokens,
                    index,
                    context,
                    out var emptyComparisonSql,
                    out var emptyComparisonEnd,
                    out var emptyComparisonFinding))
            {
                output.Append(emptyComparisonSql);
                index = emptyComparisonEnd;
                findings.Add(emptyComparisonFinding);
                classification = ConversionRuleSupport.Worst(
                    classification,
                    ConversionClassification.AutomaticWithWarning);
                confidence = Math.Min(confidence, 0.8m);
                continue;
            }

            if (TryTranslateEmptyStringCast(
                    tokens,
                    index,
                    context,
                    out var emptyCastSql,
                    out var emptyCastEnd,
                    out var emptyCastFinding))
            {
                output.Append(emptyCastSql);
                index = emptyCastEnd;
                findings.Add(emptyCastFinding);
                classification = ConversionRuleSupport.Worst(
                    classification,
                    ConversionClassification.AutomaticWithWarning);
                confidence = Math.Min(confidence, 0.75m);
                continue;
            }

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

        var sql = RewriteRoutineArgumentCasts(
            RewriteTimestampIntegerArithmetic(
                RewriteMixedNumericTextArithmetic(
                    RewriteSqlServerCastTypeNames(
                        RemoveRedundantOuterParentheses(output.ToString().Trim())),
                    context),
                context),
            context);
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
        if (name is not ("IIF" or "DATEPART" or "DATENAME" or "DATEADD" or "DATEDIFF" or
            "CHARINDEX" or "CONVERT" or "ISJSON" or "ROUND" or "RAND" or "MONTH" or "YEAR"))
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
            case "DATENAME" when translated.Length == 2:
                sql = $"to_char({translated[1]}, '{DateNameFormat(translated[0])}')";
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
            case "ROUND" when translated.Length == 2:
                sql = $"round(CAST({translated[0]} AS numeric), {translated[1]})";
                finding = Finding(
                    context.SourceObjectId,
                    "EXPRESSION.ROUND_SCALE_NUMERIC",
                    FindingSeverity.Information,
                    "Two-argument ROUND input was cast to numeric for PostgreSQL compatibility.");
                return true;
            case "RAND":
                sql = "random()";
                unsupported = translated.Length == 0 ? "RAND" : "RAND(seed)";
                finding = Finding(
                    context.SourceObjectId,
                    "EXPRESSION.RAND_SEMANTICS",
                    FindingSeverity.Warning,
                    "SQL Server RAND semantics are not equivalent to PostgreSQL random(); manual compatibility design is required.");
                return true;
            case "MONTH" when translated.Length == 1:
                sql = $"EXTRACT(MONTH FROM {translated[0]})::integer";
                return true;
            case "YEAR" when translated.Length == 1:
                sql = $"EXTRACT(YEAR FROM {translated[0]})::integer";
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
        return previous >= 0 && IsStringExpressionEndingAt(tokens, previous, columnTypes) ||
               next >= 0 &&
               (IsStringExpressionStartingAt(tokens, next, columnTypes) ||
                tokens[next].Kind == TSqlTokenKind.Word &&
                tokens[next].Text.Equals("N", StringComparison.OrdinalIgnoreCase) &&
                NextSignificantIndex(tokens, next) is var stringIndex &&
                stringIndex >= 0 &&
                tokens[stringIndex].Kind == TSqlTokenKind.String);
    }

    private static bool IsStringExpressionStartingAt(
        IReadOnlyList<TSqlToken> tokens,
        int index,
        IReadOnlyDictionary<string, string> columnTypes)
    {
        if (IsStringOperand(tokens[index], columnTypes))
        {
            return true;
        }
        if (tokens[index].Kind != TSqlTokenKind.Word)
        {
            return false;
        }
        if (IsStringReturningFunction(tokens[index].Text))
        {
            return true;
        }
        if (!tokens[index].Text.Equals("CAST", StringComparison.OrdinalIgnoreCase) &&
            !tokens[index].Text.Equals("CONVERT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return TryReadArguments(tokens, index, out var arguments, out _) &&
               arguments.Any(argument => IsTextTypeName(argument));
    }

    private static bool IsStringExpressionEndingAt(
        IReadOnlyList<TSqlToken> tokens,
        int index,
        IReadOnlyDictionary<string, string> columnTypes)
    {
        if (IsStringOperand(tokens[index], columnTypes))
        {
            return true;
        }
        if (tokens[index].Text != ")")
        {
            return false;
        }
        var depth = 0;
        for (var cursor = index; cursor >= 0; cursor--)
        {
            if (tokens[cursor].Kind is TSqlTokenKind.String or TSqlTokenKind.Comment)
            {
                continue;
            }
            if (tokens[cursor].Text == ")")
            {
                depth++;
            }
            else if (tokens[cursor].Text == "(")
            {
                depth--;
                if (depth == 0)
                {
                    var function = PreviousSignificantIndex(tokens, cursor);
                    return function >= 0 && tokens[function].Kind == TSqlTokenKind.Word &&
                           (IsStringReturningFunction(tokens[function].Text) ||
                            tokens[function].Text.Equals("CAST", StringComparison.OrdinalIgnoreCase) &&
                            ContainsTextCastType(tokens, cursor + 1, index)) ||
                           ContainsToken(tokens, cursor + 1, index, "||");
                }
            }
        }
        return false;
    }

    private static bool ContainsTextCastType(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Word && IsTextTypeName(tokens[index].Text))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsToken(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        int end,
        string value)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Text == value)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsStringReturningFunction(string name) =>
        name.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LTRIM", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("RTRIM", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SUBSTRING", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("LOWER", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("UPPER", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextTypeName(string type) =>
        type.Contains("char", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("text", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("xml", StringComparison.OrdinalIgnoreCase);

    private static bool TryTranslateTypedEmptyStringComparison(
        IReadOnlyList<TSqlToken> tokens,
        int columnIndex,
        ExpressionTranslationContext context,
        out string sql,
        out int endIndex,
        out InventoryFinding finding)
    {
        sql = string.Empty;
        endIndex = columnIndex;
        finding = null!;
        var token = tokens[columnIndex];
        if (!IsIdentifier(token))
        {
            return false;
        }

        var columnName = IdentifierText(token);
        if (!TryGetExpressionType(columnName, context, out var type) ||
            !IsTemporalType(type) && !IsNumericType(type))
        {
            return false;
        }

        var operatorIndex = NextSignificantIndex(tokens, columnIndex);
        var valueIndex = operatorIndex >= 0 ? NextSignificantIndex(tokens, operatorIndex) : -1;
        if (operatorIndex < 0 || valueIndex < 0 ||
            tokens[operatorIndex].Text is not ("=" or "<>" or "!=" or "<" or "<=" or ">" or ">=") ||
            tokens[valueIndex].Kind != TSqlTokenKind.String ||
            !IsEmptySqlString(tokens[valueIndex].Text))
        {
            return false;
        }

        var renderedColumn = context.TargetColumnNames.TryGetValue(columnName, out var mapped)
            ? mapped
            : token.Kind == TSqlTokenKind.QuotedIdentifier
                ? $"\"{columnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : token.Text;
        var comparison = tokens[operatorIndex].Text;
        sql = comparison switch
        {
            "=" => $"{renderedColumn} IS NULL",
            "<>" or "!=" => $"{renderedColumn} IS NOT NULL",
            _ => $"{renderedColumn} {comparison} {(IsTemporalType(type) ? "DATE '1900-01-01'" : "0")}"
        };
        endIndex = valueIndex;
        finding = Finding(
            context.SourceObjectId,
            IsTemporalType(type) ? "EXPRESSION.EMPTY_TEMPORAL" : "EXPRESSION.EMPTY_NUMERIC",
            FindingSeverity.Warning,
            comparison is "=" or "<>" or "!="
                ? "A typed empty-string equality comparison was converted to an explicit NULL predicate by the compatibility policy."
                : "A typed empty-string ordering comparison was converted to SQL Server's deterministic zero-date/zero-number compatibility value.");
        return true;
    }

    private static bool TryTranslateTemporalDateLiteralComparison(
        IReadOnlyList<TSqlToken> tokens,
        int columnIndex,
        ExpressionTranslationContext context,
        out string sql,
        out int endIndex,
        out InventoryFinding finding)
    {
        sql = string.Empty;
        endIndex = columnIndex;
        finding = null!;
        if (!IsIdentifier(tokens[columnIndex]))
        {
            return false;
        }
        var columnName = IdentifierText(tokens[columnIndex]);
        if (!TryGetExpressionType(columnName, context, out var type) || !IsTemporalType(type))
        {
            return false;
        }
        var operatorIndex = NextSignificantIndex(tokens, columnIndex);
        var literalIndex = operatorIndex >= 0 ? NextSignificantIndex(tokens, operatorIndex) : -1;
        if (operatorIndex < 0 || literalIndex < 0 ||
            tokens[operatorIndex].Text is not ("=" or "<>" or "!=" or "<" or "<=" or ">" or ">=") ||
            tokens[literalIndex].Kind != TSqlTokenKind.String)
        {
            return false;
        }
        var literal = tokens[literalIndex].Text[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (!DateTime.TryParseExact(
                literal,
                ["M/d/yyyy", "MM/dd/yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return false;
        }
        var renderedColumn = context.TargetColumnNames.TryGetValue(columnName, out var mapped)
            ? mapped
            : tokens[columnIndex].Kind == TSqlTokenKind.QuotedIdentifier
                ? $"\"{columnName.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : tokens[columnIndex].Text;
        sql = $"{renderedColumn} {tokens[operatorIndex].Text} DATE '{date:yyyy-MM-dd}'";
        endIndex = literalIndex;
        finding = Finding(
            context.SourceObjectId,
            "EXPRESSION.UNAMBIGUOUS_DATE_LITERAL",
            FindingSeverity.Information,
            "A SQL Server U.S.-formatted temporal literal was emitted as an unambiguous ISO date literal.");
        return true;
    }

    private static bool TryTranslateEmptyStringCast(
        IReadOnlyList<TSqlToken> tokens,
        int castIndex,
        ExpressionTranslationContext context,
        out string sql,
        out int endIndex,
        out InventoryFinding finding)
    {
        sql = string.Empty;
        endIndex = castIndex;
        finding = null!;
        if (tokens[castIndex].Kind != TSqlTokenKind.Word ||
            !tokens[castIndex].Text.Equals("CAST", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var open = NextSignificantIndex(tokens, castIndex);
        var value = open >= 0 ? NextSignificantIndex(tokens, open) : -1;
        var asIndex = value >= 0 ? NextSignificantIndex(tokens, value) : -1;
        var typeIndex = asIndex >= 0 ? NextSignificantIndex(tokens, asIndex) : -1;
        var close = typeIndex >= 0 ? NextSignificantIndex(tokens, typeIndex) : -1;
        if (open < 0 || value < 0 || asIndex < 0 || typeIndex < 0 || close < 0 ||
            tokens[open].Text != "(" || tokens[value].Kind != TSqlTokenKind.String ||
            !IsEmptySqlString(tokens[value].Text) ||
            !tokens[asIndex].Text.Equals("AS", StringComparison.OrdinalIgnoreCase) ||
            tokens[close].Text != ")")
        {
            return false;
        }

        var targetType = MapConvertType(tokens[typeIndex].Text);
        if (!IsTemporalType(targetType) && !IsNumericType(targetType))
        {
            return false;
        }

        sql = IsTemporalType(targetType)
            ? $"CAST(DATE '1900-01-01' AS {targetType})"
            : $"CAST(0 AS {targetType})";
        endIndex = close;
        finding = Finding(
            context.SourceObjectId,
            IsTemporalType(targetType) ? "EXPRESSION.EMPTY_TEMPORAL_CAST" : "EXPRESSION.EMPTY_NUMERIC_CAST",
            FindingSeverity.Warning,
            "An empty-string cast was converted to a deterministic SQL Server compatibility value.");
        return true;
    }

    private static string RewriteTimestampIntegerArithmetic(
        string expression,
        ExpressionTranslationContext context)
    {
        var tokens = TSqlTokenizer.Tokenize(expression);
        var output = new StringBuilder(expression.Length + 24);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == TSqlTokenKind.Word &&
                tokens[index].Text.Equals("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase))
            {
                var op = NextSignificantIndex(tokens, index);
                var operand = op >= 0 ? NextSignificantIndex(tokens, op) : -1;
                if (op >= 0 && operand >= 0 && tokens[op].Text is "+" or "-" &&
                    TryReadIntegerOperand(tokens, operand, context, out var operandSql, out var operandEnd) &&
                    !IsFollowedByIntervalMultiplier(tokens, operandEnd))
                {
                    output.Append(tokens[index].Text);
                    for (var gap = index + 1; gap < operand; gap++)
                    {
                        output.Append(tokens[gap].Text);
                    }
                    output.Append('(').Append(operandSql).Append(") * INTERVAL '1 day'");
                    index = operandEnd;
                    continue;
                }
            }

            if (tokens[index].Text is "+" or "-" &&
                PreviousSignificantIndex(tokens, index) is var leftIndex &&
                leftIndex >= 0 &&
                IsTemporalExpressionEndingAt(tokens, leftIndex, context))
            {
                var operand = NextSignificantIndex(tokens, index);
                if (operand >= 0 &&
                    TryReadIntegerExpression(
                        tokens,
                        operand,
                        context,
                        out var integerSql,
                        out var integerEnd) &&
                    !IsFollowedByIntervalMultiplier(tokens, integerEnd))
                {
                    output.Append(tokens[index].Text);
                    for (var gap = index + 1; gap < operand; gap++)
                    {
                        output.Append(tokens[gap].Text);
                    }
                    output.Append('(').Append(integerSql).Append(") * INTERVAL '1 day'");
                    index = integerEnd;
                    continue;
                }
            }
            output.Append(tokens[index].Text);
        }
        return output.ToString();
    }

    private static string RewriteMixedNumericTextArithmetic(
        string expression,
        ExpressionTranslationContext context)
    {
        var tokens = TSqlTokenizer.Tokenize(expression).ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Text != "*")
            {
                continue;
            }
            var left = PreviousSignificantIndex(tokens, index);
            var right = NextSignificantIndex(tokens, index);
            if (left < 0 || right < 0)
            {
                continue;
            }
            if (IsTextIdentifierOperand(tokens[left], context) &&
                IsNumericOperandEndingAt(tokens, right, context))
            {
                tokens[left] = tokens[left] with
                {
                    Text = $"CAST({tokens[left].Text} AS double precision)"
                };
            }
            else if (IsNumericOperandEndingAt(tokens, left, context) &&
                     IsTextIdentifierOperand(tokens[right], context))
            {
                tokens[right] = tokens[right] with
                {
                    Text = $"CAST({tokens[right].Text} AS double precision)"
                };
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static bool IsTextIdentifierOperand(
        TSqlToken token,
        ExpressionTranslationContext context) =>
        IsIdentifier(token) &&
        TryGetOutputIdentifierType(IdentifierText(token), context, out var type) &&
        IsTextTypeName(type);

    private static bool IsNumericOperandEndingAt(
        List<TSqlToken> tokens,
        int index,
        ExpressionTranslationContext context)
    {
        if (tokens[index].Kind == TSqlTokenKind.Number)
        {
            return true;
        }
        if (IsIdentifier(tokens[index]) &&
            TryGetOutputIdentifierType(IdentifierText(tokens[index]), context, out var type))
        {
            return IsNumericType(type);
        }
        if (tokens[index].Text != ")")
        {
            return false;
        }
        var depth = 0;
        for (var cursor = index; cursor >= 0; cursor--)
        {
            if (tokens[cursor].Text == ")")
            {
                depth++;
            }
            else if (tokens[cursor].Text == "(" && --depth == 0)
            {
                var function = PreviousSignificantIndex(tokens, cursor);
                return function >= 0 &&
                       tokens[function].Kind == TSqlTokenKind.Word &&
                       tokens[function].Text.Equals("random", StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }

    private static bool TryGetOutputIdentifierType(
        string identifier,
        ExpressionTranslationContext context,
        out string type)
    {
        if (TryGetExpressionType(identifier, context, out type!))
        {
            return true;
        }
        var sourceName = context.TargetColumnNames.FirstOrDefault(item =>
            item.Value.Equals(identifier, StringComparison.OrdinalIgnoreCase)).Key;
        return sourceName is not null && TryGetExpressionType(sourceName, context, out type!);
    }

    private static bool IsTemporalExpressionEndingAt(
        IReadOnlyList<TSqlToken> tokens,
        int index,
        ExpressionTranslationContext context)
    {
        if (!IsIdentifier(tokens[index]))
        {
            return false;
        }
        var identifier = IdentifierText(tokens[index]);
        if (TryGetExpressionType(identifier, context, out var directType))
        {
            return IsTemporalType(directType);
        }
        var sourceName = context.TargetColumnNames.FirstOrDefault(item =>
            item.Value.Equals(identifier, StringComparison.OrdinalIgnoreCase)).Key;
        return sourceName is not null &&
               TryGetExpressionType(sourceName, context, out var sourceType) &&
               IsTemporalType(sourceType);
    }

    private static bool TryReadIntegerExpression(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        ExpressionTranslationContext context,
        out string sql,
        out int end)
    {
        if (TryReadIntegerOperand(tokens, start, context, out sql, out end))
        {
            return true;
        }
        if (!TryReadRoutineCall(tokens, start, context, out _, out var close, out var signature) ||
            signature.ReturnType is null ||
            !IsIntegralType(signature.ReturnType))
        {
            sql = string.Empty;
            end = start;
            return false;
        }
        sql = TokenRange(tokens, start, close + 1);
        end = close;
        return true;
    }

    private static string RewriteRoutineArgumentCasts(
        string expression,
        ExpressionTranslationContext context)
    {
        if (context.TargetRoutineSignatures.Count == 0)
        {
            return expression;
        }
        var tokens = TSqlTokenizer.Tokenize(expression);
        var output = new StringBuilder(expression.Length + 32);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (!TryReadRoutineCall(
                    tokens,
                    index,
                    context,
                    out var open,
                    out var close,
                    out var signature))
            {
                output.Append(tokens[index].Text);
                continue;
            }

            output.Append(TokenRange(tokens, index, open + 1));
            var arguments = ReadArgumentRanges(tokens, open, close);
            for (var argumentIndex = 0; argumentIndex < arguments.Count; argumentIndex++)
            {
                if (argumentIndex > 0)
                {
                    output.Append(", ");
                }
                var (start, end) = arguments[argumentIndex];
                var argument = TokenRange(tokens, start, end).Trim();
                var expectedType = argumentIndex < signature.ParameterTypes.Count
                    ? signature.ParameterTypes[argumentIndex]
                    : null;
                if (IsTimestampWithoutTimeZone(expectedType) &&
                    argument.Contains("CURRENT_TIMESTAMP", StringComparison.OrdinalIgnoreCase) &&
                    !argument.Contains("timestamp without time zone", StringComparison.OrdinalIgnoreCase))
                {
                    output.Append('(').Append(argument).Append(")::timestamp without time zone");
                }
                else
                {
                    output.Append(argument);
                }
            }
            output.Append(tokens[close].Text);
            index = close;
        }
        return output.ToString();
    }

    private static bool TryReadRoutineCall(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        ExpressionTranslationContext context,
        out int open,
        out int close,
        out TargetRoutineSignature signature)
    {
        open = -1;
        close = -1;
        signature = null!;
        if (!IsIdentifier(tokens, start))
        {
            return false;
        }
        var nameEnd = start;
        var name = IdentifierText(tokens[start]);
        var dot = NextSignificantIndex(tokens, start);
        var second = dot >= 0 ? NextSignificantIndex(tokens, dot) : -1;
        if (dot >= 0 && second >= 0 && tokens[dot].Text == "." && IsIdentifier(tokens, second))
        {
            name = $"{name}.{IdentifierText(tokens[second])}";
            nameEnd = second;
        }
        if (!context.TargetRoutineSignatures.TryGetValue(name, out signature!))
        {
            return false;
        }
        open = NextSignificantIndex(tokens, nameEnd);
        if (open < 0 || tokens[open].Text != "(")
        {
            return false;
        }
        close = FindClosingParenthesis(tokens, open);
        return close >= 0;
    }

    private static int FindClosingParenthesis(IReadOnlyList<TSqlToken> tokens, int open)
    {
        var depth = 0;
        for (var index = open; index < tokens.Count; index++)
        {
            if (tokens[index].Kind is TSqlTokenKind.String or TSqlTokenKind.Comment)
            {
                continue;
            }
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

    private static List<(int Start, int End)> ReadArgumentRanges(
        IReadOnlyList<TSqlToken> tokens,
        int open,
        int close)
    {
        var ranges = new List<(int, int)>();
        var start = open + 1;
        var depth = 0;
        for (var index = open + 1; index < close; index++)
        {
            if (tokens[index].Kind is TSqlTokenKind.String or TSqlTokenKind.Comment)
            {
                continue;
            }
            if (tokens[index].Text == "(")
            {
                depth++;
            }
            else if (tokens[index].Text == ")")
            {
                depth--;
            }
            else if (tokens[index].Text == "," && depth == 0)
            {
                ranges.Add((start, index));
                start = index + 1;
            }
        }
        if (start < close)
        {
            ranges.Add((start, close));
        }
        return ranges;
    }

    private static string TokenRange(IReadOnlyList<TSqlToken> tokens, int start, int end) =>
        string.Concat(tokens.Skip(start).Take(end - start).Select(item => item.Text));

    private static bool IsTimestampWithoutTimeZone(string? targetType) =>
        !string.IsNullOrWhiteSpace(targetType) &&
        targetType.Contains("timestamp", StringComparison.OrdinalIgnoreCase) &&
        targetType.Contains("without time zone", StringComparison.OrdinalIgnoreCase);

    private static string RewriteSqlServerCastTypeNames(string expression)
    {
        var tokens = TSqlTokenizer.Tokenize(expression).ToList();
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind != TSqlTokenKind.Word)
            {
                continue;
            }
            var type = tokens[index].Text.ToUpperInvariant();
            if (type is not ("NVARCHAR" or "NCHAR" or "VARCHAR"))
            {
                continue;
            }
            var open = NextSignificantIndex(tokens, index);
            var size = open >= 0 ? NextSignificantIndex(tokens, open) : -1;
            var close = size >= 0 ? NextSignificantIndex(tokens, size) : -1;
            if (open < 0 || size < 0 || close < 0 ||
                tokens[open].Text != "(" || tokens[close].Text != ")")
            {
                if (type == "NVARCHAR")
                {
                    tokens[index] = tokens[index] with { Text = "varchar" };
                }
                else if (type == "NCHAR")
                {
                    tokens[index] = tokens[index] with { Text = "char" };
                }
                continue;
            }
            if (tokens[size].Kind == TSqlTokenKind.Word &&
                tokens[size].Text.Equals("MAX", StringComparison.OrdinalIgnoreCase))
            {
                tokens[index] = tokens[index] with { Text = "text" };
                for (var clear = index + 1; clear <= close; clear++)
                {
                    tokens[clear] = tokens[clear] with { Text = string.Empty };
                }
            }
            else if (type == "NVARCHAR")
            {
                tokens[index] = tokens[index] with { Text = "varchar" };
            }
            else if (type == "NCHAR")
            {
                tokens[index] = tokens[index] with { Text = "char" };
            }
        }
        return string.Concat(tokens.Select(item => item.Text));
    }

    private static bool IsFollowedByIntervalMultiplier(
        IReadOnlyList<TSqlToken> tokens,
        int operandEnd)
    {
        var multiply = NextSignificantIndex(tokens, operandEnd);
        var interval = multiply >= 0 ? NextSignificantIndex(tokens, multiply) : -1;
        return multiply >= 0 && interval >= 0 && tokens[multiply].Text == "*" &&
               tokens[interval].Kind == TSqlTokenKind.Word &&
               tokens[interval].Text.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIntegerOperand(TSqlToken token, ExpressionTranslationContext context) =>
        token.Kind == TSqlTokenKind.Number && !token.Text.Contains('.', StringComparison.Ordinal) ||
        IsIdentifier(token) && TryGetExpressionType(IdentifierText(token), context, out var type) &&
        IsIntegralType(type);

    private static bool TryReadIntegerOperand(
        IReadOnlyList<TSqlToken> tokens,
        int start,
        ExpressionTranslationContext context,
        out string operandSql,
        out int end)
    {
        operandSql = string.Empty;
        end = start;
        if (IsIntegerOperand(tokens[start], context))
        {
            operandSql = tokens[start].Text;
            return true;
        }
        if (tokens[start].Text != "(")
        {
            return false;
        }
        var value = NextSignificantIndex(tokens, start);
        var close = value >= 0 ? NextSignificantIndex(tokens, value) : -1;
        if (value < 0 || close < 0 || tokens[close].Text != ")" ||
            !IsIntegerOperand(tokens[value], context))
        {
            return false;
        }
        operandSql = tokens[value].Text;
        end = close;
        return true;
    }

    private static bool TryGetExpressionType(
        string columnName,
        ExpressionTranslationContext context,
        out string type) =>
        context.TargetColumnTypes.TryGetValue(columnName, out type!) ||
        context.ColumnTypes.TryGetValue(columnName, out type!);

    private static bool IsEmptySqlString(string value) => value == "''" || value == "N''";

    private static bool IsBareEmptyStringExpression(string expression)
    {
        var significant = TSqlTokenizer.Tokenize(expression)
            .Where(item => item.Kind is not TSqlTokenKind.Whitespace and not TSqlTokenKind.Comment)
            .ToList();
        while (significant.Count >= 3 && significant[0].Text == "(" && significant[^1].Text == ")")
        {
            significant.RemoveAt(significant.Count - 1);
            significant.RemoveAt(0);
        }
        return significant.Count == 1 && significant[0].Kind == TSqlTokenKind.String &&
               IsEmptySqlString(significant[0].Text) ||
               significant.Count == 2 &&
               significant[0].Kind == TSqlTokenKind.Word &&
               significant[0].Text.Equals("N", StringComparison.OrdinalIgnoreCase) &&
               significant[1].Kind == TSqlTokenKind.String &&
               IsEmptySqlString(significant[1].Text);
    }

    private static bool IsTemporalType(string type) =>
        type.Contains("date", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("time", StringComparison.OrdinalIgnoreCase);

    private static bool IsIntegralType(string type) =>
        type.Contains("int", StringComparison.OrdinalIgnoreCase) &&
        !type.Contains("interval", StringComparison.OrdinalIgnoreCase);

    private static bool IsNumericType(string type) =>
        IsIntegralType(type) ||
        type.Contains("numeric", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("decimal", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("money", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("real", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("float", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("double", StringComparison.OrdinalIgnoreCase);

    private static string DateNameFormat(string value) =>
        NormalizeDatePart(value) switch
        {
            "month" => "FMMonth",
            "day" => "FMDay",
            "year" => "YYYY",
            var part => part
        };

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
               IsTextTypeName(type);
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
