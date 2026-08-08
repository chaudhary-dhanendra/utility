using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;
using System.Text.RegularExpressions;

namespace MigrationStudio.Tests.Conversion;

public sealed class ProgrammableObjectConverterTests
{
    [Fact]
    public async Task ConvertsSimpleScalarFunction()
    {
        var function = Object(
            InventoryObjectType.Function,
            "AddOne",
            "CREATE FUNCTION [dbo].[AddOne](@value int) RETURNS int AS BEGIN RETURN ISNULL(@value, 0) + 1; END");
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "int"),
                Parameter(1, "@value", "int")
            ]);
        var context = Context([function], [module]);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(function, context, CancellationToken.None);

        Assert.Equal(ConversionClassification.Automatic, result.Classification);
        Assert.Contains("RETURNS integer", result.Target, StringComparison.Ordinal);
        Assert.Contains("COALESCE(p_value, 0) + 1", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScalarFunction_UsesTimestamptzAwareUtcTemporalMapping()
    {
        var function = Object(
            InventoryObjectType.Function,
            "UtcNow",
            "CREATE FUNCTION [dbo].[UtcNow]() RETURNS datetimeoffset AS BEGIN RETURN SYSUTCDATETIME(); END");
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "datetimeoffset", 10)]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Contains("with time zone", result.Target, StringComparison.Ordinal);
        Assert.Contains("SELECT CURRENT_TIMESTAMP;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FnChkSauDupAcc_DeclaresAndUsesLocalVariables()
    {
        var table = Object(InventoryObjectType.Table, "SAU_details1617", null);
        var function = Object(
            InventoryObjectType.Function,
            "FnChkSAU_DupAcc",
            """
            CREATE FUNCTION [dbo].[FnChkSAU_DupAcc] (@Acc_No varchar(18))
            RETURNS BIT
            AS
            BEGIN
                DECLARE @Return BIT
                DECLARE @SQL1 nvarchar(max)
                SET @Return=0
                BEGIN
                    SELECT @Return=CASE WHEN count(1)>1 THEN 1 ELSE 0 END
                    FROM SAU_details1617 (NOLOCK)
                    WHERE Acc_No=@Acc_No
                END
                RETURN @Return
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "bit", 1),
                Parameter(1, "@Acc_No", "varchar", 18)
            ]);
        var inventory = TestInventory.CreateSnapshot([table, function]) with
        {
            Modules = [module],
            Dependencies =
            [
                new InventoryDependency(
                    function.Id,
                    table.Id,
                    DependencyKind.SqlExpression,
                    table.QualifiedSourceName,
                    true,
                    false)
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context(inventory),
            CancellationToken.None);

        Assert.Contains("LANGUAGE plpgsql", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_return boolean", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_sql1 text", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_return := false", result.Target, StringComparison.Ordinal);
        Assert.Contains("INTO v_return", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN v_return", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_acc_no", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("p_return", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("NOLOCK", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SauFnDupPeriodDate_UsesParametersAndDeclaredLocal()
    {
        var table = Object(InventoryObjectType.Table, "SAU_GP_level_summary_data", null);
        var function = Object(
            InventoryObjectType.Function,
            "SAU_FnDupPeriodDate",
            """
            CREATE FUNCTION [dbo].[SAU_FnDupPeriodDate]
            (
                @panchayat_code varchar(10),
                @SA_period_from_date datetime,
                @SA_period_to_date datetime
            )
            RETURNS bit
            AS
            BEGIN
                DECLARE @Dup_cnt BIT = 0
                BEGIN
                    SELECT @Dup_cnt = CASE WHEN count(1)>=1 THEN 1 ELSE 0 END
                    FROM SAU_GP_level_summary_data
                    WHERE Panchayat_code=@panchayat_code
                      AND SA_period_from_date=@SA_period_from_date
                      AND SA_period_To_date=@SA_period_To_date;
                END
                RETURN @Dup_cnt
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "bit", 1),
                Parameter(1, "@panchayat_code", "varchar", 10),
                Parameter(2, "@SA_period_from_date", "datetime", 8),
                Parameter(3, "@SA_period_to_date", "datetime", 8)
            ]);
        var inventory = TestInventory.CreateSnapshot([table, function]) with
        {
            Modules = [module],
            Dependencies =
            [
                new InventoryDependency(
                    function.Id,
                    table.Id,
                    DependencyKind.SqlExpression,
                    table.QualifiedSourceName,
                    true,
                    false)
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context(inventory),
            CancellationToken.None);

        Assert.Contains("v_dup_cnt boolean", result.Target, StringComparison.Ordinal);
        Assert.Contains("INTO v_dup_cnt", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN v_dup_cnt", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_panchayat_code", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_sa_period_from_date", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_sa_period_to_date", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("p_dup_cnt", result.Target, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "fc_get_working_days_bank",
        "SELECT ISNULL(count(1),0) AS total FROM [dbo].[Calendar] WHERE bank_holiday='Y' AND DateValue > @from AND DateValue <= @to")]
    [InlineData(
        "fc_get_labor_days",
        "SELECT count(1) AS total FROM [dbo].[Calendar] WHERE holiday='Y' AND DateValue > @from AND DateValue <= @to")]
    public async Task QueryReturningScalarFunction_DoesNotGenerateSelectSelect(
        string name,
        string query)
    {
        var calendar = Object(InventoryObjectType.Table, "Calendar", null);
        var function = Object(
            InventoryObjectType.Function,
            name,
            $"CREATE FUNCTION [dbo].[{name}](@from datetime, @to datetime) " +
            $"RETURNS int AS BEGIN RETURN ({query}) END");
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "int"),
                Parameter(1, "@from", "datetime", 8),
                Parameter(2, "@to", "datetime", 8)
            ]);
        var inventory = TestInventory.CreateSnapshot([calendar, function]) with
        {
            Modules = [module],
            Dependencies =
            [
                new InventoryDependency(
                    function.Id,
                    calendar.Id,
                    DependencyKind.SqlExpression,
                    calendar.QualifiedSourceName,
                    true,
                    false)
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context(inventory),
            CancellationToken.None);

        Assert.Contains("LANGUAGE sql", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT SELECT", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT ", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_from", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_to", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SimpleIfElseReturn_UsesPlpgsqlControlFlow()
    {
        var function = Object(
            InventoryObjectType.Function,
            "ClassifyValue",
            """
            CREATE FUNCTION [dbo].[ClassifyValue](@value int)
            RETURNS int AS
            BEGIN
                IF @value > 0
                    RETURN 1
                ELSE
                    RETURN 0
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "int"),
                Parameter(1, "@value", "int")
            ]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Contains("LANGUAGE plpgsql", result.Target, StringComparison.Ordinal);
        Assert.Contains("IF p_value > 0 THEN", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN 1;", result.Target, StringComparison.Ordinal);
        Assert.Contains("ELSE", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN 0;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("@value", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignmentIfElse_AfterSelectAssignment_UsesCompletePlpgsqlControlFlow()
    {
        var applicant = Object(InventoryObjectType.Table, "Applicant", null);
        var function = Object(
            InventoryObjectType.Function,
            "CheckDuplicate",
            """
            CREATE FUNCTION [dbo].[CheckDuplicate](@jobCardNo varchar(35))
            RETURNS bit AS
            BEGIN
                DECLARE @Return bit
                DECLARE @RegNo int
                SET @Return = 0
                SET @RegNo = 0
                SELECT @RegNo = COUNT(jobCardNo) FROM Applicant WHERE jobCardNo = @jobCardNo
                IF @RegNo > 2
                    SET @Return = 0
                ELSE
                    SET @Return = 1
                RETURN @Return
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "bit"), Parameter(1, "@jobCardNo", "varchar", 35)]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([applicant, function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("SELECT COUNT(jobCardNo) INTO v_regno", result.Target, StringComparison.Ordinal);
        Assert.Matches(@"IF\s+v_regno\s*>\s*2\s+THEN", result.Target!);
        Assert.Contains("v_return := false;", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_return := true;", result.Target, StringComparison.Ordinal);
        Assert.Contains("END IF;", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN v_return;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedAssignmentIfBlocks_ArePreserved()
    {
        var function = Object(
            InventoryObjectType.Function,
            "NestedChecks",
            """
            CREATE FUNCTION [dbo].[NestedChecks](@mode char(1))
            RETURNS bit AS
            BEGIN
                DECLARE @Return bit
                SET @Return = 0
                BEGIN
                    IF (@mode = 'A' OR @mode = 'B')
                    BEGIN
                        SET @Return = 1
                        IF (@Return = 1)
                        BEGIN
                            SET @Return = 0
                        END
                    END
                END
                RETURN @Return
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "bit"), Parameter(1, "@mode", "char", 1)]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Equal(2, Regex.Count(result.Target!, "END IF;", RegexOptions.CultureInvariant));
        Assert.Contains("IF p_mode = 'A' OR p_mode = 'B' THEN", result.Target, StringComparison.Ordinal);
        Assert.Matches(@"IF\s+v_return\s*=\s*true\s+THEN", result.Target!);
    }

    [Fact]
    public async Task CompressedAssignmentIfBlocks_AndOuterEndTerminator_AreParsed()
    {
        var function = Object(
            InventoryObjectType.Function,
            "CompressedChecks",
            """
            CREATE FUNCTION [dbo].[CompressedChecks](@mode char(1)) RETURNS bit AS
            BEGIN
            DECLARE @Return bit DECLARE @unused nvarchar(max)
            SET @Return=0 BEGIN IF (@mode='A') BEGIN SET @Return=1 END END RETURN @Return
            END;
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "bit"), Parameter(1, "@mode", "char", 1)]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("IF p_mode='A' THEN", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_return := true;", result.Target, StringComparison.Ordinal);
        Assert.Contains("END IF;", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN v_return;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignmentIfElse_WithReturnInsideElse_PreservesFinalReturn()
    {
        var function = Object(
            InventoryObjectType.Function,
            "FiscalYear",
            """
            CREATE FUNCTION [dbo].[FiscalYear](@myDate AS datetime) RETURNS varchar(10) AS
            BEGIN
                DECLARE @month int
                DECLARE @year int
                DECLARE @finyear varchar(20)
                SET @month = DATEPART(month, @myDate)
                SET @year = DATEPART(year, @myDate)
                IF @month >= 1 AND @month <= 3
                BEGIN
                    SET @finyear = CAST(@year - 1 AS varchar) + '-' + CAST(@year AS varchar)
                END
                ELSE
                BEGIN
                    SET @finyear = CAST(@year AS varchar) + '-' + CAST(@year + 1 AS varchar)
                    RETURN @finyear
                END
                RETURN @finyear
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "varchar", 10), Parameter(1, "@myDate", "datetime")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("IF v_month >= 1 AND v_month <= 3 THEN", result.Target, StringComparison.Ordinal);
        Assert.Contains("ELSE", result.Target, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(result.Target!, "RETURN v_finyear;", RegexOptions.CultureInvariant));
        Assert.Contains("END IF;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmployeeAge_GuardAndCaseGenerateCompletePlpgsqlFunction()
    {
        var function = Object(
            InventoryObjectType.Function,
            "fn_EmployeeAge",
            """
            CREATE FUNCTION cert.fn_EmployeeAge
            (
                @DateOfBirth DATE,
                @AsOfDate DATE
            )
            RETURNS INT
            AS
            BEGIN
                IF @DateOfBirth IS NULL OR @AsOfDate IS NULL RETURN NULL;

                RETURN DATEDIFF(YEAR, @DateOfBirth, @AsOfDate)
                       - CASE
                           WHEN DATEADD(YEAR, DATEDIFF(YEAR, @DateOfBirth, @AsOfDate), @DateOfBirth) > @AsOfDate
                           THEN 1 ELSE 0
                         END;
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [
                Parameter(0, string.Empty, "int"),
                Parameter(1, "@DateOfBirth", "date", 3),
                Parameter(2, "@AsOfDate", "date", 3)
            ]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Contains("RETURNS integer", result.Target, StringComparison.Ordinal);
        Assert.Contains("LANGUAGE plpgsql", result.Target, StringComparison.Ordinal);
        Assert.Contains(
            "IF p_dateofbirth IS NULL OR p_asofdate IS NULL THEN",
            result.Target,
            StringComparison.Ordinal);
        Assert.Contains("RETURN NULL;", result.Target, StringComparison.Ordinal);
        Assert.Contains("CASE", result.Target, StringComparison.Ordinal);
        Assert.Contains("END;", result.Target, StringComparison.Ordinal);
        Assert.Contains("$migrationstudio$;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("DATEDIFF", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@DateOfBirth", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsSimpleDataModificationProcedure()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "WriteLog",
            "CREATE PROCEDURE [dbo].[WriteLog] @name nvarchar(50) AS BEGIN INSERT INTO [dbo].[Log]([Name]) VALUES(@name) END");
        var module = Module(procedure, ModuleKind.StoredProcedure, [Parameter(1, "@name", "nvarchar", 100)]);
        var context = Context([procedure], [module]);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(procedure, context, CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("CREATE OR REPLACE PROCEDURE", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_name", result.Target, StringComparison.Ordinal);
        Assert.Contains("\"dbo\".\"Log\"", result.Target, StringComparison.Ordinal);
        Assert.Contains("VALUES(p_name);", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($";{Environment.NewLine}END;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_RemovesSqlServerOnlySessionSettings()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "CreateCustomer",
            """
            CREATE PROCEDURE [dbo].[CreateCustomer] @customerId bigint OUTPUT AS
            BEGIN
                SET NOCOUNT ON;
                SET XACT_ABORT ON;
                INSERT INTO [dbo].[Customer] DEFAULT VALUES;
                SET @customerId = SCOPE_IDENTITY();
            END
            """);
        var module = Module(
            procedure,
            ModuleKind.StoredProcedure,
            [Parameter(1, "@customerId", "bigint", isOutput: true)]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.DoesNotContain("NOCOUNT", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("XACT_ABORT", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p_customerid := lastval();", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_TranslatesUtcTemporalFunctionThroughExpressionTokenizer()
    {
        var log = Object(InventoryObjectType.Table, "Log", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "WriteUtcLog",
            "CREATE PROCEDURE [dbo].[WriteUtcLog] AS BEGIN " +
            "INSERT INTO [dbo].[Log] VALUES (GETUTCDATE()); END");
        var module = Module(procedure, ModuleKind.StoredProcedure, []);
        var inventory = TestInventory.CreateSnapshot([log, procedure]) with
        {
            Modules = [module],
            Dependencies =
            [
                new InventoryDependency(
                    procedure.Id,
                    log.Id,
                    DependencyKind.SqlExpression,
                    log.QualifiedSourceName,
                    true,
                    false)
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context(inventory),
            CancellationToken.None);

        Assert.Contains(
            "timezone('UTC', CURRENT_TIMESTAMP)",
            result.Target,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GETUTCDATE", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectAssignmentDoesNotMakeProcedureManual()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "AssignValue",
            "CREATE PROCEDURE [dbo].[AssignValue] @value int OUTPUT AS BEGIN SELECT @value = 7; END");
        var module = Module(
            procedure,
            ModuleKind.StoredProcedure,
            [Parameter(1, "@value", "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("SELECT 7 INTO p_value;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_DeclaresTypedLocalsAndTranslatesCompoundAssignmentsAndPrint()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "Accumulate",
            """
            CREATE PROCEDURE [dbo].[Accumulate] AS
            BEGIN
                DECLARE @message varchar(100) = 'start', @count int = 1;
                SET @message += ' done';
                SET @count += 2;
                PRINT(@message);
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_message varchar(100) := 'start';", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_count integer := 1;", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_message := v_message || ' done';", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_count := v_count + 2;", result.Target, StringComparison.Ordinal);
        Assert.Contains("RAISE NOTICE '%', v_message;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("@message", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_RemovesNoCountOffAndTranslatesSimpleSetAssignment()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "AssignLocal",
            """
            CREATE PROCEDURE [dbo].[AssignLocal] AS
            BEGIN
                SET NOCOUNT OFF
                DECLARE @count int;
                SET @count = 42
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_count := 42;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("NOCOUNT", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SET v_", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_WithUnparsedIfElseUsesValidManualReviewStub()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "Conditional",
            "CREATE PROCEDURE [dbo].[Conditional] AS BEGIN IF 1 = 1 PRINT 'yes'; ELSE PRINT 'no'; END");
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.NotNull(result.Target);
        Assert.Contains("RAISE EXCEPTION 'Manual conversion required", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("IF 1 = 1", result.Target!.Split("/* Source T-SQL:")[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("procedure IF/ELSE control flow", result.UnsupportedConstructs, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Procedure_WithStaticSqlServerExecUsesValidManualReviewStub()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "EncryptionWrapper",
            "CREATE PROCEDURE [dbo].[EncryptionWrapper] AS BEGIN EXEC openkey; EXECUTE closekey; END");
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("dynamic SQL", result.UnsupportedConstructs, StringComparer.Ordinal);
        Assert.NotNull(result.Target);
        Assert.DoesNotContain("EXEC openkey", result.Target!.Split("/* Source T-SQL:")[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_ExecutesDeclaredDynamicSqlExpression()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "DynamicSelect",
            """
            CREATE PROCEDURE [dbo].[DynamicSelect] AS
            BEGIN
                DECLARE @sql nvarchar(max) = N'SELECT 1';
                EXECUTE (@sql);
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [], containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_sql text := 'SELECT 1';", result.Target, StringComparison.Ordinal);
        Assert.Contains("EXECUTE v_sql;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("EXECUTE (", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@sql", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_PreservesMultilineDynamicSqlAssignmentBeforePrintAndExecute()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "DynamicReport",
            """
            CREATE PROCEDURE [dbo].[DynamicReport] @table_name varchar(50) AS
            BEGIN
                DECLARE @sql nvarchar(max);
                SET @sql = 'SELECT *
            FROM ' + @table_name
                PRINT(@sql)
                EXEC(@sql)
            END
            """);
        var module = Module(
            procedure,
            ModuleKind.StoredProcedure,
            [Parameter(1, "@table_name", "varchar", 50)],
            containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_sql := 'SELECT *", result.Target, StringComparison.Ordinal);
        Assert.Contains("FROM ' || p_table_name;", result.Target, StringComparison.Ordinal);
        Assert.Contains("RAISE NOTICE '%', v_sql;", result.Target, StringComparison.Ordinal);
        Assert.Contains("EXECUTE v_sql;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_RemovesStandaloneGroupingBlockBeforePrintAndDynamicExecute()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "DynamicReportWithGroupingBlock",
            """
            CREATE PROCEDURE [dbo].[DynamicReportWithGroupingBlock] AS
            DECLARE @sql nvarchar(max)
            BEGIN
                SET @sql = 'SELECT 1'
            END
            PRINT @sql
            EXEC(@sql)
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [], containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_sql := 'SELECT 1';", result.Target, StringComparison.Ordinal);
        Assert.Contains("RAISE NOTICE '%', v_sql;", result.Target, StringComparison.Ordinal);
        Assert.Contains("EXECUTE v_sql;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("END\nRAISE", result.Target!.Replace("\r\n", "\n"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("END\nSELECT", result.Target.Replace("\r\n", "\n"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScalarSubqueryAssignmentBeforeIf_PlacesIntoBeforeTerminator()
    {
        var sourceTable = Object(InventoryObjectType.Table, "DaySource", null);
        var function = Object(
            InventoryObjectType.Function,
            "HasDays",
            """
            CREATE FUNCTION [dbo].[HasDays](@id int) RETURNS bit AS
            BEGIN
                DECLARE @days int
                DECLARE @result bit
                SELECT @days = (SELECT total_days FROM DaySource WHERE id = @id);
                IF @days > 0
                    SET @result = 1
                ELSE
                    SET @result = 0
                RETURN @result
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "bit"), Parameter(1, "@id", "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([sourceTable, function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains(") INTO v_days;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("); INTO", result.Target, StringComparison.Ordinal);
        Assert.Contains("END IF;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetadataMultiTargetSelectAssignment_UsesManualReviewStub()
    {
        var function = Object(
            InventoryObjectType.Function,
            "MetadataFlags",
            """
            CREATE FUNCTION [dbo].[MetadataFlags]() RETURNS int AS
            BEGIN
                DECLARE @first int
                DECLARE @second int
                SELECT @first = OBJECT_ID('dbo.First'), @second = OBJECT_ID('dbo.Second')
                IF @first IS NOT NULL SET @second = @second + 1
                RETURN @second
            END
            """);
        var module = Module(function, ModuleKind.ScalarFunction, [Parameter(0, string.Empty, "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("metadata multi-target SELECT assignment", result.UnsupportedConstructs, StringComparer.Ordinal);
        Assert.Contains("RAISE EXCEPTION 'Manual conversion required", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURNS integer", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_AddsOptionalIntoToSqlServerInsertSyntax()
    {
        var targetTable = Object(InventoryObjectType.Table, "History", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "WriteHistory",
            "CREATE PROCEDURE [dbo].[WriteHistory] @id int AS BEGIN INSERT History(Id) VALUES(@id) END");
        var module = Module(procedure, ModuleKind.StoredProcedure, [Parameter(1, "@id", "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([targetTable, procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("INSERT INTO", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VALUES(p_id);", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_TerminatesAdjacentDmlStatements()
    {
        var first = Object(InventoryObjectType.Table, "FirstTable", null);
        var second = Object(InventoryObjectType.Table, "SecondTable", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "WriteAndUpdate",
            """
            CREATE PROCEDURE [dbo].[WriteAndUpdate] @id int AS
            BEGIN
                INSERT INTO FirstTable(Id) VALUES(@id)
                UPDATE SecondTable SET Id = @id
                UPDATE FirstTable SET Id = @id
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [Parameter(1, "@id", "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([first, second, procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Matches(@"VALUES\(p_id\);\s+UPDATE", result.Target!);
        Assert.Matches(@"SET Id = p_id;\s+UPDATE", result.Target!);
    }

    [Fact]
    public async Task Procedure_RemovesWithNoLockWithoutLeavingDanglingWith()
    {
        var sourceTable = Object(InventoryObjectType.Table, "Queue", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "UpdateQueue",
            """
            CREATE PROCEDURE [dbo].[UpdateQueue] AS
            BEGIN
                UPDATE Queue SET Status = 1
                WHERE Id IN (SELECT Id FROM Queue WITH (NOLOCK) WHERE Status = 0)
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([sourceTable, procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.DoesNotContain("NOLOCK", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Queue WITH", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_UpdatableSqlServerCteUsesManualReviewStub()
    {
        var sourceTable = Object(InventoryObjectType.Table, "Queue", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "UpdateQueueCte",
            """
            CREATE PROCEDURE [dbo].[UpdateQueueCte] AS
            BEGIN
                WITH pending AS
                (SELECT Id, Status FROM Queue WITH (NOLOCK) WHERE Status = 0)
                UPDATE pending SET Status = 1
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([sourceTable, procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("updatable CTE", result.UnsupportedConstructs, StringComparer.Ordinal);
        Assert.Contains("CREATE OR REPLACE PROCEDURE", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Procedure_RemovesAnsiWarningsSessionDirective()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "SessionDirective",
            """
            CREATE PROCEDURE [dbo].[SessionDirective] AS
            BEGIN
                SET ANSI_WARNINGS OFF;
                DECLARE @sql nvarchar(max);
                SET @sql = 'SELECT 1';
                EXEC(@sql);
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [], containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.DoesNotContain("ANSI_WARNINGS", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EXECUTE v_sql;", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_ExecutesConcatenatedDeclaredDynamicSql()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "DynamicBatch",
            """
            CREATE PROCEDURE [dbo].[DynamicBatch] AS
            BEGIN
                DECLARE @sql1 nvarchar(max)
                DECLARE @sql2 nvarchar(max)
                SET @sql1 = 'SELECT 1'
                SET @sql2 = '; SELECT 2'
                PRINT @sql1 + @sql2
                EXECUTE(@sql1 + @sql2)
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [], containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("RAISE NOTICE '%', v_sql1 || v_sql2;", result.Target, StringComparison.Ordinal);
        Assert.Contains("EXECUTE v_sql1 || v_sql2;", result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("EXECUTE(", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResultSetSelectHasSpecificManualReason()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "ReturnRows",
            "CREATE PROCEDURE [dbo].[ReturnRows] AS BEGIN\nSELECT [Id] FROM [dbo].[Log];\nEND");
        var module = Module(procedure, ModuleKind.StoredProcedure, []);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains(
            "dynamic or multiple result-set interface",
            result.UnsupportedConstructs,
            StringComparer.Ordinal);
    }

    [Fact]
    public async Task ScalarCaseReturn_PreservesCaseEndBeforeRoutineEnd()
    {
        var function = Object(
            InventoryObjectType.Function,
            "FiscalYearNew",
            """
            CREATE FUNCTION dbo.FiscalYearNew(@value datetime) RETURNS varchar(10) AS
            BEGIN
                RETURN CASE WHEN MONTH(@value) >= 4
                    THEN CAST(YEAR(@value) AS varchar) + '-' + CAST(YEAR(@value) + 1 AS varchar)
                    ELSE CAST(YEAR(@value) - 1 AS varchar) + '-' + CAST(YEAR(@value) AS varchar) END
            END
            """);
        var module = Module(
            function,
            ModuleKind.ScalarFunction,
            [Parameter(0, string.Empty, "varchar", 10), Parameter(1, "@value", "datetime")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            function,
            Context([function], [module]),
            CancellationToken.None);

        Assert.NotEqual(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("ELSE", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(" END;", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'-'", result.Target, StringComparison.Ordinal);
        Assert.Contains("||", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Procedure_FinalDmlBeforeTrailingCommentGetsEffectiveTerminator()
    {
        var table = Object(InventoryObjectType.Table, "GroupMaster", null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "InsertGroup",
            """
            CREATE PROCEDURE dbo.InsertGroup @id int AS
            BEGIN
                INSERT INTO GroupMaster(Id) VALUES(@id)
                -- execute InsertGroup
            END
            """);
        var module = Module(procedure, ModuleKind.StoredProcedure, [Parameter(1, "@id", "int")]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([table, procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Matches(@"VALUES\(p_id\);\s*-- execute", result.Target!);
    }

    [Fact]
    public async Task Procedure_DeclarationInitializerStopsBeforeFollowingSet()
    {
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "BuildSql",
            """
            CREATE PROCEDURE dbo.BuildSql @fin_year char(9) AS
            BEGIN
                DECLARE @fy char(4)
                DECLARE @sql varchar(8000) = ''
                SET @fy = SUBSTRING(@fin_year, 3, 2) + SUBSTRING(@fin_year, 8, 2)
                SET @sql = 'SELECT ' + @fy
                EXEC(@sql)
            END
            """);
        var module = Module(
            procedure,
            ModuleKind.StoredProcedure,
            [Parameter(1, "@fin_year", "char", 9)],
            containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            procedure,
            Context([procedure], [module]),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("v_sql varchar(8000) := '';", result.Target, StringComparison.Ordinal);
        Assert.Contains("v_fy := substring(p_fin_year, 3, 2)", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("v_sql varchar(8000) := v_fy", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewMapsGloballyUniqueUnqualifiedRelationsAcrossUnionBranches()
    {
        var first = Object(InventoryObjectType.Table, "FirstRoll", null) with
        {
            SourceSchema = "legacy",
            QualifiedSourceName = "[legacy].[FirstRoll]"
        };
        var second = Object(InventoryObjectType.Table, "SecondRoll", null) with
        {
            SourceSchema = "legacy",
            QualifiedSourceName = "[legacy].[SecondRoll]"
        };
        var view = Object(
            InventoryObjectType.View,
            "AllRolls",
            "CREATE VIEW dbo.AllRolls AS SELECT * FROM FirstRoll UNION ALL SELECT * FROM SecondRoll");
        var module = Module(view, ModuleKind.View, []);
        var context = Context([first, second, view], [module]);

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            view,
            context,
            CancellationToken.None);

        Assert.Contains(context.Identifiers.MapObject(first).QualifiedName, result.Target, StringComparison.Ordinal);
        Assert.Contains(context.Identifiers.MapObject(second).QualifiedName, result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM FirstRoll", result.Target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM SecondRoll", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewWithUnresolvedCatalogRelationUsesValidManualStub()
    {
        var view = Object(
            InventoryObjectType.View,
            "UnresolvedView",
            "CREATE VIEW dbo.UnresolvedView AS SELECT * FROM MissingRoll");
        var module = Module(view, ModuleKind.View, []);
        var inventory = TestInventory.CreateSnapshot([view]) with
        {
            Modules = [module],
            Dependencies =
            [
                new InventoryDependency(
                    view.Id,
                    null,
                    DependencyKind.SqlExpression,
                    "MissingRoll",
                    false,
                    false)
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            view,
            Context(inventory),
            CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains("unresolved relation MissingRoll", result.UnsupportedConstructs, StringComparer.Ordinal);
        Assert.Contains("SELECT NULL::text AS manual_review WHERE false", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ViewMapsUnqualifiedTableReferenceThroughFinalIdentifierMap()
    {
        var longTableName = new string('T', 75);
        var table = Object(InventoryObjectType.Table, longTableName, null);
        var view = Object(
            InventoryObjectType.View,
            "MappedView",
            $"CREATE VIEW [dbo].[MappedView] AS SELECT 1 AS [Value] FROM [{longTableName}];");
        var inventory = TestInventory.CreateSnapshot([table, view]) with
        {
            Modules = [Module(view, ModuleKind.View, [])],
            Dependencies =
            [
                new InventoryDependency(
                    view.Id,
                    table.Id,
                    DependencyKind.SqlExpression,
                    table.QualifiedSourceName,
                    true,
                    false)
            ]
        };
        var context = Context(inventory);
        var mappedTable = context.Identifiers.MapObject(table);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(view, context, CancellationToken.None);

        Assert.NotEqual(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains(mappedTable.QualifiedName, result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{longTableName}]", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcedureMapsUnqualifiedTableReferenceThroughFinalIdentifierMap()
    {
        var longTableName = new string('P', 75);
        var table = Object(InventoryObjectType.Table, longTableName, null);
        var procedure = Object(
            InventoryObjectType.StoredProcedure,
            "WriteMapped",
            $"CREATE PROCEDURE [dbo].[WriteMapped] AS BEGIN INSERT INTO [{longTableName}] DEFAULT VALUES; END");
        var inventory = TestInventory.CreateSnapshot([table, procedure]) with
        {
            Modules = [Module(procedure, ModuleKind.StoredProcedure, [])],
            Dependencies =
            [
                new InventoryDependency(
                    procedure.Id,
                    table.Id,
                    DependencyKind.SqlExpression,
                    table.QualifiedSourceName,
                    true,
                    false)
            ]
        };
        var context = Context(inventory);
        var mappedTable = context.Identifiers.MapObject(table);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(procedure, context, CancellationToken.None);

        Assert.NotEqual(ConversionClassification.ManualConversion, result.Classification);
        Assert.Contains(mappedTable.QualifiedName, result.Target, StringComparison.Ordinal);
        Assert.DoesNotContain($"[{longTableName}]", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConvertsAfterTriggerWithStatementTransitionTables()
    {
        var table = Object(InventoryObjectType.Table, "Customer", null);
        var trigger = Object(
            InventoryObjectType.Trigger,
            "CustomerAudit",
            "CREATE TRIGGER [dbo].[CustomerAudit] ON [dbo].[Customer] AFTER INSERT AS BEGIN INSERT INTO [dbo].[Audit] SELECT * FROM inserted; END",
            table.Id);
        var module = Module(trigger, ModuleKind.DmlTrigger, []);
        var inventory = TestInventory.CreateSnapshot([table, trigger]) with
        {
            Modules = [module],
            Triggers =
            [
                new TriggerInventory(
                    trigger.Id, table.Id, "OBJECT", false, false, false, null, ["INSERT"], [], [])
            ]
        };
        var context = Context(inventory);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(trigger, context, CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("NEW TABLE AS inserted", result.Target, StringComparison.Ordinal);
        Assert.Contains("FOR EACH STATEMENT", result.Target, StringComparison.Ordinal);
        Assert.Contains("RETURN NULL", result.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trigger_TranslatesUtcTemporalFunctionThroughExpressionTokenizer()
    {
        var table = Object(InventoryObjectType.Table, "Customer", null);
        var trigger = Object(
            InventoryObjectType.Trigger,
            "CustomerUtc",
            "CREATE TRIGGER [dbo].[CustomerUtc] ON [dbo].[Customer] AFTER INSERT AS " +
            "BEGIN PERFORM SYSUTCDATETIME(); END",
            table.Id);
        var module = Module(trigger, ModuleKind.DmlTrigger, []);
        var inventory = TestInventory.CreateSnapshot([table, trigger]) with
        {
            Modules = [module],
            Triggers =
            [
                new TriggerInventory(
                    trigger.Id, table.Id, "OBJECT", false, false, false, null, ["INSERT"], [], [])
            ]
        };

        var result = await new ProgrammableObjectConverter().ConvertAsync(
            trigger,
            Context(inventory),
            CancellationToken.None);

        Assert.Contains(
            "timezone('UTC', CURRENT_TIMESTAMP)",
            result.Target,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SYSUTCDATETIME", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DynamicSqlProcedure_IsManualAndPreservesSource()
    {
        const string sourceSql =
            "CREATE PROCEDURE [dbo].[Dynamic] AS BEGIN EXEC sp_executesql N'SELECT 1'; END";
        var procedure = Object(InventoryObjectType.StoredProcedure, "Dynamic", sourceSql);
        var module = Module(procedure, ModuleKind.StoredProcedure, [], containsDynamicSql: true);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(procedure, Context([procedure], [module]), CancellationToken.None);

        Assert.Equal(ConversionClassification.ManualConversion, result.Classification);
        Assert.True(result.RequiresManualReview);
        Assert.Contains("sp_executesql", result.Target, StringComparison.OrdinalIgnoreCase);
    }

    private static ConversionContext Context(
        IReadOnlyList<InventoryObject> objects,
        IReadOnlyList<ModuleInventory> modules) =>
        Context(TestInventory.CreateSnapshot(objects) with { Modules = modules });

    private static ConversionContext Context(InventorySnapshot inventory)
    {
        var options = new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.PreserveQuoted };
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(inventory, options);
        var byId = inventory.Objects.ToDictionary(item => item.Id);
        return new ConversionContext(
            inventory,
            options,
            mapper,
            new PostgreSqlTypeMappingRegistry(),
            new StructuredSqlExpressionTranslator(),
            byId,
            byId.ToDictionary(item => item.Key, item => mapper.MapObject(item.Value)));
    }

    private static InventoryObject Object(
        InventoryObjectType type,
        string name,
        string? definition,
        InventoryObjectId? parent = null)
    {
        var id = InventoryObjectId.Create("fixture", type, "dbo", name, name.GetHashCode(StringComparison.Ordinal), parent);
        return new InventoryObject(
            id, "fixture", "dbo", name, $"[dbo].[{name}]", type, null, parent, null, null, false, true,
            SelectionReason.CompleteDatabase, 0, 0, [], InventoryClassification.ForObject(type), definition,
            null, "hash", [], DiscoveryStatus.Discovered);
    }

    private static ModuleInventory Module(
        InventoryObject item,
        ModuleKind kind,
        IReadOnlyList<ModuleParameterInventory> parameters,
        bool containsDynamicSql = false) =>
        new(
            item.Id, kind, true, true, false, false, false, false, null, containsDynamicSql, false,
            false, false, parameters, []);

    private static ModuleParameterInventory Parameter(
        int id,
        string name,
        string type,
        short length = 4,
        bool isOutput = false) =>
        new(id, name, "sys", type, length, 18, 0, isOutput, false, null, false, false);
}
