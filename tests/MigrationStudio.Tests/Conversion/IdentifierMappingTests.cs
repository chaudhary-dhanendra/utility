using System.Text;
using System.Diagnostics;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;

namespace MigrationStudio.Tests.Conversion;

public sealed class IdentifierMappingTests
{
    [Fact]
    public void Mapper_IsByteAwareDeterministicAndQuotesReservedWords()
    {
        var longName = string.Concat(Enumerable.Repeat("資料", 40));
        var objects = new[]
        {
            Object("dbo", longName, 1),
            Object("dbo", "Select", 2)
        };
        var inventory = TestInventory.CreateSnapshot(objects) with
        {
            Schemas = [Schema("dbo")]
        };
        var service = new PostgreSqlIdentifierMappingService();

        var first = service.CreateMapper(inventory, new ConversionOptions());
        var second = service.CreateMapper(inventory, new ConversionOptions());
        var shortened = first.MapObject(objects[0]);

        Assert.True(Encoding.UTF8.GetByteCount(shortened.Name.Trim('"')) <= 63);
        Assert.Equal(shortened, second.MapObject(objects[0]));
        Assert.Equal("\"select\"", first.MapObject(objects[1]).Name);
        Assert.Equal("dbo", first.MapObject(objects[0]).Schema);
        Assert.True(first.Mappings.Single(item => item.SourceObjectId == objects[0].Id).WasShortened);
    }

    [Fact]
    public void Mapper_ResolvesNormalizationCollisionsDeterministically()
    {
        var objects = new[] { Object("dbo", "Customer", 1), Object("dbo", "customer", 2) };
        var inventory = TestInventory.CreateSnapshot(objects) with { Schemas = [Schema("dbo")] };
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(inventory, new ConversionOptions());

        Assert.NotEqual(mapper.MapObject(objects[0]).Name, mapper.MapObject(objects[1]).Name);
        Assert.Contains(mapper.Mappings, item => item.HadCollision);
    }

    [Fact]
    public void Mapper_ThousandsOfIdentifiersWithSameUtf8Prefix_TerminatesAndRemainsUnique()
    {
        const int count = 5_000;
        var sharedPrefix = new string('x', 96);
        var objects = Enumerable.Range(1, count)
            .Select(index => Object("dbo", $"{sharedPrefix}_{index:D5}", index))
            .ToArray();
        var inventory = TestInventory.CreateSnapshot(objects) with
        {
            Schemas = [Schema("dbo")]
        };
        var stopwatch = Stopwatch.StartNew();

        var mapper = new PostgreSqlIdentifierMappingService()
            .CreateMapper(inventory, new ConversionOptions());

        stopwatch.Stop();
        var names = objects.Select(item => mapper.MapObject(item).Name.Trim('"')).ToArray();
        Assert.Equal(count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.True(Encoding.UTF8.GetByteCount(name) <= 63));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public void Mapper_OutputDoesNotDependOnInventoryEnumerationOrder()
    {
        var objects = new[]
        {
            Object("dbo", "CustomerID", 1),
            Object("dbo", "customerid", 2),
            Object("dbo", "CustomerId", 3),
            Object("dbo", new string('x', 80), 4)
        };
        var forwardInventory = TestInventory.CreateSnapshot(objects) with
        {
            Schemas = [Schema("dbo")]
        };
        var reverseInventory = forwardInventory with
        {
            Objects = forwardInventory.Objects.Reverse().ToArray()
        };
        var service = new PostgreSqlIdentifierMappingService();

        var forward = service.CreateMapper(forwardInventory, new ConversionOptions()).Mappings
            .Where(item => item.ObjectType == InventoryObjectType.Table.ToString())
            .ToDictionary(item => item.SourceKey, item => item.TargetQualifiedName);
        var reverse = service.CreateMapper(reverseInventory, new ConversionOptions()).Mappings
            .Where(item => item.ObjectType == InventoryObjectType.Table.ToString())
            .ToDictionary(item => item.SourceKey, item => item.TargetQualifiedName);

        Assert.Equal(forward.Count, reverse.Count);
        Assert.All(forward, item => Assert.Equal(item.Value, reverse[item.Key]));
    }

    [Fact]
    public void Mapper_AppliesCustomSchemaMapping()
    {
        var item = Object("sales", "Order", 1);
        var inventory = TestInventory.CreateSnapshot([item]) with { Schemas = [Schema("sales")] };
        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            inventory,
            new ConversionOptions
            {
                SchemaMappingMode = SchemaMappingMode.Custom,
                SchemaMappings = [new SchemaMappingRule("sales", "commerce")]
            });

        Assert.Equal("commerce", mapper.MapObject(item).Schema);
    }

    [Fact]
    public void Mapper_PreserveModeMapsDboToDbo()
    {
        var item = Object("dbo", "Customer", 1);

        var mapper = CreateMapper(
            [item],
            new ConversionOptions { SchemaMappingMode = SchemaMappingMode.Preserve });

        Assert.Equal("dbo", mapper.MapObject(item).Schema);
    }

    [Fact]
    public void Mapper_MapDboToPublicMapsOnlyDbo()
    {
        var dbo = Object("dbo", "Customer", 1);
        var sales = Object("sales", "Order", 2);

        var mapper = CreateMapper(
            [dbo, sales],
            new ConversionOptions { SchemaMappingMode = SchemaMappingMode.MapDboToPublic });

        Assert.Equal("public", mapper.MapObject(dbo).Schema);
        Assert.Equal("sales", mapper.MapObject(sales).Schema);
    }

    [Fact]
    public void Mapper_ExplicitRuleOverridesBuiltInSchemaModeCaseInsensitively()
    {
        var dbo = Object("DBO", "Customer", 1);

        var mapper = CreateMapper(
            [dbo],
            new ConversionOptions
            {
                SchemaMappingMode = SchemaMappingMode.MapDboToPublic,
                SchemaMappings = [new SchemaMappingRule("dbo", "Commerce")]
            });

        Assert.Equal("commerce", mapper.MapObject(dbo).Schema);
    }

    [Fact]
    public void Mapper_NormalizesMultipleSourceSchemasIndependently()
    {
        var sales = Object("Sales Data", "Order", 1);
        var archive = Object("Archive", "Order", 2);

        var mapper = CreateMapper(
            [sales, archive],
            new ConversionOptions { SchemaMappingMode = SchemaMappingMode.Preserve });

        Assert.Equal("sales_data", mapper.MapObject(sales).Schema);
        Assert.Equal("archive", mapper.MapObject(archive).Schema);
        Assert.NotEqual(mapper.MapObject(sales).Schema, mapper.MapObject(archive).Schema);
    }

    [Theory]
    [InlineData("freeze")]
    [InlineData("user")]
    [InlineData("order")]
    public void Mapper_QuotesRestrictedKeywordsAndReportsSafeStatus(string sourceName)
    {
        var source = Object("dbo", sourceName, 1);
        var mapper = CreateMapper([source]);

        Assert.Equal($"\"{sourceName}\"", mapper.MapObject(source).Name);
        var mapping = Assert.Single(mapper.Mappings, item => item.SourceObjectId == source.Id);
        Assert.True(mapping.IsReservedWord);
        Assert.True(mapping.RequiresQuoting);
        Assert.True(mapping.WasQuoted);
        Assert.Equal(IdentifierMappingStatus.ReservedWordSafelyQuoted, mapping.MappingStatus);
        Assert.Equal(IdentifierMappingSeverity.Information, mapping.Severity);
        Assert.False(mapping.ManualReviewRequired);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void Mapper_UsesKeywordRegistryForEverySupportedTargetVersion(int major)
    {
        var source = Object("dbo", "freeze", 1);
        var mapper = CreateMapper(
            [source],
            new ConversionOptions { TargetVersion = new PostgreSqlVersion(major) });

        Assert.Equal("\"freeze\"", mapper.MapObject(source).Name);
        Assert.True(Assert.Single(
            mapper.Mappings,
            item => item.SourceObjectId == source.Id).IsReservedWord);
    }

    [Fact]
    public void Mapper_EscapesSpacesAndEmbeddedDoubleQuotes()
    {
        var source = Object("dbo", "A \"quoted\" name", 1);
        var mapper = CreateMapper(
            [source],
            new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.PreserveQuoted });

        Assert.Equal("\"A \"\"quoted\"\" name\"", mapper.MapObject(source).Name);
    }

    [Fact]
    public void Mapper_SupportsEveryExplicitCaseAndQuotingPolicy()
    {
        var source = Object("dbo", "MixedCase", 1);

        Assert.Equal("mixedcase", CreateMapper(
            [source],
            new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.LowercaseUnquoted })
            .MapObject(source).Name);
        Assert.Equal("\"MixedCase\"", CreateMapper(
            [source],
            new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.PreserveQuoted })
            .MapObject(source).Name);
        Assert.Equal("mixedcase", CreateMapper(
            [source],
            new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.QuoteOnlyWhenRequired })
            .MapObject(source).Name);
        Assert.Equal("\"MixedCase\"", CreateMapper(
            [source],
            new ConversionOptions { IdentifierCaseMode = IdentifierCaseMode.QuoteEveryIdentifier })
            .MapObject(source).Name);
    }

    [Fact]
    public void Mapper_Accepts63Utf8BytesAndShortens64Bytes()
    {
        var exact = Object("dbo", new string('a', 63), 1);
        var tooLong = Object("dbo", new string('b', 64), 2);
        var mapper = CreateMapper([exact, tooLong]);

        Assert.Equal(63, Encoding.UTF8.GetByteCount(mapper.MapObject(exact).Name));
        Assert.True(Encoding.UTF8.GetByteCount(mapper.MapObject(tooLong).Name.Trim('"')) <= 63);
        Assert.Equal(
            IdentifierMappingStatus.AutomaticallyShortened,
            mapper.Mappings.Single(item => item.SourceObjectId == tooLong.Id).MappingStatus);
    }

    [Fact]
    public void Mapper_UsesUtf8BytesForUnicodeWithoutSplittingRunes()
    {
        var source = Object("dbo", string.Concat(Enumerable.Repeat("界", 22)), 1);
        Assert.True(source.SourceName.Length < 63);
        Assert.True(Encoding.UTF8.GetByteCount(source.SourceName) > 63);

        var target = CreateMapper([source]).MapObject(source).Name.Trim('"');

        Assert.True(Encoding.UTF8.GetByteCount(target) <= 63);
        Assert.DoesNotContain('\uFFFD', target);
    }

    [Fact]
    public void Mapper_ResolvesSchemaConsolidationCollisionsDeterministically()
    {
        var left = Object("sales", "Ledger", 1);
        var right = Object("archive", "ledger", 2);
        var options = new ConversionOptions
        {
            SchemaMappingMode = SchemaMappingMode.MapAllToOne,
            ConsolidatedSchema = "public"
        };
        var first = CreateMapper([left, right], options);
        var second = CreateMapper([left, right], options);

        Assert.NotEqual(first.MapObject(left).Name, first.MapObject(right).Name);
        Assert.Equal(first.MapObject(left), second.MapObject(left));
        Assert.Equal(first.MapObject(right), second.MapObject(right));
        Assert.Contains(first.Mappings, item =>
            item.MappingStatus == IdentifierMappingStatus.CollisionResolved);
    }

    [Fact]
    public void Mapper_UsesOwnerNamespaceForColumnsAndSchemaNamespaceForIndexes()
    {
        var left = Object("dbo", "left_table", 1);
        var right = Object("dbo", "right_table", 2);
        var mapper = CreateMapper([left, right]);

        Assert.Equal(
            mapper.MapChildIdentifier(left.Id, "column", "dbo", "user"),
            mapper.MapChildIdentifier(right.Id, "column", "dbo", "user"));
        var leftIndex = mapper.MapChildIdentifier(left.Id, "index", "dbo", "IX_Shared");
        var rightIndex = mapper.MapChildIdentifier(right.Id, "index", "dbo", "IX_Shared");
        Assert.NotEqual(leftIndex, rightIndex);
        Assert.Contains(mapper.Mappings, item =>
            item.ObjectType == "index" && item.CollisionResolved);
    }

    [Fact]
    public void FacetOwnedObject_HasOneCanonicalMappingInItsPostgreSqlNamespace()
    {
        var table = Object("dbo", "orders", 1);
        var indexId = InventoryObjectId.Create(
            "fixture", InventoryObjectType.Index, "dbo", "IX_Orders_Code", 2, table.Id);
        var indexObject = Object("dbo", "IX_Orders_Code", 2) with
        {
            Id = indexId,
            ObjectType = InventoryObjectType.Index,
            ParentObjectId = table.Id
        };
        var index = new IndexInventory(
            indexId,
            table.Id,
            2,
            indexObject.SourceName,
            IndexKind.NonClustered,
            false,
            false,
            false,
            false,
            false,
            null,
            0,
            "PRIMARY",
            [new IndexColumn(1, "Code", false, false)],
            [],
            ConversionClassification.Automatic);
        var inventory = TestInventory.CreateSnapshot([table, indexObject]) with
        {
            Schemas = [Schema("dbo")],
            Indexes = [index]
        };

        var mapper = new PostgreSqlIdentifierMappingService().CreateMapper(
            inventory,
            new ConversionOptions());
        var mapping = Assert.Single(mapper.Mappings, item =>
            item.SourceKey.ObjectId == indexId);

        Assert.Equal("index", mapping.ObjectType);
        Assert.Equal(mapping.TargetQualifiedName, mapper.MapObject(indexObject).QualifiedName);
    }

    [Fact]
    public void Mapper_ReusesColumnMappingForCreateForeignKeyCopyAndValidation()
    {
        var table = Object("dbo", "order", 1);
        var mapper = CreateMapper([table]);

        var create = mapper.MapChildIdentifier(table.Id, "column", "dbo", "freeze");
        var foreignKey = mapper.MapChildIdentifier(table.Id, "column", "dbo", "freeze");
        var copy = mapper.MapChildIdentifier(table.Id, "column", "dbo", "freeze");
        var validation = mapper.MapChildIdentifier(table.Id, "column", "dbo", "freeze");

        Assert.Equal("\"freeze\"", create);
        Assert.Equal(create, foreignKey);
        Assert.Equal(create, copy);
        Assert.Equal(create, validation);
        Assert.Single(mapper.Mappings, item =>
            item.SourceObjectId == table.Id && item.ObjectType == "column");
    }

    [Fact]
    public void Mapper_ShortensGeneratedTriggerFunctionDeterministically()
    {
        var table = Object("dbo", "audit_source", 1);
        var name = new string('x', 80) + "_trigger_function";

        var first = CreateMapper([table])
            .MapChildIdentifier(table.Id, "trigger_function", "dbo", name);
        var second = CreateMapper([table])
            .MapChildIdentifier(table.Id, "trigger_function", "dbo", name);

        Assert.Equal(first, second);
        Assert.True(Encoding.UTF8.GetByteCount(first.Trim('"')) <= 63);
    }

    private static InventoryObject Object(string schema, string name, int sqlId)
    {
        var id = InventoryObjectId.Create("fixture", InventoryObjectType.Table, schema, name, sqlId);
        return new InventoryObject(
            id, "fixture", schema, name, $"[{schema}].[{name}]", InventoryObjectType.Table, sqlId,
            null, null, null, false, true, SelectionReason.CompleteDatabase, 0, 0, [],
            ConversionClassification.Automatic, null, null, "hash", [], DiscoveryStatus.Discovered);
    }

    private static MigrationStudio.Application.Conversion.IIdentifierMapper CreateMapper(
        IReadOnlyList<InventoryObject> objects,
        ConversionOptions? options = null)
    {
        var inventory = TestInventory.CreateSnapshot(objects) with
        {
            Schemas = objects
                .Select(item => item.SourceSchema)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(Schema)
                .ToArray()
        };
        return new PostgreSqlIdentifierMappingService().CreateMapper(
            inventory,
            options ?? new ConversionOptions());
    }

    private static SchemaInventory Schema(string name)
    {
        var item = new InventoryObject(
            InventoryObjectId.Create("fixture", InventoryObjectType.Schema, string.Empty, name, null),
            "fixture", string.Empty, name, $"[{name}]", InventoryObjectType.Schema, null, null, null,
            null, false, true, SelectionReason.CompleteDatabase, 0, 0, [],
            ConversionClassification.Automatic, null, null, "hash", [], DiscoveryStatus.Discovered);
        return new SchemaInventory(item, "dbo", 1, false, true);
    }
}
