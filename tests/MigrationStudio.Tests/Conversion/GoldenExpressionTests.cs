using System.IO;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Conversion;

namespace MigrationStudio.Tests.Conversion;

public sealed class GoldenExpressionTests
{
    [Fact]
    public void ComputedFullName_MatchesReviewedGoldenFile()
    {
        var source = ReadFixture("sqlserver", "source", "computed_full_name.sql").Trim();
        var expected = ReadFixture("postgresql", "expected", "computed_full_name.sql").Trim();
        var id = InventoryObjectId.Create("fixture", InventoryObjectType.Table, "dbo", "person", 1);
        var result = new StructuredSqlExpressionTranslator().Translate(
            source,
            new ExpressionTranslationContext(
                id,
                new Dictionary<string, string>
                {
                    ["FirstName"] = "nvarchar",
                    ["LastName"] = "nvarchar"
                },
                new ConversionOptions(),
                true));

        Assert.Equal(expected, result.Sql);
    }

    private static string ReadFixture(params string[] parts) =>
        File.ReadAllText(Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]));
}
