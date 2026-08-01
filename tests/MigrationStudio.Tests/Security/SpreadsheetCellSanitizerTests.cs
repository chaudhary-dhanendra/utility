using MigrationStudio.Application.Security;

namespace MigrationStudio.Tests.Security;

public sealed class SpreadsheetCellSanitizerTests
{
    [Theory]
    [InlineData("=HYPERLINK(\"https://example.invalid\")")]
    [InlineData("+cmd|' /C calc'!A0")]
    [InlineData("-1+2")]
    [InlineData("@SUM(1,2)")]
    [InlineData("  =1+1")]
    [InlineData("\t=1+1")]
    public void Escape_PrefixesSpreadsheetFormulas(string value)
    {
        var escaped = SpreadsheetCellSanitizer.Escape(value);

        Assert.StartsWith("'", escaped, StringComparison.Ordinal);
        Assert.EndsWith(value, escaped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dbo.Customer")]
    [InlineData("42")]
    [InlineData("")]
    [InlineData(" normal text")]
    public void Escape_PreservesOrdinaryText(string value) =>
        Assert.Equal(value, SpreadsheetCellSanitizer.Escape(value));
}
