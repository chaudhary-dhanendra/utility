using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;

namespace MigrationStudio.Reporting;

public sealed class DataMigrationReportWriter : IDataMigrationReportWriter
{
    public Task WriteAsync(
        DataMigrationResult result,
        string reportsDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(reportsDirectory);
        WriteWorkbook(result, Path.Combine(reportsDirectory, "Data_Migration_Report.xlsx"));
        return File.WriteAllTextAsync(
            Path.Combine(reportsDirectory, "Data_Migration_Report.html"),
            CreateHtml(result),
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static void WriteWorkbook(DataMigrationResult result, string path)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("Summary");
        summary.Cell(1, 1).Value = "Run ID";
        summary.Cell(1, 2).Value = result.RunId.ToString();
        summary.Cell(2, 1).Value = "State";
        summary.Cell(2, 2).Value = result.State.ToString();
        summary.Cell(3, 1).Value = "Started UTC";
        summary.Cell(3, 2).Value = result.StartedAt.UtcDateTime;
        summary.Cell(4, 1).Value = "Completed UTC";
        summary.Cell(4, 2).Value = result.CompletedAt.UtcDateTime;
        summary.Cell(5, 1).Value = "Rows written";
        summary.Cell(5, 2).Value = result.Tables.Sum(item => item.RowsWritten);
        summary.Cell(6, 1).Value = "Rows rejected";
        summary.Cell(6, 2).Value = result.Tables.Sum(item => item.RowsRejected);
        summary.Cell(7, 1).Value = "Effective table parallelism";
        summary.Cell(7, 2).Value = result.EffectiveTableParallelism;
        summary.Cell(8, 1).Value = "Peak reader connections";
        summary.Cell(8, 2).Value = result.PeakReaderConnections;
        summary.Cell(9, 1).Value = "Peak writer connections";
        summary.Cell(9, 2).Value = result.PeakWriterConnections;

        var tables = workbook.AddWorksheet("Tables");
        string[] headers =
        [
            "Table", "State", "Rows read", "Rows written", "Rows rejected", "Bytes",
            "Rows/sec", "Bytes/sec", "Read duration", "Write duration", "Validation duration",
            "Total duration", "Retries", "Failures", "Effective parallelism", "Peak memory", "Message"
        ];
        for (var index = 0; index < headers.Length; index++)
        {
            tables.Cell(1, index + 1).Value = headers[index];
        }

        for (var row = 0; row < result.Tables.Count; row++)
        {
            var item = result.Tables[row];
            object?[] values =
            [
                item.Table, item.State.ToString(), item.RowsRead, item.RowsWritten, item.RowsRejected,
                item.BytesTransferred, item.RowsPerSecond, item.BytesPerSecond, item.ReadDuration.ToString(),
                item.WriteDuration.ToString(), item.ValidationDuration.ToString(), item.TotalDuration.ToString(),
                item.RetryCount, item.FailureCount, item.EffectiveParallelism, item.PeakManagedMemoryBytes,
                item.Message
            ];
            for (var column = 0; column < values.Length; column++)
            {
                tables.Cell(row + 2, column + 1).Value =
                    XLCellValue.FromObject(values[column], CultureInfo.InvariantCulture);
            }
        }

        var failures = workbook.AddWorksheet("Failures (redacted)");
        string[] failureHeaders =
            ["Table", "Batch", "Row ordinal", "Safe key", "SQLSTATE", "Category", "Disposition", "Message"];
        for (var index = 0; index < failureHeaders.Length; index++)
        {
            failures.Cell(1, index + 1).Value = failureHeaders[index];
        }

        for (var row = 0; row < result.Failures.Count; row++)
        {
            var item = result.Failures[row];
            object?[] values =
            [
                item.Table, item.Batch, item.RowOrdinal, item.SafeSourceKey, item.SqlState,
                item.Category.ToString(), item.Disposition.ToString(), item.SanitizedMessage
            ];
            for (var column = 0; column < values.Length; column++)
            {
                failures.Cell(row + 2, column + 1).Value =
                    XLCellValue.FromObject(values[column], CultureInfo.InvariantCulture);
            }
        }

        var streaming = workbook.AddWorksheet("Streaming execution");
        string[] streamingHeaders =
        [
            "Stage", "Outcome", "Started UTC", "Completed UTC", "Elapsed ms", "Source table",
            "Target table", "Batch", "Rows read", "Rows written", "Reader", "Writer",
            "SQL Server query", "PostgreSQL query", "COPY SQL", "INSERT SQL", "SQLSTATE",
            "Failure component", "Failure reason", "Remediation", "Exception type",
            "Inner exception", "Stack trace"
        ];
        for (var index = 0; index < streamingHeaders.Length; index++)
        {
            streaming.Cell(1, index + 1).Value = streamingHeaders[index];
        }

        for (var row = 0; row < result.StreamingStages.Count; row++)
        {
            var item = result.StreamingStages[row];
            object?[] values =
            [
                $"{(int)item.Stage}: {item.Stage}", item.Outcome.ToString(), item.StartedAt.UtcDateTime,
                item.CompletedAt?.UtcDateTime, item.ElapsedMilliseconds, item.SourceTable, item.TargetTable,
                item.CurrentBatch, item.RowsRead, item.RowsWritten, item.CurrentReader, item.CurrentWriter,
                item.SqlServerQuery, item.PostgreSqlQuery, item.CopySql, item.InsertSql, item.SqlState,
                item.FailureComponent, item.FailureReason, item.Remediation, item.ExceptionType,
                item.InnerException, item.StackTrace
            ];
            for (var column = 0; column < values.Length; column++)
            {
                streaming.Cell(row + 2, column + 1).Value =
                    XLCellValue.FromObject(values[column], CultureInfo.InvariantCulture);
            }
        }

        var validation = workbook.AddWorksheet("Validation");
        string[] validationHeaders =
            ["Table", "Source rows", "Target rows", "Source checksum", "Target checksum", "Outcome", "Duration", "Message"];
        for (var index = 0; index < validationHeaders.Length; index++)
        {
            validation.Cell(1, index + 1).Value = validationHeaders[index];
        }

        for (var row = 0; row < result.Validations.Count; row++)
        {
            var item = result.Validations[row];
            object?[] values =
            [
                item.Table, item.SourceRowCount, item.TargetRowCount, item.SourceChecksum,
                item.TargetChecksum, item.Outcome.ToString(), item.Duration.ToString(), item.Message
            ];
            for (var column = 0; column < values.Length; column++)
            {
                validation.Cell(row + 2, column + 1).Value =
                    XLCellValue.FromObject(values[column], CultureInfo.InvariantCulture);
            }
        }

        foreach (var worksheet in workbook.Worksheets)
        {
            worksheet.SheetView.FreezeRows(1);
            // Fixed bounded widths keep report generation deterministic in
            // restricted service/CI accounts where Windows font directories
            // are not readable by the process.
            worksheet.ColumnsUsed().Width = 24;
        }

        ExcelReportSecurity.Protect(workbook);
        workbook.SaveAs(path);
    }

    private static string CreateHtml(DataMigrationResult result)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Data migration report</title>")
            .Append("<style>body{font-family:Segoe UI,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}")
            .Append("th,td{border:1px solid #bbb;padding:.4rem;text-align:left}th{background:#eee}</style></head><body>")
            .Append("<h1>Data migration report</h1><p>Run ")
            .Append(WebUtility.HtmlEncode(result.RunId.ToString()))
            .Append(" · ")
            .Append(WebUtility.HtmlEncode(result.State.ToString()))
            .Append("</p><table><thead><tr><th>Table</th><th>State</th><th>Read</th><th>Written</th>")
            .Append("<th>Rejected</th><th>Rows/sec</th><th>Duration</th><th>Validation</th></tr></thead><tbody>");
        foreach (var table in result.Tables)
        {
            var validation = result.Validations.FirstOrDefault(item =>
                item.Table.Equals(table.Table, StringComparison.OrdinalIgnoreCase));
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(table.Table))
                .Append("</td><td>").Append(table.State)
                .Append("</td><td>").Append(table.RowsRead.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(table.RowsWritten.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(table.RowsRejected.ToString("N0", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(table.RowsPerSecond.ToString("N1", CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(table.TotalDuration)
                .Append("</td><td>").Append(validation?.Outcome.ToString() ?? "NotRun")
                .Append("</td></tr>");
        }

        html.Append("</tbody></table><h2>Streaming execution</h2><table><thead><tr>")
            .Append("<th>Stage</th><th>Outcome</th><th>Table</th><th>Batch</th><th>Read</th>")
            .Append("<th>Written</th><th>Elapsed ms</th><th>SQLSTATE</th><th>Failure</th><th>Remediation</th>")
            .Append("</tr></thead><tbody>");
        foreach (var stage in result.StreamingStages)
        {
            html.Append("<tr><td>").Append((int)stage.Stage).Append(": ")
                .Append(WebUtility.HtmlEncode(stage.Stage.ToString()))
                .Append("</td><td>").Append(stage.Outcome)
                .Append("</td><td>").Append(WebUtility.HtmlEncode(stage.SourceTable))
                .Append("</td><td>").Append(stage.CurrentBatch)
                .Append("</td><td>").Append(stage.RowsRead)
                .Append("</td><td>").Append(stage.RowsWritten)
                .Append("</td><td>").Append(stage.ElapsedMilliseconds)
                .Append("</td><td>").Append(WebUtility.HtmlEncode(stage.SqlState))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(stage.FailureReason))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(stage.Remediation))
                .Append("</td></tr>");
        }

        return html.Append("</tbody></table><p>Failure details are redacted; row values and credentials are never included.</p></body></html>")
            .ToString();
    }
}
