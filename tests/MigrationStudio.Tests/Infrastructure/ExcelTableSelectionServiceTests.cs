using System.IO;
using ClosedXML.Excel;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Excel;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class ExcelTableSelectionServiceTests
{
    [Fact]
    public async Task MatchAsync_TrimsDeduplicatesAndReportsAmbiguityAndUnmatched()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.xlsx");
        try
        {
            using (var workbook = new XLWorkbook())
            {
                var sheet = workbook.AddWorksheet("Selection");
                sheet.Cell(1, 1).Value = "Table";
                sheet.Cell(2, 1).Value = " [sales].[Orders] ";
                sheet.Cell(3, 1).Value = "[sales].[Orders]";
                sheet.Cell(4, 1).Value = "Customer";
                sheet.Cell(5, 1).Value = "Missing";
                sheet.Cell(6, 1).Value = string.Empty;
                workbook.SaveAs(path);
            }
            var objects = new[]
            {
                Object("sales", "Orders", 1),
                Object("sales", "Customer", 2),
                Object("crm", "Customer", 3)
            };

            var result = await new ClosedXmlTableSelectionService().MatchAsync(
                new ExcelTableSelectionOptions(path, "Selection", "Table"),
                objects,
                CancellationToken.None);

            Assert.Single(result.Matched);
            Assert.Single(result.Unmatched);
            Assert.Single(result.Ambiguous);
            Assert.Equal(1, result.DuplicatesRemoved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static InventoryObject Object(string schema, string name, int sqlId)
    {
        var id = InventoryObjectId.Create("db", InventoryObjectType.Table, schema, name, sqlId);
        return new InventoryObject(
            id, "db", schema, name, $"[{schema}].[{name}]", InventoryObjectType.Table, sqlId, null,
            null, null, false, false, SelectionReason.None, 0, 0, [], ConversionClassification.Automatic,
            null, null, "hash", [], DiscoveryStatus.Discovered);
    }
}
