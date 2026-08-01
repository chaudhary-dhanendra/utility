using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Shapes;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using MigrationStudio.Domain.Reporting;
using PdfSharp.Fonts;

namespace MigrationStudio.Reporting;

public static class MigrationPdfReportWriter
{
    static MigrationPdfReportWriter()
    {
        if (OperatingSystem.IsWindows())
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        }
    }

    public static void Write(MigrationReportDocument report, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = CreateDocument(report);
        var renderer = new PdfDocumentRenderer
        {
            Document = document
        };
        renderer.RenderDocument();
        cancellationToken.ThrowIfCancellationRequested();
        renderer.PdfDocument.Save(path);
    }

    private static Document CreateDocument(MigrationReportDocument report)
    {
        var document = new Document
        {
            Info =
            {
                Title = report.Template.ReportTitle,
                Author = report.Template.PreparedBy,
                Subject = "SQL Server to PostgreSQL migration executive report"
            }
        };
        DefineStyles(document);
        AddCover(document, report);
        var section = document.AddSection();
        ConfigureSection(section, report);
        AddHeading(section, "Executive summary", 1);
        AddSummary(section, report);
        AddHeading(section, "Scope", 1);
        section.AddParagraph(
            $"Migration scope: {report.Summary.Scope}. Included schemas: " +
            $"{string.Join(", ", report.Summary.IncludedSchemas)}. " +
            $"{report.Summary.IncludedObjects:N0} objects are included and " +
            $"{report.Summary.ExcludedObjects:N0} are excluded.");
        AddHeading(section, "Architecture overview", 1);
        section.AddParagraph(
            "The migration pipeline uses an immutable SQL Server inventory, deterministic identifier and " +
            "datatype mappings, dependency-ordered PostgreSQL deployment, streamed data transfer, and an " +
            "independent post-migration validation phase. Detailed object evidence is supplied in the " +
            "companion Excel workbook and offline HTML report.");
        AddHeading(section, "Migration statistics", 1);
        AddMetrics(section, report);
        AddHeading(section, "Conversion status", 1);
        section.AddParagraph(
            $"Schema conversion: {report.Summary.SchemaConversionResult}. " +
            $"Unsupported items: {report.Summary.Unsupported:N0}. " +
            $"Manual-review items: {report.Summary.ManualReviews:N0}.");
        AddHeading(section, "Deployment status", 1);
        section.AddParagraph(
            $"Deployment result: {report.Summary.DeploymentResult}. " +
            $"Duration: {report.Summary.DeploymentDuration}.");
        AddHeading(section, "Validation status", 1);
        section.AddParagraph(
            $"Validation result: {report.Summary.ValidationResult}. " +
            $"Overall readiness: {report.Summary.OverallReadiness}. " +
            $"Duration: {report.Summary.ValidationDuration}.");
        AddReadinessTable(section, report);
        AddHeading(section, "Critical issues", 1);
        AddCriticalIssues(section, report);
        AddHeading(section, "Manual-review summary", 1);
        AddManualReview(section, report);
        AddHeading(section, "Unsupported-feature summary", 1);
        AddUnsupported(section, report);
        AddHeading(section, "Recommendations", 1);
        AddRecommendations(section, report);
        AddHeading(section, "Sign-off", 1);
        AddSignOff(section, report);
        return document;
    }

    private static void DefineStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 9.5;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        var heading1 = document.Styles[StyleNames.Heading1]!;
        heading1.Font.Name = "Arial";
        heading1.Font.Size = 16;
        heading1.Font.Bold = true;
        heading1.Font.Color = Color.Parse("#1F4E78");
        heading1.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
        heading1.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        var heading2 = document.Styles[StyleNames.Heading2]!;
        heading2.Font.Name = "Arial";
        heading2.Font.Size = 12;
        heading2.Font.Bold = true;
        heading2.Font.Color = Color.Parse("#2F6FED");
    }

    private static void AddCover(Document document, MigrationReportDocument report)
    {
        var section = document.AddSection();
        section.PageSetup.TopMargin = Unit.FromCentimeter(3);
        var classification = section.AddParagraph(report.Template.ClassificationMarking);
        classification.Format.Alignment = ParagraphAlignment.Right;
        classification.Format.Font.Bold = true;
        classification.Format.Font.Color = Color.Parse("#B54708");
        section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(3);
        if (!string.IsNullOrWhiteSpace(report.Template.LogoPath))
        {
            var logo = section.AddImage(report.Template.LogoPath);
            logo.LockAspectRatio = true;
            logo.Width = Unit.FromCentimeter(4.5);
            logo.Top = ShapePosition.Top;
            logo.Left = ShapePosition.Left;
        }
        var title = section.AddParagraph(report.Template.ReportTitle);
        title.Format.Font.Name = "Arial";
        title.Format.Font.Size = 28;
        title.Format.Font.Bold = true;
        title.Format.Font.Color = Color.Parse("#1F4E78");
        title.Format.SpaceAfter = Unit.FromCentimeter(1);
        var organization = section.AddParagraph(report.Template.OrganizationName);
        organization.Format.Font.Size = 16;
        organization.Format.Font.Color = Colors.DimGray;
        section.AddParagraph(report.Template.ProjectName).Format.Font.Size = 13;
        section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(2);
        section.AddParagraph($"Source: {report.Summary.Source.Server} / {report.Summary.Source.Database}");
        section.AddParagraph($"Target: {report.Summary.Target.Server} / {report.Summary.Target.Database}");
        section.AddParagraph($"Prepared by: {report.Template.PreparedBy}");
        section.AddParagraph($"Reviewed by: {report.Template.ReviewedBy}");
        section.AddParagraph(
            $"Generated: {report.Summary.GeneratedAt.ToString(report.Template.DateTimeFormat, CultureInfo.InvariantCulture)}");
    }

    private static void ConfigureSection(Section section, MigrationReportDocument report)
    {
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = Colors.Gray;
        footer.AddText(report.Template.Footer + " - ");
        footer.AddPageField();
        footer.AddText(" / ");
        footer.AddNumPagesField();
    }

    private static void AddSummary(Section section, MigrationReportDocument report)
    {
        var table = CreateTable(section, 5.5, 10.5);
        SummaryRow(table, "Overall readiness", report.Summary.OverallReadiness);
        SummaryRow(table, "Critical blockers", report.Summary.CriticalBlockers.ToString(CultureInfo.InvariantCulture));
        SummaryRow(table, "Schema conversion", report.Summary.SchemaConversionResult);
        SummaryRow(table, "Data migration", report.Summary.DataMigrationResult);
        SummaryRow(table, "Deployment", report.Summary.DeploymentResult);
        SummaryRow(table, "Validation", report.Summary.ValidationResult);
        SummaryRow(table, "Warnings", report.Summary.Warnings.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddMetrics(Section section, MigrationReportDocument report)
    {
        var table = CreateTable(section, 6.5, 4.5, 5);
        HeaderRow(table, "Metric", "Value", "Context");
        DataRow(table, "Objects", report.Summary.IncludedObjects.ToString("N0", CultureInfo.InvariantCulture), "Included scope");
        DataRow(table, "Rows read", report.Summary.RowsRead.ToString("N0", CultureInfo.InvariantCulture), "Data migration");
        DataRow(table, "Rows written", report.Summary.RowsWritten.ToString("N0", CultureInfo.InvariantCulture), "Data migration");
        DataRow(table, "Failed rows", report.Summary.FailedRows.ToString("N0", CultureInfo.InvariantCulture), "Redacted details");
        DataRow(table, "Throughput", report.Summary.RowsPerSecond.ToString("N1", CultureInfo.InvariantCulture), "Rows per second");
        DataRow(table, "Total duration", report.Summary.TotalDuration.ToString(), "Recorded phases");
    }

    private static void AddReadinessTable(Section section, MigrationReportDocument report)
    {
        if (report.Validation is null)
        {
            section.AddParagraph("No validation scorecards are available.");
            return;
        }
        var table = CreateTable(section, 6, 3, 3, 4);
        HeaderRow(table, "Category", "Status", "Score", "Blockers");
        foreach (var item in report.Validation.Readiness.Categories)
        {
            DataRow(
                table,
                item.Category.ToString(),
                item.Status.ToString(),
                item.Score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "N/A",
                item.BlockerChecks.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AddCriticalIssues(Section section, MigrationReportDocument report)
    {
        var blockers = report.Validation?.Readiness.CriticalBlockers ?? [];
        if (blockers.Count == 0)
        {
            section.AddParagraph("No critical validation blockers were recorded.");
            return;
        }
        foreach (var item in blockers.Take(20))
        {
            AddBullet(section, $"{item.SourceObject}: {item.Summary}");
        }
        if (blockers.Count > 20)
        {
            section.AddParagraph(
                $"{blockers.Count - 20:N0} additional blockers are listed in the workbook.");
        }
    }

    private static void AddManualReview(Section section, MigrationReportDocument report)
    {
        var grouped = report.ManualReviews.GroupBy(item => item.Status)
            .OrderBy(item => item.Key).ToArray();
        if (grouped.Length == 0)
        {
            section.AddParagraph("No workflow items were recorded.");
            return;
        }
        foreach (var group in grouped)
        {
            AddBullet(section, $"{group.Key}: {group.Count():N0}");
        }
    }

    private static void AddUnsupported(Section section, MigrationReportDocument report)
    {
        var unsupported = report.Conversion?.Artifacts.Where(item =>
                item.Classification == Domain.Inventory.ConversionClassification.Unsupported)
            .Take(20).ToArray() ?? [];
        if (unsupported.Length == 0)
        {
            section.AddParagraph("No unsupported conversion artifacts were recorded.");
            return;
        }
        foreach (var item in unsupported)
        {
            AddBullet(
                section,
                $"{item.TargetObjectId.QualifiedName}: {string.Join(", ", item.UnsupportedConstructs)}");
        }
        section.AddParagraph("See MigrationReport.xlsx for the complete unsupported-feature inventory.");
    }

    private static void AddRecommendations(Section section, MigrationReportDocument report)
    {
        if (report.Summary.CriticalBlockers > 0)
        {
            AddBullet(section, "Resolve all critical validation blockers before production cutover.");
        }
        if (report.Summary.ManualReviews > 0)
        {
            AddBullet(
                section,
                "Assign owners and record a resolution or accepted-risk decision for every open manual-review item.");
        }
        if (report.Summary.Unsupported > 0)
        {
            AddBullet(section, "Replace, redesign, or formally exclude unsupported SQL Server features.");
        }
        AddBullet(
            section,
            "Retain this PDF with the versioned JSON report, HTML report, workbook, and deployment/validation journals.");
    }

    private static void AddSignOff(Section section, MigrationReportDocument report)
    {
        section.AddParagraph();
        var table = CreateTable(section, 8, 8);
        DataRow(table, "Prepared by", report.Template.PreparedBy);
        DataRow(table, "Signature / date", string.Empty);
        DataRow(table, "Reviewed by", report.Template.ReviewedBy);
        DataRow(table, "Signature / date", string.Empty);
        DataRow(table, "Final disposition", "Approved / Approved with risk / Not approved");
    }

    private static Table CreateTable(Section section, params double[] widths)
    {
        var table = section.AddTable();
        table.Borders.Color = Color.Parse("#B8C3D1");
        table.Borders.Width = Unit.FromPoint(0.5);
        foreach (var width in widths)
        {
            table.AddColumn(Unit.FromCentimeter(width));
        }
        table.Format.Font.Size = 8.5;
        table.Rows.LeftIndent = Unit.Zero;
        return table;
    }

    private static void HeaderRow(Table table, params string[] values)
    {
        var row = table.AddRow();
        row.HeadingFormat = true;
        row.Shading.Color = Color.Parse("#1F4E78");
        row.Format.Font.Color = Colors.White;
        row.Format.Font.Bold = true;
        SetCells(row, values);
    }

    private static void SummaryRow(Table table, string label, string value)
    {
        var row = table.AddRow();
        row.Cells[0].Format.Font.Bold = true;
        SetCells(row, label, value);
    }

    private static void DataRow(Table table, params string[] values)
    {
        var row = table.AddRow();
        SetCells(row, values);
    }

    private static void SetCells(Row row, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            row.Cells[index].AddParagraph(values[index]);
            row.Cells[index].VerticalAlignment = VerticalAlignment.Center;
        }
    }

    private static void AddBullet(Section section, string text)
    {
        var paragraph = section.AddParagraph($"- {text}");
        paragraph.Format.LeftIndent = Unit.FromCentimeter(0.35);
        paragraph.Format.FirstLineIndent = Unit.FromCentimeter(-0.25);
    }

    private static void AddHeading(Section section, string text, int level) =>
        section.AddParagraph(text, level == 1 ? StyleNames.Heading1 : StyleNames.Heading2);
}
