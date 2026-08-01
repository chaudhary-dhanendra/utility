using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Reporting;

public sealed class DeploymentReportWriter : IDeploymentReportWriter
{
    public Task WriteAsync(
        DeploymentResult result,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outputDirectory);
        WriteWorkbook(result, Path.Combine(outputDirectory, "Deployment_Report.xlsx"));
        return File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "Deployment_Report.html"),
            CreateHtml(result),
            new UTF8Encoding(false),
            cancellationToken);
    }

    private static void WriteWorkbook(DeploymentResult result, string path)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.AddWorksheet("Summary");
        WriteRows(
            summary,
            ["Property", "Value"],
            [
                ["Deployment ID", result.DeploymentId.ToString()],
                ["Status", result.Status.ToString()],
                ["Target database", result.TargetDatabase],
                ["Started UTC", result.StartedAt.ToString("O", CultureInfo.InvariantCulture)],
                ["Completed UTC", result.CompletedAt.ToString("O", CultureInfo.InvariantCulture)],
                ["Succeeded", result.Objects.Count(item => item.Status == DeploymentObjectStatus.Succeeded)],
                ["Failed", result.Objects.Count(item => item.Status == DeploymentObjectStatus.Failed)],
                ["Skipped", result.Objects.Count(item =>
                    item.Status is DeploymentObjectStatus.Skipped or
                        DeploymentObjectStatus.SkippedEquivalent)],
                ["Data migration run", result.DataMigrationRunId?.ToString() ?? string.Empty]
            ]);

        var objects = workbook.AddWorksheet("Objects");
        WriteRows(
            objects,
            ["Phase", "Object", "Status", "Commit", "SQL hash", "Started", "Ended", "Retries", "Message"],
            result.Objects.Select(item => new object?[]
            {
                item.Phase.ToString(), item.TargetObject, item.Status.ToString(), item.CommitStatus.ToString(),
                item.ExecutedSqlHash, item.StartedAt?.ToString("O", CultureInfo.InvariantCulture),
                item.EndedAt?.ToString("O", CultureInfo.InvariantCulture), item.Retries.Count, item.Message
            }));

        var failures = workbook.AddWorksheet("Failures (redacted)");
        WriteRows(
            failures,
            ["Phase", "Object", "SQLSTATE", "Severity", "Hint", "Schema", "Table", "Column",
                "Constraint", "Datatype", "Retries"],
            result.Failures.Select(item => new object?[]
            {
                item.Phase.ToString(), item.TargetObject, item.SqlState, item.Severity, item.Hint,
                item.Schema, item.Table, item.Column, item.Constraint, item.DataType, item.RetryCount
            }));

        var findings = workbook.AddWorksheet("Assessment");
        WriteRows(
            findings,
            ["Severity", "Code", "Phase", "Message", "Overrideable"],
            result.Findings.Select(item => new object?[]
            {
                item.Severity.ToString(), item.Code, item.Phase?.ToString(), item.Message, item.CanOverride
            }));
        ExcelReportSecurity.Protect(workbook);
        workbook.SaveAs(path);
    }

    private static void WriteRows(
        IXLWorksheet worksheet,
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<object?>> rows)
    {
        for (var column = 0; column < headers.Count; column++)
        {
            worksheet.Cell(1, column + 1).Value = headers[column];
        }

        var rowNumber = 2;
        foreach (var row in rows)
        {
            for (var column = 0; column < row.Count; column++)
            {
                worksheet.Cell(rowNumber, column + 1).Value =
                    XLCellValue.FromObject(row[column], CultureInfo.InvariantCulture);
            }

            rowNumber++;
        }

        worksheet.SheetView.FreezeRows(1);
        worksheet.ColumnsUsed().AdjustToContents(1, Math.Min(rowNumber, 500));
    }

    private static string CreateHtml(DeploymentResult result)
    {
        var html = new StringBuilder(
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Deployment report</title>" +
            "<style>body{font-family:Segoe UI,sans-serif;margin:2rem}table{border-collapse:collapse;width:100%}" +
            "th,td{border:1px solid #bbb;padding:.4rem;text-align:left}th{background:#eee}</style></head><body>");
        html.Append("<h1>PostgreSQL deployment report</h1><p>")
            .Append(WebUtility.HtmlEncode(result.DeploymentId.ToString()))
            .Append(" · ").Append(WebUtility.HtmlEncode(result.Status.ToString()))
            .Append("</p><table><thead><tr><th>Phase</th><th>Object</th><th>Status</th><th>Commit</th>")
            .Append("<th>Retries</th><th>Message</th></tr></thead><tbody>");
        foreach (var item in result.Objects)
        {
            html.Append("<tr><td>").Append(item.Phase)
                .Append("</td><td>").Append(WebUtility.HtmlEncode(item.TargetObject))
                .Append("</td><td>").Append(item.Status)
                .Append("</td><td>").Append(item.CommitStatus)
                .Append("</td><td>").Append(item.Retries.Count.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>").Append(WebUtility.HtmlEncode(item.Message))
                .Append("</td></tr>");
        }

        return html.Append(
            "</tbody></table><p>SQL text, credentials, row values and PostgreSQL detail fields are excluded.</p></body></html>")
            .ToString();
    }
}
