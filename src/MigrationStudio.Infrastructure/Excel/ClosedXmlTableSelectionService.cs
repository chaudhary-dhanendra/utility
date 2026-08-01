using ClosedXML.Excel;
using ClosedXML.Graphics;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Excel;

public sealed class ClosedXmlTableSelectionService : IExcelTableSelectionService
{
    static ClosedXmlTableSelectionService()
    {
        using var font = OpenFallbackFont();
        LoadOptions.DefaultGraphicEngine = DefaultGraphicEngine.CreateOnlyWithFonts(font);
    }

    public Task<IReadOnlyList<string>> GetWorksheetNamesAsync(
        string workbookPath,
        CancellationToken cancellationToken)
    {
        ValidateWorkbookPath(workbookPath);
        return Task.Run<IReadOnlyList<string>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var workbook = OpenWorkbook(workbookPath);
                return workbook.Worksheets.Select(worksheet => worksheet.Name).ToArray();
            },
            cancellationToken);
    }

    public Task<ExcelTableSelectionResult> MatchAsync(
        ExcelTableSelectionOptions options,
        IReadOnlyList<InventoryObject> inventoryObjects,
        CancellationToken cancellationToken) =>
        MatchAsync(options, inventoryObjects, null, cancellationToken);

    public Task<ExcelTableSelectionResult> MatchAsync(
        ExcelTableSelectionOptions options,
        IReadOnlyList<InventoryObject> inventoryObjects,
        IProgress<ExcelSelectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inventoryObjects);
        ValidateWorkbookPath(options.WorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorksheetName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.TableNameColumn);

        return Task.Run(
            () => MatchCore(options, inventoryObjects, progress, cancellationToken),
            cancellationToken);
    }

    public Task ExportIssuesAsync(
        ExcelTableSelectionResult result,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var workbook = CreateWorkbook();
                var unmatched = workbook.AddWorksheet("Unmatched");
                unmatched.Cell(1, 1).Value = "Row";
                unmatched.Cell(1, 2).Value = "Original value";
                unmatched.Cell(1, 3).Value = "Parsed name";

                var row = 2;
                foreach (var entry in result.Unmatched)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    unmatched.Cell(row, 1).Value = entry.RowNumber;
                    unmatched.Cell(row, 2).Value = entry.OriginalValue;
                    unmatched.Cell(row, 3).Value = entry.ParsedName.QualifiedName;
                    row++;
                }

                var ambiguous = workbook.AddWorksheet("Ambiguous");
                ambiguous.Cell(1, 1).Value = "Row";
                ambiguous.Cell(1, 2).Value = "Original value";
                ambiguous.Cell(1, 3).Value = "Candidate";
                row = 2;
                foreach (var issue in result.Ambiguous)
                {
                    foreach (var candidate in issue.Candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ambiguous.Cell(row, 1).Value = issue.Entry.RowNumber;
                        ambiguous.Cell(row, 2).Value = issue.Entry.OriginalValue;
                        ambiguous.Cell(row, 3).Value = candidate.QualifiedTableName;
                        row++;
                    }
                }

                unmatched.Columns().AdjustToContents();
                ambiguous.Columns().AdjustToContents();
                workbook.SaveAs(outputPath);
            },
            cancellationToken);
    }

    private static ExcelTableSelectionResult MatchCore(
        ExcelTableSelectionOptions options,
        IReadOnlyList<InventoryObject> inventoryObjects,
        IProgress<ExcelSelectionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var workbook = OpenWorkbook(options.WorkbookPath);
        var worksheet = workbook.Worksheets.FirstOrDefault(
            item => string.Equals(item.Name, options.WorksheetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Worksheet '{options.WorksheetName}' was not found.", nameof(options));
        var usedRange = worksheet.RangeUsed();
        if (usedRange is null)
        {
            return new ExcelTableSelectionResult([], [], [], 0, 0);
        }

        var columnNumber = ResolveColumnNumber(usedRange, options.TableNameColumn);
        var entries = new List<ExcelTableNameEntry>();
        var blankRows = 0;
        var duplicates = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstDataRow = usedRange.RangeAddress.FirstAddress.RowNumber + 1;
        var lastDataRow = usedRange.RangeAddress.LastAddress.RowNumber;
        var totalRows = Math.Max(0, lastDataRow - firstDataRow + 1);

        for (var rowNumber = firstDataRow;
             rowNumber <= lastDataRow;
             rowNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var original = worksheet.Cell(rowNumber, columnNumber).GetFormattedString().Trim();
            if (original.Length == 0)
            {
                blankRows++;
                continue;
            }

            if (!SqlObjectName.TryParse(original, out var parsed) || parsed is null)
            {
                entries.Add(new ExcelTableNameEntry(rowNumber, original, new SqlObjectName(null, original)));
                continue;
            }

            var key = $"{parsed.Schema ?? string.Empty}\u001f{parsed.Name}";
            if (!seen.Add(key))
            {
                duplicates++;
                continue;
            }

            entries.Add(new ExcelTableNameEntry(rowNumber, original, parsed));
            if ((rowNumber - firstDataRow + 1) % 250 == 0)
            {
                progress?.Report(new ExcelSelectionProgress(
                    "Reading workbook", rowNumber - firstDataRow + 1, totalRows, 0, 0, 0));
            }
        }

        var tables = inventoryObjects
            .Where(item => item.ObjectType is InventoryObjectType.Table or InventoryObjectType.ExternalTable)
            .ToArray();
        var byQualifiedName = tables
            .GroupBy(
                item => QualifiedKey(item.SourceSchema, item.SourceName),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var byName = tables
            .GroupBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matched = new List<ExcelTableMatch>();
        var unmatched = new List<ExcelTableNameEntry>();
        var ambiguous = new List<ExcelAmbiguousTableMatch>();

        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[index];
            var matchingTables = entry.ParsedName.Schema is null
                ? byName.GetValueOrDefault(entry.ParsedName.Name, [])
                : byQualifiedName.GetValueOrDefault(
                    QualifiedKey(entry.ParsedName.Schema, entry.ParsedName.Name), []);
            var candidates = matchingTables
                .Select(table => new ExcelTableMatch(entry, table.Id, table.QualifiedSourceName))
                .ToArray();

            switch (candidates.Length)
            {
                case 0:
                    unmatched.Add(entry);
                    break;
                case 1:
                    matched.Add(candidates[0]);
                    break;
                default:
                    ambiguous.Add(new ExcelAmbiguousTableMatch(entry, candidates));
                    break;
            }

            if ((index + 1) % 250 == 0 || index + 1 == entries.Count)
            {
                progress?.Report(new ExcelSelectionProgress(
                    "Matching tables", index + 1, entries.Count,
                    matched.Count, unmatched.Count, ambiguous.Count));
            }
        }

        return new ExcelTableSelectionResult(matched, unmatched, ambiguous, blankRows, duplicates);
    }

    private static string QualifiedKey(string schema, string name) => $"{schema}\u001f{name}";

    private static int ResolveColumnNumber(IXLRange usedRange, string column)
    {
        if (int.TryParse(column, out var columnNumber) && columnNumber > 0)
        {
            return columnNumber;
        }

        if (column.All(char.IsLetter) && column.Length <= 3)
        {
            return XLHelper.GetColumnNumberFromLetter(column.ToUpperInvariant());
        }

        var headerRow = usedRange.FirstRow();
        var matchingHeaders = headerRow.CellsUsed()
            .Where(cell => string.Equals(cell.GetFormattedString().Trim(), column.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matchingHeaders.Length switch
        {
            1 => matchingHeaders[0].Address.ColumnNumber,
            0 => throw new ArgumentException($"Column '{column}' was not found in the header row.", nameof(column)),
            _ => throw new ArgumentException($"Column header '{column}' is ambiguous.", nameof(column))
        };
    }

    private static void ValidateWorkbookPath(string workbookPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);
        if (!string.Equals(Path.GetExtension(workbookPath), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("Only .xlsx workbooks are supported.");
        }

        if (!File.Exists(workbookPath))
        {
            throw new FileNotFoundException("The workbook was not found.", workbookPath);
        }
    }

    private static XLWorkbook OpenWorkbook(string path)
        => new(path);

    private static XLWorkbook CreateWorkbook()
        => new();

    private static FileStream OpenFallbackFont()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var fontPath = Path.Combine(windowsDirectory, "Fonts", "segoeui.ttf");
        return File.OpenRead(fontPath);
    }
}
