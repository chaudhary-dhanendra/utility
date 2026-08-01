using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Infrastructure.SqlServer;

namespace MigrationStudio.Tests.Conversion;

public sealed class CentralIdentifierMappingTests
{
    [Theory]
    [InlineData("U")]
    [InlineData("U ")]
    public void SqlServerPaddedUserTableType_IsClassifiedAsTable(string sqlType)
    {
        Assert.Equal(
            InventoryObjectType.Table,
            SqlServerInventoryDiscoveryService.MapObjectType(sqlType));
    }

    [Fact]
    public void LegacyUnknownTable_EagerlyMapsEveryIncludedColumnIncludingProductionExample()
    {
        var fixture = ProductionExample();
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            fixture.Inventory,
            new ConversionOptions());
        Assert.NotEqual(Guid.Empty, mapper.MappingSetId);
        Assert.Equal(IdentifierMappingSchema.CurrentVersion, mapper.SchemaVersion);
        Assert.False(mapper.LoadedFromCache);

        var table = mapper.Mappings.Single(item =>
            item.SourceKey.ObjectId == fixture.Table.Id &&
            item.ObjectType == InventoryObjectType.Table.ToString());
        var column = mapper.Mappings.Single(item =>
            item.SourceKey.ObjectId == fixture.Column.ObjectId &&
            item.SourceKey.ParentObjectId == fixture.Table.Id &&
            item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("nrega_sk", table.TargetSchema);
        Assert.Equal("verify_observe1819", table.TargetName);
        Assert.Equal("discre_obsrv", column.TargetName);
        Assert.Equal(
            new ColumnIdentifierKey(fixture.Table.Id, fixture.Column.ColumnId),
            column.SourceKey.ColumnKey);
        Assert.Equal(fixture.Column.ObjectId, column.SourceKey.ObjectId);
        Assert.Equal(fixture.Table.Id, column.SourceKey.ParentObjectId);
        Assert.False(column.IsBlocking);
    }

    [Fact]
    public void IncludedTriggerMissingFromFacet_IsAutoRecoveredBeforeConversion()
    {
        var schema = Object(InventoryObjectType.Schema, string.Empty, "nrega_SK", 10, true);
        var table = Object(
            InventoryObjectType.Table,
            "nrega_SK",
            "DigiPay_TrainerDetails",
            1543491571,
            true);
        var trigger = Object(
            InventoryObjectType.Trigger,
            "nrega_SK",
            "TRG_DigiPay_TrainerDetailsHistory_Del",
            119642663,
            true) with
        {
            ParentObjectId = table.Id
        };
        var inventory = TestInventory.CreateSnapshot([schema, table, trigger]) with
        {
            Database = TestInventory.CreateSnapshot([]).Database with
            {
                DatabaseName = "vbgramg"
            },
            Schemas =
            [
                new SchemaInventory(schema, "dbo", 2, false, true)
            ],
            Tables =
            [
                new TableInventory(
                    table.Id, TableKind.Ordinary, false, null, false, 0, null, false, false,
                    false, false, false, false, false, 1, 0, 0, [])
            ],
            Triggers = []
        };

        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            inventory,
            new ConversionOptions());

        var mapping = Assert.Single(mapper.Mappings, item =>
            item.SourceKey.ObjectId == trigger.Id);
        var key = Assert.IsType<TriggerIdentifierKey>(mapping.SourceKey.TriggerKey);
        Assert.Equal(trigger.Id, key.TriggerObjectId);
        Assert.Equal(table.Id, key.ParentTableObjectId);
        Assert.Equal(schema.Id, key.SourceSchemaId);
        Assert.Equal(trigger.SourceName, key.SourceName);
        Assert.Equal(mapper.MapObject(table).QualifiedName, mapping.TargetParentObject);
        Assert.Equal("trg_digipay_trainerdetailshistory_del", mapper.MapObject(trigger).Name);
        Assert.True(mapping.AutoRecovered);
        Assert.Equal(IdentifierMappingAction.AutoRecovered, mapping.MappingAction);
    }

    [Fact]
    public void MissingActiveColumnMapping_IsAutoRecoveredAndPreviewPlanContinues()
    {
        var fixture = ProductionExample();
        var options = new ConversionOptions();
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            fixture.Inventory,
            options);
        var tableOnly = mapper.Mappings.Where(item =>
            item.SourceKey.ObjectId == fixture.Table.Id &&
            !item.ObjectType.Equals("column", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var conversion = new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "vbgramg",
            options.TargetVersion,
            options,
            tableOnly,
            [],
            [],
            [],
            [],
            "test");
        conversion = conversion with
        {
            MappingSet = new IdentifierMappingSetMetadata(
                Guid.NewGuid(),
                IdentifierMappingSchema.CurrentVersion,
                DateTimeOffset.UtcNow,
                false,
                tableOnly.Length,
                tableOnly.Length,
                1,
                1)
        };
        var request = new DataMigrationRequest(
            fixture.Inventory,
            conversion,
            new SqlServerConnectionOptions
            {
                Server = "localhost",
                Database = "vbgramg"
            },
            "Host=localhost;Database=target;Username=postgres;Password=not-logged",
            new DataMigrationOptions
            {
                ExecutionMode = DataMigrationExecutionMode.Preview
            });

        var plan = new DataMigrationPlanner(
            new SensitiveColumnClassifier()).CreatePlan(request);

        var table = Assert.Single(plan.Tables);
        var column = Assert.Single(table.Columns);
        Assert.Equal("discre_obsrv", column.SourceName);
        Assert.Equal("discre_obsrv", column.TargetName);
        var recovered = Assert.Single(plan.RecoveredIdentifierMappings);
        Assert.True(recovered.AutoRecovered);
        Assert.Equal(IdentifierMappingAction.AutoRecovered, recovered.MappingAction);
    }

    [Fact]
    public void ConversionSession_RejectsLegacyCachedMappingSchema()
    {
        var legacy = new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "vbgramg",
            new PostgreSqlVersion(18),
            new ConversionOptions(),
            [],
            [],
            [],
            [],
            [],
            "legacy");
        var session = new ConversionSession();

        var failure = Assert.Throws<InvalidOperationException>(
            () => session.SetCurrent(legacy));

        Assert.Contains("stale", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.Current);
    }

    [Fact]
    public void SourceSchemaLookup_RespectsDatabaseCollationCaseSensitivity()
    {
        var insensitive = ProductionExample().Inventory;
        var insensitiveMapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            insensitive,
            new ConversionOptions());
        Assert.Equal(
            insensitiveMapper.MapSchema("nrega_SK"),
            insensitiveMapper.MapSchema("NREGA_sk"));

        var sensitive = insensitive with
        {
            Database = insensitive.Database with
            {
                Collation = "Latin1_General_100_CS_AS"
            },
            Schemas =
            [
                Schema("CaseSchema", true),
                Schema("caseschema", true)
            ]
        };
        var sensitiveMapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            sensitive,
            new ConversionOptions());

        Assert.NotEqual(
            sensitiveMapper.MapSchema("CaseSchema"),
            sensitiveMapper.MapSchema("caseschema"));
    }

    [Fact]
    public void AutomaticPolicy_SanitizesInvalidCharactersAndLeadingDigits()
    {
        var source = Object(
            InventoryObjectType.Table,
            "dbo",
            " 123 bad/name ",
            42,
            true);
        var inventory = TestInventory.CreateSnapshot([source]) with
        {
            Schemas = [Schema("dbo", true)]
        };

        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            inventory,
            new ConversionOptions());
        var mapping = mapper.Mappings.Single(item =>
            item.SourceKey.ObjectId == source.Id);

        Assert.Equal("_123_bad_name", mapper.MapObject(source).Name);
        Assert.True(mapping.InvalidCharacterReplacement);
        Assert.Equal(IdentifierMappingAction.Sanitized, mapping.MappingAction);
    }

    private static ProductionFixture ProductionExample()
    {
        var table = Object(
            InventoryObjectType.Unknown,
            "nrega_SK",
            "verify_observe1819",
            1819,
            true) with
        {
            Id = new InventoryObjectId(
                Guid.Parse("e20dc7da-e0b9-5230-82a4-a8b16d0002a0"))
        };
        var columnId = new InventoryObjectId(
            Guid.Parse("09480727-c89a-5afb-bc67-0d95ca5177ce"));
        var column = new ColumnInventory(
            ObjectId: columnId,
            ParentObjectId: table.Id,
            ColumnId: 4,
            OrdinalPosition: 4,
            Name: "discre_obsrv",
            SystemTypeName: "nvarchar",
            UserTypeName: "nvarchar",
            TypeSchema: "sys",
            MaximumLength: 200,
            Precision: 0,
            Scale: 0,
            Collation: "Latin1_General_100_CI_AS",
            IsNullable: true,
            IsIdentity: false,
            IdentitySeed: null,
            IdentityIncrement: null,
            IdentityLastValue: null,
            IsIdentityNotForReplication: false,
            IsComputed: false,
            ComputedDefinition: null,
            IsComputedPersisted: false,
            IsComputedDeterministic: null,
            IsSparse: false,
            IsColumnSet: false,
            IsRowGuidColumn: false,
            IsFileStream: false,
            GeneratedAlwaysType: 0,
            IsHidden: false,
            IsMasked: false,
            MaskingFunction: null,
            EncryptionType: null,
            EncryptionAlgorithm: null,
            ColumnEncryptionKey: null,
            XmlSchemaCollection: null,
            DefaultConstraintName: null,
            DefaultDefinition: null,
            RuleName: null,
            ExtendedProperties: []);
        var inventory = TestInventory.CreateSnapshot([table]) with
        {
            Database = TestInventory.CreateSnapshot([]).Database with
            {
                DatabaseName = "vbgramg"
            },
            Schemas = [Schema("nrega_SK", true)],
            Tables =
            [
                new TableInventory(
                    table.Id,
                    TableKind.Ordinary,
                    false,
                    null,
                    false,
                    0,
                    null,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    1,
                    0,
                    0,
                    [])
            ],
            Columns = [column]
        };
        return new ProductionFixture(inventory, table, column);
    }

    private static InventoryObject Object(
        InventoryObjectType type,
        string schema,
        string name,
        int sqlId,
        bool included)
    {
        var id = InventoryObjectId.Create("vbgramg", type, schema, name, sqlId);
        return new InventoryObject(
            id,
            "vbgramg",
            schema,
            name,
            $"[{schema}].[{name}]",
            type,
            sqlId,
            null,
            null,
            null,
            false,
            included,
            SelectionReason.CompleteDatabase,
            0,
            0,
            [],
            ConversionClassification.Automatic,
            null,
            null,
            "hash",
            [],
            DiscoveryStatus.Discovered);
    }

    private static SchemaInventory Schema(string name, bool included)
    {
        var source = Object(
            InventoryObjectType.Schema,
            string.Empty,
            name,
            name.GetHashCode(StringComparison.Ordinal),
            included);
        return new SchemaInventory(source, "dbo", 1, false, true);
    }

    private sealed record ProductionFixture(
        InventorySnapshot Inventory,
        InventoryObject Table,
        ColumnInventory Column);
}
