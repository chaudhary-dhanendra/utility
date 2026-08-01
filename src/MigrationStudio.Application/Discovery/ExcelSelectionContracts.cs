using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Discovery;

public sealed record ExcelTableSelectionOptions(
    string WorkbookPath,
    string WorksheetName,
    string TableNameColumn);

public sealed record ExcelTableNameEntry(
    int RowNumber,
    string OriginalValue,
    SqlObjectName ParsedName);

public sealed record ExcelTableMatch(
    ExcelTableNameEntry Entry,
    InventoryObjectId TableObjectId,
    string QualifiedTableName);

public sealed record ExcelAmbiguousTableMatch(
    ExcelTableNameEntry Entry,
    IReadOnlyList<ExcelTableMatch> Candidates);

public sealed record ExcelTableSelectionResult(
    IReadOnlyList<ExcelTableMatch> Matched,
    IReadOnlyList<ExcelTableNameEntry> Unmatched,
    IReadOnlyList<ExcelAmbiguousTableMatch> Ambiguous,
    int BlankRowsRemoved,
    int DuplicatesRemoved);

public sealed record ExcelSelectionProgress(
    string Stage,
    int RowsProcessed,
    int TotalRows,
    int Matched,
    int Unmatched,
    int Ambiguous);

public interface IExcelTableSelectionService
{
    Task<IReadOnlyList<string>> GetWorksheetNamesAsync(
        string workbookPath,
        CancellationToken cancellationToken);

    Task<ExcelTableSelectionResult> MatchAsync(
        ExcelTableSelectionOptions options,
        IReadOnlyList<InventoryObject> inventoryObjects,
        CancellationToken cancellationToken);

    Task<ExcelTableSelectionResult> MatchAsync(
        ExcelTableSelectionOptions options,
        IReadOnlyList<InventoryObject> inventoryObjects,
        IProgress<ExcelSelectionProgress>? progress,
        CancellationToken cancellationToken);

    Task ExportIssuesAsync(
        ExcelTableSelectionResult result,
        string outputPath,
        CancellationToken cancellationToken);
}
