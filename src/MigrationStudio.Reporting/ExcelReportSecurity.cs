using ClosedXML.Excel;
using MigrationStudio.Application.Security;

namespace MigrationStudio.Reporting;

internal static class ExcelReportSecurity
{
    public static void Protect(XLWorkbook workbook)
    {
        foreach (var worksheet in workbook.Worksheets)
        {
            var usedRange = worksheet.RangeUsed();
            if (usedRange is null)
            {
                continue;
            }

            foreach (var cell in usedRange.CellsUsed())
            {
                if (cell.HasFormula || cell.DataType != XLDataType.Text)
                {
                    continue;
                }

                var current = cell.GetString();
                var escaped = SpreadsheetCellSanitizer.Escape(current);
                if (!string.Equals(current, escaped, StringComparison.Ordinal))
                {
                    cell.Value = escaped;
                }
            }
        }
    }
}
