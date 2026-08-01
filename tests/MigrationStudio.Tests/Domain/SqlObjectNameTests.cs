using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Domain;

public sealed class SqlObjectNameTests
{
    [Theory]
    [InlineData("[sales].[Order]]Line]", "sales", "Order]Line")]
    [InlineData("dbo.Customer", "dbo", "Customer")]
    [InlineData("[Unqualified]", null, "Unqualified")]
    public void TryParse_ParsesSqlServerIdentifiers(string text, string? schema, string name)
    {
        Assert.True(SqlObjectName.TryParse(text, out var result));
        Assert.NotNull(result);
        Assert.Equal(schema, result.Schema);
        Assert.Equal(name, result.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dbo..Table")]
    [InlineData("[dbo].[Table")]
    [InlineData("server.database.schema.table")]
    public void TryParse_RejectsInvalidOrUnsupportedNames(string text) =>
        Assert.False(SqlObjectName.TryParse(text, out _));
}
