using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;

namespace MigrationStudio.Tests.Conversion;

public sealed class ExpressionTranslationTests
{
    private readonly StructuredSqlExpressionTranslator _translator = new();
    private readonly InventoryObjectId _id =
        InventoryObjectId.Create("fixture", InventoryObjectType.Table, "dbo", "t", 1);

    [Fact]
    public void Translation_PreservesLiteralsAndComments()
    {
        var result = Translate("ISNULL([Name], N'GETDATE() + ISNULL') /* ISNULL(GETDATE()) */");

        Assert.Equal("COALESCE(\"Name\", 'GETDATE() + ISNULL') /* ISNULL(GETDATE()) */", result.Sql);
        Assert.DoesNotContain("CURRENT_TIMESTAMP", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translation_ConvertsStringConcatenationButPreservesNumericAddition()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "nvarchar",
                ["Amount"] = "int"
            },
            new ConversionOptions(),
            false);

        var strings = _translator.Translate("[Name] + '-' + [Name]", context);
        var numeric = _translator.Translate("[Amount] + 1", context);

        Assert.Contains("||", strings.Sql, StringComparison.Ordinal);
        Assert.Contains("+", numeric.Sql, StringComparison.Ordinal);
        Assert.Contains(strings.Findings, item => item.Code == "EXPRESSION.CONCAT_NULL_SEMANTICS");
    }

    [Theory]
    [InlineData("LTRIM(RTRIM([Name])) + CAST(42 AS varchar)", "ltrim(rtrim(\"Name\")) || CAST(42 AS varchar)")]
    [InlineData("LEFT([Name], 2) + RIGHT('000' + CAST([Amount] AS varchar), 3)", "left(\"Name\", 2) || right('000' || CAST(\"Amount\" AS varchar), 3)")]
    [InlineData("(([Name] + '/') + 'IF') + CAST([Amount] AS varchar)", "((\"Name\" || '/') || 'IF') || CAST(\"Amount\" AS varchar)")]
    public void Translation_ConvertsConcatenationAcrossStringFunctionsAndCasts(
        string source,
        string expected)
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Name"] = "varchar",
                ["Amount"] = "int"
            },
            new ConversionOptions(),
            true);

        var result = _translator.Translate(source, context);

        Assert.Equal(expected, result.Sql);
        Assert.DoesNotContain(" + ", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[Created] = ''", "\"Created\" IS NULL")]
    [InlineData("[Created] >= ''", "\"Created\" >= DATE '1900-01-01'")]
    [InlineData("[Amount] = ''", "\"Amount\" IS NULL")]
    [InlineData("[Amount] <= ''", "\"Amount\" <= 0")]
    public void Translation_ConvertsTypedEmptyStringComparisons(
        string source,
        string expected)
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Created"] = "datetime2",
                ["Amount"] = "bigint"
            },
            new ConversionOptions(),
            false);

        var result = _translator.Translate(source, context);

        Assert.Equal(expected, result.Sql);
        Assert.Contains(result.Findings, item =>
            item.Code is "EXPRESSION.EMPTY_TEMPORAL" or "EXPRESSION.EMPTY_NUMERIC");
    }

    [Theory]
    [InlineData("[Created] >= '04/01/2011'", "\"Created\" >= DATE '2011-04-01'")]
    [InlineData("[Created] <= '03/31/2012'", "\"Created\" <= DATE '2012-03-31'")]
    public void Translation_ConvertsSqlServerUsDateLiteralsToIsoDates(
        string source,
        string expected)
    {
        var result = _translator.Translate(
            source,
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string> { ["Created"] = "datetime" },
                new ConversionOptions(),
                false));

        Assert.Equal(expected, result.Sql);
        Assert.Contains(result.Findings, item => item.Code == "EXPRESSION.UNAMBIGUOUS_DATE_LITERAL");
    }

    [Theory]
    [InlineData("CAST('' AS bigint)", "CAST(0 AS bigint)")]
    [InlineData("CAST('' AS datetime)", "CAST(DATE '1900-01-01' AS timestamp)")]
    public void Translation_ConvertsTypedEmptyStringCasts(
        string source,
        string expected)
    {
        var result = Translate(source);

        Assert.Equal(expected, result.Sql);
        Assert.DoesNotContain("CAST(''", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("''", "bigint", "0", "EXPRESSION.EMPTY_NUMERIC_DEFAULT")]
    [InlineData("((''))", "timestamp without time zone", "DATE '1900-01-01'", "EXPRESSION.EMPTY_TEMPORAL_DEFAULT")]
    public void Translation_ConvertsBareTypedEmptyStringDefaults(
        string source,
        string targetType,
        string expected,
        string findingCode)
    {
        var result = _translator.Translate(
            source,
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string>(),
                new ConversionOptions(),
                false)
            {
                ExpectedTargetType = targetType
            });

        Assert.Equal(expected, result.Sql);
        Assert.Contains(result.Findings, item => item.Code == findingCode);
    }

    [Fact]
    public void Translation_CastsTwoArgumentRoundToNumeric()
    {
        var result = Translate("ROUND([Amount], 2)");

        Assert.Equal("round(CAST(\"Amount\" AS numeric), 2)", result.Sql);
        Assert.Empty(result.UnsupportedFunctions);
    }

    [Theory]
    [InlineData("GETDATE() - 7", "CURRENT_TIMESTAMP - (7) * INTERVAL '1 day'")]
    [InlineData("GETDATE() + 2", "CURRENT_TIMESTAMP + (2) * INTERVAL '1 day'")]
    [InlineData("CAST(GETDATE() - (1) AS date)", "CAST(CURRENT_TIMESTAMP - (1) * INTERVAL '1 day' AS date)")]
    [InlineData("CONVERT(date, GETDATE() - (1), 0)", "CAST(CURRENT_TIMESTAMP - (1) * INTERVAL '1 day' AS date)")]
    [InlineData("CAST(@sql AS nvarchar(max))", "CAST(@sql AS text)")]
    public void Translation_ConvertsTimestampMinusIntegerToDayInterval(
        string source,
        string expected)
    {
        var result = Translate(source);

        Assert.Equal(expected, result.Sql);
    }

    [Fact]
    public void Translation_ConvertsTimestampMinusIntegerRoutineToDayInterval()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string> { ["Processed"] = "datetime", ["Sent"] = "datetime" },
            new ConversionOptions(),
            true)
        {
            TargetColumnNames = new Dictionary<string, string>
            {
                ["Processed"] = "processed",
                ["Sent"] = "sent"
            },
            TargetColumnTypes = new Dictionary<string, string>
            {
                ["Processed"] = "timestamp without time zone",
                ["Sent"] = "timestamp without time zone"
            },
            TargetObjectNames = new Dictionary<string, string>
            {
                [MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey("dbo", "WorkingDays")] =
                    "public.workingdays"
            },
            TargetRoutineSignatures = new Dictionary<string, TargetRoutineSignature>
            {
                ["public.workingdays"] = new(
                    ["timestamp without time zone", "timestamp without time zone"],
                    "integer")
            },
            ExpectedTargetType = "timestamp without time zone"
        };

        var result = _translator.Translate(
            "[Processed] - [dbo].[WorkingDays]([Sent], [Processed])",
            context);

        Assert.Equal(
            "processed - (public.workingdays(sent, processed)) * INTERVAL '1 day'",
            result.Sql);
        Assert.DoesNotContain("INTERVAL * INTERVAL", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Translation_CastsCurrentTimestampForMappedTimestampWithoutTimeZoneRoutine()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(),
            new ConversionOptions(),
            true)
        {
            TargetObjectNames = new Dictionary<string, string>
            {
                [MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey("dbo", "FiscalYear")] =
                    "public.fiscalyear"
            },
            TargetRoutineSignatures = new Dictionary<string, TargetRoutineSignature>
            {
                ["public.fiscalyear"] = new(["timestamp without time zone"], "varchar(10)")
            }
        };

        var result = _translator.Translate("[dbo].[FiscalYear](GETDATE())", context);

        Assert.Equal(
            "public.fiscalyear((CURRENT_TIMESTAMP)::timestamp without time zone)",
            result.Sql);
    }

    [Fact]
    public void Translation_PreservesCurrentTimestampForMappedTimestampWithTimeZoneRoutine()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(),
            new ConversionOptions(),
            false)
        {
            TargetObjectNames = new Dictionary<string, string>
            {
                [MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey("dbo", "ObserveAt")] =
                    "public.observe_at"
            },
            TargetRoutineSignatures = new Dictionary<string, TargetRoutineSignature>
            {
                ["public.observe_at"] = new(["timestamp with time zone"], "integer")
            }
        };

        var result = _translator.Translate("dbo.ObserveAt(GETDATE())", context);

        Assert.Equal("public.observe_at(CURRENT_TIMESTAMP)", result.Sql);
    }

    [Fact]
    public void Translation_CastsTextOperandForNumericMultiplication()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string> { ["Mobile"] = "char" },
            new ConversionOptions(),
            true)
        {
            TargetColumnNames = new Dictionary<string, string> { ["Mobile"] = "mobile" },
            TargetColumnTypes = new Dictionary<string, string> { ["Mobile"] = "char(10)" },
            ExpectedTargetType = "bigint"
        };

        var result = _translator.Translate("RAND(7) * [Mobile]", context);

        Assert.Matches(@"random\(\)\s*\*\s*CAST\(mobile AS double precision\)", result.Sql);
    }

    [Theory]
    [InlineData("MONTH([Created])", "EXTRACT(MONTH FROM \"Created\")::integer")]
    [InlineData("YEAR([Created])", "EXTRACT(YEAR FROM \"Created\")::integer")]
    [InlineData("DATENAME(month, [Created])", "to_char(\"Created\", 'FMMonth')")]
    public void Translation_ConvertsAdditionalTemporalFunctions(
        string source,
        string expected)
    {
        var result = Translate(source);

        Assert.Equal(expected, result.Sql);
        Assert.Empty(result.UnsupportedFunctions);
    }

    [Fact]
    public void GeneratedColumn_ClassifiesSeededRandForManualCompatibility()
    {
        var result = _translator.Translate(
            "RAND([Amount])",
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string> { ["Amount"] = "int" },
                new ConversionOptions(),
                true));

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("RAND(seed)", result.UnsupportedFunctions, StringComparer.Ordinal);
        Assert.Contains(result.Findings, item => item.Code == "EXPRESSION.RAND_SEMANTICS");
    }

    [Fact]
    public void Translation_ConvertsCommonDateAndConditionalFunctionsStructurally()
    {
        var result = Translate("IIF(DATEPART(day, GETDATE()) > 1, DATEADD(day, 2, [Created]), [Created])");

        Assert.Contains("CASE WHEN", result.Sql, StringComparison.Ordinal);
        Assert.Contains("EXTRACT(day FROM CURRENT_TIMESTAMP)", result.Sql, StringComparison.Ordinal);
        Assert.Contains("INTERVAL '1 day'", result.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GETDATE()", "CURRENT_TIMESTAMP")]
    [InlineData("SYSDATETIME()", "CURRENT_TIMESTAMP")]
    [InlineData("GETUTCDATE()", "timezone('UTC', CURRENT_TIMESTAMP)")]
    [InlineData("SYSUTCDATETIME()", "timezone('UTC', CURRENT_TIMESTAMP)")]
    public void Translation_MapsSqlServerCurrentTimeFunctions(
        string source,
        string expected)
    {
        var result = Translate(source);

        Assert.Equal(expected, result.Sql);
        Assert.False(result.IsImmutable);
        Assert.DoesNotContain("GETDATE", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SYSDATETIME", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SYSUTCDATETIME()", "timestamp without time zone", "timezone('UTC', CURRENT_TIMESTAMP)")]
    [InlineData("getutcdate()", "timestamp(6) without time zone", "timezone('UTC', CURRENT_TIMESTAMP)")]
    [InlineData("SysUtcDateTime()", "timestamp with time zone", "CURRENT_TIMESTAMP")]
    [InlineData("GetUtcDate()", "timestamptz", "CURRENT_TIMESTAMP")]
    public void Translation_MapsUtcFunctionsAccordingToPostgreSqlTargetType(
        string source,
        string targetType,
        string expected)
    {
        var result = _translator.Translate(
            source,
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string>(),
                new ConversionOptions(),
                false)
            {
                ExpectedTargetType = targetType
            });

        Assert.Equal(expected, result.Sql);
        Assert.DoesNotContain("utcdatetime", result.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("getutcdate", result.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sysutcdatetime()")]
    [InlineData("(sysutcdatetime())")]
    [InlineData("((sysutcdatetime()))")]
    public void Translation_NormalizesWrappedUtcDefaults(string source)
    {
        var result = _translator.Translate(
            source,
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string>(),
                new ConversionOptions(),
                false)
            {
                ExpectedTargetType = "timestamp without time zone"
            });

        Assert.Equal("timezone('UTC', CURRENT_TIMESTAMP)", result.Sql);
    }

    [Fact]
    public void Translation_ConvertsDateDiffYearToCompletePostgreSqlExpression()
    {
        var result = Translate("DATEDIFF(YEAR, [BirthDate], [AsOfDate])");

        Assert.Equal(
            "EXTRACT(YEAR FROM \"AsOfDate\")::integer - EXTRACT(YEAR FROM \"BirthDate\")::integer",
            result.Sql);
        Assert.Empty(result.UnsupportedFunctions);
    }

    [Theory]
    [InlineData("CONVERT([bit],(1))", "TRUE")]
    [InlineData("CONVERT(bit, 0)", "FALSE")]
    public void Translation_MapsSqlServerBitLiteralsToPostgreSqlBooleans(
        string source,
        string expected)
    {
        var result = Translate(source);

        Assert.Equal(expected, result.Sql);
        Assert.Empty(result.UnsupportedFunctions);
    }

    [Fact]
    public void Translation_MapsSqlServerIdentityAndJsonPredicates()
    {
        var user = Translate("SUSER_SNAME()");
        var json = Translate("ISJSON([PayloadBody])=(1)");

        Assert.Equal("CURRENT_USER", user.Sql);
        Assert.Equal(
            "(CASE WHEN \"PayloadBody\" IS JSON THEN 1 ELSE 0 END)=(1)",
            json.Sql);
        Assert.Empty(json.UnsupportedFunctions);
    }

    [Fact]
    public void Translation_DoesNotRewriteTemporalFunctionsInsideStringsOrComments()
    {
        var result = Translate(
            "N'SYSUTCDATETIME()' /* GETUTCDATE() */ + SYSUTCDATETIME()");

        Assert.Contains("'SYSUTCDATETIME()'", result.Sql, StringComparison.Ordinal);
        Assert.Contains("/* GETUTCDATE() */", result.Sql, StringComparison.Ordinal);
        Assert.EndsWith(
            "timezone('UTC', CURRENT_TIMESTAMP)",
            result.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedColumn_RejectsNonImmutableExpression()
    {
        var result = _translator.Translate(
            "GETDATE()",
            new ExpressionTranslationContext(_id, new Dictionary<string, string>(), new ConversionOptions(), true));

        Assert.False(result.IsImmutable);
        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
    }

    [Fact]
    public void Translation_MapsNextValueForThroughSchemaRules()
    {
        var result = Translate("NEXT VALUE FOR [dbo].[OrderNumber]");

        Assert.Equal("nextval('dbo.ordernumber'::regclass)", result.Sql);
    }

    [Fact]
    public void Translation_MapsScopeIdentityWithExplicitSemanticWarning()
    {
        var result = Translate("SCOPE_IDENTITY()");

        Assert.Equal("lastval()", result.Sql);
        Assert.Equal(
            ConversionClassification.AutomaticWithWarning,
            result.Classification);
        Assert.Contains(result.Findings, item =>
            item.Code == "EXPRESSION.SCOPE_IDENTITY");
        Assert.Empty(result.UnsupportedFunctions);
    }

    [Theory]
    [InlineData("[NREGA_SK].[FnChkSau_DupAcc]([Acc_No])", "nrega_sk.fnchksau_dupacc(acc_no)")]
    [InlineData("[vbgramg].[NREGA_SK].[FnChkSau_DupAcc]([Acc_No])", "nrega_sk.fnchksau_dupacc(acc_no)")]
    [InlineData("NREGA_SK.FnChkSau_DupAcc(Acc_No)", "nrega_sk.fnchksau_dupacc(acc_no)")]
    public void Translation_UsesFinalMappingForQualifiedObjectReferences(
        string source,
        string expected)
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Acc_No"] = "varchar"
            },
            new ConversionOptions(),
            false)
        {
            TargetColumnNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Acc_No"] = "acc_no"
            },
            TargetObjectNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey(
                    "NREGA_SK",
                    "FnChkSau_DupAcc")] = "nrega_sk.fnchksau_dupacc"
            }
        };

        var result = _translator.Translate(source, context);

        Assert.Equal(expected, result.Sql);
        Assert.DoesNotContain("\"NREGA_SK\"", result.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Translation_UsesFinalMappingForSequenceReferences()
    {
        var context = new ExpressionTranslationContext(
            _id,
            new Dictionary<string, string>(),
            new ConversionOptions(),
            false)
        {
            TargetObjectNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceKey(
                    "NREGA_SK",
                    "OrderNumber")] = "nrega_sk.ordernumber_7f02"
            }
        };

        var result = _translator.Translate(
            "NEXT VALUE FOR [NREGA_SK].[OrderNumber]",
            context);

        Assert.Equal(
            "nextval('nrega_sk.ordernumber_7f02'::regclass)",
            result.Sql);
        Assert.DoesNotContain("NREGA_SK", result.Sql, StringComparison.Ordinal);
    }

    private ExpressionTranslationResult Translate(string sql) =>
        _translator.Translate(
            sql,
            new ExpressionTranslationContext(
                _id,
                new Dictionary<string, string> { ["Name"] = "nvarchar", ["Created"] = "datetime2" },
                new ConversionOptions(),
                false));
}
