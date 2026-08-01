using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Conversion.Converters;

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
            "CREATE PROCEDURE [dbo].[WriteLog] @name nvarchar(50) AS BEGIN INSERT INTO [dbo].[Log]([Name]) VALUES(@name); END");
        var module = Module(procedure, ModuleKind.StoredProcedure, [Parameter(1, "@name", "nvarchar", 100)]);
        var context = Context([procedure], [module]);

        var result = await new ProgrammableObjectConverter()
            .ConvertAsync(procedure, context, CancellationToken.None);

        Assert.Equal(ConversionClassification.AutomaticWithWarning, result.Classification);
        Assert.Contains("CREATE OR REPLACE PROCEDURE", result.Target, StringComparison.Ordinal);
        Assert.Contains("p_name", result.Target, StringComparison.Ordinal);
        Assert.Contains("\"dbo\".\"Log\"", result.Target, StringComparison.Ordinal);
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
            "result-set SELECT interface",
            result.UnsupportedConstructs,
            StringComparer.Ordinal);
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
