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
