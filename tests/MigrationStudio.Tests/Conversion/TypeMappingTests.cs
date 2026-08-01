using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;

namespace MigrationStudio.Tests.Conversion;

public sealed class TypeMappingTests
{
    [Theory]
    [InlineData("bit", 1, 0, 0, "boolean")]
    [InlineData("tinyint", 1, 0, 0, "smallint")]
    [InlineData("smallint", 2, 0, 0, "smallint")]
    [InlineData("int", 4, 0, 0, "integer")]
    [InlineData("bigint", 8, 0, 0, "bigint")]
    [InlineData("decimal", 17, 18, 4, "numeric(18,4)")]
    [InlineData("money", 8, 0, 0, "numeric(19,4)")]
    [InlineData("smallmoney", 4, 0, 0, "numeric(10,4)")]
    [InlineData("float", 8, 53, 0, "double precision")]
    [InlineData("float", 4, 24, 0, "real")]
    [InlineData("real", 4, 0, 0, "real")]
    [InlineData("date", 3, 0, 0, "date")]
    [InlineData("time", 5, 0, 3, "time(3)")]
    [InlineData("datetime", 8, 0, 3, "timestamp without time zone")]
    [InlineData("datetime2", 8, 0, 6, "timestamp(6) without time zone")]
    [InlineData("datetimeoffset", 10, 0, 6, "timestamp(6) with time zone")]
    [InlineData("char", 12, 0, 0, "char(12)")]
    [InlineData("varchar", -1, 0, 0, "text")]
    [InlineData("nvarchar", 100, 0, 0, "varchar(50)")]
    [InlineData("ntext", -1, 0, 0, "text")]
    [InlineData("varbinary", -1, 0, 0, "bytea")]
    [InlineData("image", -1, 0, 0, "bytea")]
    [InlineData("uniqueidentifier", 16, 0, 0, "uuid")]
    [InlineData("xml", -1, 0, 0, "xml")]
    [InlineData("rowversion", 8, 0, 0, "bytea")]
    public void MapsBuiltInTypes(
        string source,
        short length,
        byte precision,
        byte scale,
        string expected)
    {
        var result = new PostgreSqlTypeMappingRegistry().Map(
            source, length, precision, scale, new ConversionOptions());
        Assert.Equal(expected, result.TargetType);
    }

    [Fact]
    public void Geography_RequiresPostGisOptIn()
    {
        var registry = new PostgreSqlTypeMappingRegistry();
        Assert.Equal(
            ConversionClassification.ManualConversion,
            registry.Map("geography", -1, 0, 0, new ConversionOptions()).Classification);
        var enabled = registry.Map("geography", -1, 0, 0, new ConversionOptions { EnablePostGis = true });
        Assert.Equal("geography", enabled.TargetType);
        Assert.Contains("postgis", enabled.RequiredExtensions);
    }
}
