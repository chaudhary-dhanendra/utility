using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Reporting;

public sealed class ValidationReportWriter : IValidationReportWriter
{
    public async Task<IReadOnlyList<string>> WriteAsync(
        ValidationRun run,
        string reportsDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(reportsDirectory);
        var stem = $"validation-{run.RunId:N}";
        var markdownPath = Path.Combine(reportsDirectory, $"{stem}.md");
        var workbookPath = Path.Combine(reportsDirectory, $"{stem}.xlsx");
        await File.WriteAllTextAsync(
            markdownPath, BuildMarkdown(run), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        WriteWorkbook(run, workbookPath);
        return [markdownPath, workbookPath];
    }

    private static string BuildMarkdown(ValidationRun run)
    {
        var text = new StringBuilder();
        text.AppendLine("# Post-migration validation report");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Run: `{run.RunId}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Target: `{run.TargetDatabaseIdentity}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Level: `{run.Configuration.Level}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Overall: **{run.Readiness.OverallStatus}**");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Weighted score: {(run.Readiness.WeightedScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? "not calculated")}");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Explanation: {run.Readiness.Explanation}");
        text.AppendLine();
        text.AppendLine("## Category scorecards");
        text.AppendLine();
        text.AppendLine("| Category | Status | Score | Weight | Explanation |");
        text.AppendLine("|---|---:|---:|---:|---|");
        foreach (var category in run.Readiness.Categories)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {category.Category} | {category.Status} | {category.Score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A"} | {category.Weight} | {Escape(category.Explanation)} |");
        }
        text.AppendLine();
        text.AppendLine("## Critical blockers");
        text.AppendLine();
        if (run.Readiness.CriticalBlockers.Count == 0)
        {
            text.AppendLine("None.");
        }
        else
        {
            foreach (var blocker in run.Readiness.CriticalBlockers)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"- `{blocker.RuleId}` {blocker.SourceObject}: {blocker.Summary}");
            }
        }
        text.AppendLine();
        text.AppendLine("## Findings");
        text.AppendLine();
        text.AppendLine("| Severity | Classification | Category | Object | Summary |");
        text.AppendLine("|---|---|---|---|---|");
        foreach (var finding in run.Findings)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {finding.Severity} | {finding.Classification} | {finding.Category} | {Escape(finding.SourceObject)} | {Escape(finding.Summary)} |");
        }
        text.AppendLine();
        text.AppendLine("> Checksums are SHA-256 digests of framed canonical values. They are evidence, not a mathematical proof of equality; collision risk is non-zero.");
        text.AppendLine("> No sensitive row values are included in this report. Programmable objects remain incomplete until administrator-approved semantic tests pass.");
        return text.ToString();
    }

    private static void WriteWorkbook(ValidationRun run, string path)
    {
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add("Readiness");
        summary.Cell(1, 1).Value = "Overall status";
        summary.Cell(1, 2).Value = run.Readiness.OverallStatus.ToString();
        summary.Cell(2, 1).Value = "Weighted score";
        summary.Cell(2, 2).Value = run.Readiness.WeightedScore;
        summary.Cell(3, 1).Value = "Explanation";
        summary.Cell(3, 2).Value = run.Readiness.Explanation;
        summary.Cell(5, 1).InsertTable(run.Readiness.Categories.Select(item => new
        {
            Category = item.Category.ToString(),
            Status = item.Status.ToString(),
            item.Score,
            item.Weight,
            item.ApplicableChecks,
            item.PassedChecks,
            item.WarningChecks,
            item.BlockerChecks,
            item.Explanation
        }));

        var findings = workbook.Worksheets.Add("Findings");
        findings.Cell(1, 1).InsertTable(run.Findings.Select(item => new
        {
            item.RuleId,
            Category = item.Category.ToString(),
            Severity = item.Severity.ToString(),
            Classification = item.Classification.ToString(),
            item.ObjectType,
            item.SourceObject,
            item.TargetObject,
            item.Summary,
            item.IsOverridden,
            item.OverrideReason
        }));

        var data = workbook.Worksheets.Add("Data reconciliation");
        data.Cell(1, 1).InsertTable(run.DataComparisons.Select(item => new
        {
            item.SourceTable,
            item.TargetTable,
            item.SourceRowCount,
            item.TargetRowCount,
            item.SourceChecksum,
            item.TargetChecksum,
            item.IsOrderedChecksum,
            Classification = item.Classification.ToString(),
            item.Detail
        }));

        var sequence = workbook.Worksheets.Add("Sequences");
        sequence.Cell(1, 1).InsertTable(run.SequenceResults.Select(item => new
        {
            item.SourceSequence,
            item.TargetSequence,
            item.CurrentValue,
            item.MaximumColumnValue,
            item.Increment,
            item.Minimum,
            item.Maximum,
            item.IsCycling,
            item.ExpectedNextValue,
            item.WouldGenerateDuplicate,
            Classification = item.Classification.ToString()
        }));
        foreach (var worksheet in workbook.Worksheets)
        {
            worksheet.SheetView.FreezeRows(1);
            worksheet.ColumnsUsed().AdjustToContents(1, 80);
        }
        ExcelReportSecurity.Protect(workbook);
        workbook.SaveAs(path);
    }

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).ReplaceLineEndings(" ");
}
