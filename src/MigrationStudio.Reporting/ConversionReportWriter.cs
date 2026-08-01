using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Security;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Reporting;

public sealed class ConversionReportWriter : IConversionReportWriter
{
    private const int StreamingExcelThreshold = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJsonOptions = new();

    public async Task WriteAsync(
        ConversionRun run,
        string reportsDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        Directory.CreateDirectory(reportsDirectory);
        await Task.Run(
            () => WriteExcelReports(run, reportsDirectory, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        await WriteCsvAsync(run, reportsDirectory, cancellationToken).ConfigureAwait(false);
        await WriteHtmlAsync(run, reportsDirectory, cancellationToken).ConfigureAwait(false);
        await WriteJsonAsync(run, reportsDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteExcelReports(
        ConversionRun run,
        string directory,
        CancellationToken cancellationToken)
    {
        var streamIdentifiers = run.IdentifierMappings.Count >= StreamingExcelThreshold;
        var conversionWorkbookPath = Path.Combine(directory, "Conversion_Report.xlsx");
        using var workbook = new XLWorkbook();
        AddSummary(workbook, run, cancellationToken);
        AddObjects(workbook, run, cancellationToken);
        if (!streamIdentifiers)
        {
            AddIdentifiers(workbook, run, cancellationToken);
        }
        AddTypes(workbook, run, cancellationToken);
        AddComputed(workbook, "Computed Review", run, cancellationToken);
        AddProgrammable(workbook, run, cancellationToken);
        AddFindings(workbook, "Unsupported Features", run, item => item.Severity >= FindingSeverity.Warning, cancellationToken);
        AddFindings(workbook, "Dependency Cycles", run, item => item.Code.Contains("CYCLE", StringComparison.OrdinalIgnoreCase), cancellationToken);
        AddFindings(workbook, "External Dependencies", run, item => item.Code.Contains("DEPENDENCY", StringComparison.OrdinalIgnoreCase), cancellationToken);
        AddExtensions(workbook, run, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ExcelReportSecurity.Protect(workbook);
        workbook.SaveAs(conversionWorkbookPath);

        if (streamIdentifiers)
        {
            AppendStreamingIdentifierWorksheet(conversionWorkbookPath, run, cancellationToken);
            WriteStreamingIdentifierWorkbook(
                Path.Combine(directory, "Identifier_Mapping.xlsx"),
                run,
                cancellationToken);
        }
        else
        {
            using var identifiers = new XLWorkbook();
            AddIdentifiers(identifiers, run, cancellationToken);
            ExcelReportSecurity.Protect(identifiers);
            identifiers.SaveAs(Path.Combine(directory, "Identifier_Mapping.xlsx"));
        }

        using var computed = new XLWorkbook();
        AddComputed(computed, "Computed Columns", run, cancellationToken);
        ExcelReportSecurity.Protect(computed);
        computed.SaveAs(Path.Combine(directory, "ComputedColumn_ManualReview.xlsx"));
    }

    private static void WriteStreamingIdentifierWorkbook(
        string path,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        WriteStreamingIdentifierWorksheet(worksheetPart, run, cancellationToken);
        workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = 1,
            Name = "Identifier Mapping"
        });
        workbookPart.Workbook.Save();
    }

    private static void AppendStreamingIdentifierWorksheet(
        string path,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        using var document = SpreadsheetDocument.Open(path, true);
        var workbookPart = document.WorkbookPart ??
            throw new InvalidOperationException("Conversion workbook has no workbook part.");
        var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        WriteStreamingIdentifierWorksheet(worksheetPart, run, cancellationToken);
        var sheets = workbookPart.Workbook.GetFirstChild<Sheets>() ??
            workbookPart.Workbook.AppendChild(new Sheets());
        var nextId = sheets.Elements<Sheet>()
            .Select(item => item.SheetId?.Value ?? 0U)
            .DefaultIfEmpty()
            .Max() + 1;
        sheets.Append(new Sheet
        {
            Id = workbookPart.GetIdOfPart(worksheetPart),
            SheetId = nextId,
            Name = "Identifier Mapping"
        });
        workbookPart.Workbook.Save();
    }

    private static void WriteStreamingIdentifierWorksheet(
        WorksheetPart worksheetPart,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var headers = IdentifierHeaders;
        using var writer = OpenXmlWriter.Create(worksheetPart);
        writer.WriteStartElement(new Worksheet());
        writer.WriteStartElement(new SheetViews());
        writer.WriteStartElement(new SheetView { WorkbookViewId = 0U });
        writer.WriteElement(new Pane
        {
            VerticalSplit = 1D,
            TopLeftCell = "A2",
            ActivePane = PaneValues.BottomLeft,
            State = PaneStateValues.Frozen
        });
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement(new SheetData());
        WriteStreamingRow(writer, headers);
        var processed = 0;
        foreach (var item in run.IdentifierMappings)
        {
            if ((processed++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            WriteStreamingRow(writer, IdentifierValues(item));
        }
        writer.WriteEndElement();
        writer.WriteElement(new AutoFilter
        {
            Reference = $"A1:AI{run.IdentifierMappings.Count + 1}"
        });
        writer.WriteEndElement();
    }

    private static void WriteStreamingRow(OpenXmlWriter writer, IEnumerable<string> values)
    {
        writer.WriteStartElement(new Row());
        foreach (var value in values)
        {
            writer.WriteStartElement(new Cell { DataType = CellValues.InlineString });
            writer.WriteElement(new InlineString(new Text(value ?? string.Empty)
            {
                Space = SpaceProcessingModeValues.Preserve
            }));
            writer.WriteEndElement();
        }
        writer.WriteEndElement();
    }

    private static void AddSummary(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Conversion Summary");
        var mapping = run.IdentifierMappingSummary;
        var rows = new[]
        {
            ("Run", run.RunId.ToString("N")),
            ("Source database", run.SourceDatabase),
            ("Target PostgreSQL", run.TargetVersion.Major.ToString(CultureInfo.InvariantCulture)),
            ("Generated", run.GeneratedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("Artifacts", run.Artifacts.Count.ToString(CultureInfo.InvariantCulture)),
            ("Automatic", run.Artifacts.Count(item => item.Classification == ConversionClassification.Automatic).ToString(CultureInfo.InvariantCulture)),
            ("With warnings", run.Artifacts.Count(item => item.Classification == ConversionClassification.AutomaticWithWarning).ToString(CultureInfo.InvariantCulture)),
            ("Manual review", run.Artifacts.Count(item => item.RequiresManualReview).ToString(CultureInfo.InvariantCulture)),
            ("Unsupported", run.Artifacts.Count(item => item.Classification == ConversionClassification.Unsupported).ToString(CultureInfo.InvariantCulture)),
            ("Findings", run.Findings.Count.ToString(CultureInfo.InvariantCulture)),
            ("Included identifiers", mapping.TotalIncludedObjects.ToString(CultureInfo.InvariantCulture)),
            ("Identifiers mapped", mapping.AutomaticallyMapped.ToString(CultureInfo.InvariantCulture)),
            ("Identifiers renamed", mapping.Renamed.ToString(CultureInfo.InvariantCulture)),
            ("Identifiers shortened", mapping.Truncated.ToString(CultureInfo.InvariantCulture)),
            ("Reserved words adjusted", mapping.ReservedWordsAdjusted.ToString(CultureInfo.InvariantCulture)),
            ("Collisions resolved", mapping.CollisionsResolved.ToString(CultureInfo.InvariantCulture)),
            ("Mappings auto-recovered", mapping.AutoRecovered.ToString(CultureInfo.InvariantCulture)),
            ("Identifiers unresolved", mapping.Unresolved.ToString(CultureInfo.InvariantCulture))
        };
        sheet.Cell(1, 1).Value = "Metric";
        sheet.Cell(1, 2).Value = "Value";
        for (var index = 0; index < rows.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(index + 2, 1).Value = rows[index].Item1;
            sheet.Cell(index + 2, 2).Value = rows[index].Item2;
        }
        StyleHeader(sheet.Range(1, 1, 1, 2));
        AddIdentifierLegend(sheet, rows.Length + 4, 1);
    }

    private static void AddObjects(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Object Inventory");
        WriteHeaders(sheet, "Source ID", "Target", "Phase", "Classification", "Confidence", "Rule", "Manual review", "Validation", "Hash");
        var row = 2;
        foreach (var artifact in run.Artifacts)
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(row, 1).Value = artifact.SourceObjectId.ToString();
            sheet.Cell(row, 2).Value = artifact.TargetObjectId.QualifiedName;
            sheet.Cell(row, 3).Value = artifact.DeploymentPhase.ToString();
            sheet.Cell(row, 4).Value = artifact.Classification.ToString();
            sheet.Cell(row, 5).Value = artifact.Confidence;
            sheet.Cell(row, 6).Value = artifact.RuleId;
            sheet.Cell(row, 7).Value = artifact.RequiresManualReview;
            sheet.Cell(row, 8).Value = artifact.Validation.IsStructurallyValid ? "Offline valid" : "Failed";
            sheet.Cell(row, 9).Value = artifact.ContentHash;
            row++;
        }
    }

    private static readonly string[] IdentifierHeaders =
    [
        "Object type", "Parent object", "Source database", "Source schema", "Source name",
        "Source qualified name", "Target schema", "Target name", "Target qualified name",
        "Source UTF-8 byte length", "Target UTF-8 byte length", "Source character length",
        "Target character length", "Is reserved word", "Requires quoting", "Was quoted",
        "Was case-normalized", "Was shortened", "Collision detected", "Collision resolved",
        "Mapping status", "Transformation reason", "Hash suffix", "Severity",
        "Manual review required", "Source object ID", "Target parent object",
        "Mapping action", "Invalid-character replacement", "Auto-recovered",
        "Included in scope", "Conversion classification", "Collision group",
        "Collision resolution", "Warnings"
    ];

    private static string[] IdentifierValues(IdentifierMappingEntry item) =>
    [
        item.ObjectType,
        item.ParentObject,
        item.SourceDatabase,
        item.SourceSchema,
        item.SourceName,
        item.SourceQualifiedName,
        item.TargetSchema,
        item.TargetName,
        item.TargetQualifiedName,
        item.OriginalUtf8ByteLength.ToString(CultureInfo.InvariantCulture),
        item.TargetUtf8ByteLength.ToString(CultureInfo.InvariantCulture),
        item.SourceCharacterLength.ToString(CultureInfo.InvariantCulture),
        item.TargetCharacterLength.ToString(CultureInfo.InvariantCulture),
        item.IsReservedWord.ToString(CultureInfo.InvariantCulture),
        item.RequiresQuoting.ToString(CultureInfo.InvariantCulture),
        item.WasQuoted.ToString(CultureInfo.InvariantCulture),
        item.WasCaseNormalized.ToString(CultureInfo.InvariantCulture),
        item.WasShortened.ToString(CultureInfo.InvariantCulture),
        item.HadCollision.ToString(CultureInfo.InvariantCulture),
        item.CollisionResolved.ToString(CultureInfo.InvariantCulture),
        StatusText(item),
        item.TransformationReason,
        item.HashSuffix ?? string.Empty,
        item.Severity.ToString(),
        item.ManualReviewRequired.ToString(CultureInfo.InvariantCulture),
        item.SourceKey.ObjectId?.ToString() ?? string.Empty,
        item.TargetParentObject,
        item.MappingAction.ToString(),
        item.InvalidCharacterReplacement.ToString(CultureInfo.InvariantCulture),
        item.AutoRecovered.ToString(CultureInfo.InvariantCulture),
        item.IncludedInScope.ToString(CultureInfo.InvariantCulture),
        item.ConversionClassification.ToString(),
        item.CollisionGroup,
        item.CollisionResolution,
        string.Join("; ", item.Warnings)
    ];

    private static void AddIdentifiers(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Identifier Mapping");
        WriteHeaders(sheet, IdentifierHeaders);
        var row = 2;
        foreach (var item in run.IdentifierMappings)
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(row, 1).Value = item.ObjectType;
            sheet.Cell(row, 2).Value = item.ParentObject;
            sheet.Cell(row, 3).Value = item.SourceDatabase;
            sheet.Cell(row, 4).Value = item.SourceSchema;
            sheet.Cell(row, 5).Value = item.SourceName;
            sheet.Cell(row, 6).Value = item.SourceQualifiedName;
            sheet.Cell(row, 7).Value = item.TargetSchema;
            sheet.Cell(row, 8).Value = item.TargetName;
            sheet.Cell(row, 9).Value = item.TargetQualifiedName;
            sheet.Cell(row, 10).Value = item.OriginalUtf8ByteLength;
            sheet.Cell(row, 11).Value = item.TargetUtf8ByteLength;
            sheet.Cell(row, 12).Value = item.SourceCharacterLength;
            sheet.Cell(row, 13).Value = item.TargetCharacterLength;
            sheet.Cell(row, 14).Value = item.IsReservedWord;
            sheet.Cell(row, 15).Value = item.RequiresQuoting;
            sheet.Cell(row, 16).Value = item.WasQuoted;
            sheet.Cell(row, 17).Value = item.WasCaseNormalized;
            sheet.Cell(row, 18).Value = item.WasShortened;
            sheet.Cell(row, 19).Value = item.HadCollision;
            sheet.Cell(row, 20).Value = item.CollisionResolved;
            sheet.Cell(row, 21).Value = StatusText(item);
            sheet.Cell(row, 22).Value = item.TransformationReason;
            sheet.Cell(row, 23).Value = item.HashSuffix ?? string.Empty;
            sheet.Cell(row, 24).Value = item.Severity.ToString();
            sheet.Cell(row, 25).Value = item.ManualReviewRequired;
            sheet.Cell(row, 26).Value = item.SourceKey.ObjectId?.ToString() ?? string.Empty;
            sheet.Cell(row, 27).Value = item.TargetParentObject;
            sheet.Cell(row, 28).Value = item.MappingAction.ToString();
            sheet.Cell(row, 29).Value = item.InvalidCharacterReplacement;
            sheet.Cell(row, 30).Value = item.AutoRecovered;
            sheet.Cell(row, 31).Value = item.IncludedInScope;
            sheet.Cell(row, 32).Value = item.ConversionClassification.ToString();
            sheet.Cell(row, 33).Value = item.CollisionGroup;
            sheet.Cell(row, 34).Value = item.CollisionResolution;
            sheet.Cell(row, 35).Value = string.Join("; ", item.Warnings);
            var fill = IdentifierStatusColor(item);
            sheet.Range(row, 1, row, 35).Style.Fill.BackgroundColor = fill;
            sheet.Range(row, 1, row, 35).Style.Font.FontColor =
                item.IsBlocking ? XLColor.White : XLColor.Black;
            row++;
        }
        AddIdentifierLegend(sheet, row + 2, 1);
    }

    private static void AddTypes(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Datatype Mapping");
        WriteHeaders(sheet, "Source type", "Target type", "Classification", "Rule", "Extensions", "Findings");
        var row = 2;
        foreach (var item in run.TypeMappings)
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(row, 1).Value = item.SourceType;
            sheet.Cell(row, 2).Value = item.TargetType;
            sheet.Cell(row, 3).Value = item.Classification.ToString();
            sheet.Cell(row, 4).Value = item.RuleId;
            sheet.Cell(row, 5).Value = string.Join(", ", item.RequiredExtensions);
            sheet.Cell(row, 6).Value = string.Join("; ", item.Findings.Select(finding => finding.Message));
            row++;
        }
    }

    private static void AddProgrammable(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Programmable Review");
        WriteHeaders(sheet, "Target", "Phase", "Classification", "Rule", "Unsupported", "Findings");
        var row = 2;
        foreach (var item in run.Artifacts.Where(item =>
                     item.DeploymentPhase is DeploymentPhase.Views or DeploymentPhase.Functions or
                         DeploymentPhase.PreDataFunctions or
                         DeploymentPhase.Procedures or DeploymentPhase.Triggers))
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(row, 1).Value = item.TargetObjectId.QualifiedName;
            sheet.Cell(row, 2).Value = item.DeploymentPhase.ToString();
            sheet.Cell(row, 3).Value = item.Classification.ToString();
            sheet.Cell(row, 4).Value = item.RuleId;
            sheet.Cell(row, 5).Value = string.Join(", ", item.UnsupportedConstructs);
            sheet.Cell(row, 6).Value = string.Join("; ", item.Findings.Select(finding => finding.Message));
            row++;
        }
    }

    private static void AddComputed(
        XLWorkbook workbook,
        string sheetName,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet(sheetName);
        WriteHeaders(
            sheet,
            "Source object ID",
            "Target object",
            "Source expression/evidence",
            "Generated PostgreSQL",
            "Strategy",
            "Reason",
            "Unsupported functions",
            "Severity");
        var row = 2;
        foreach (var artifact in run.Artifacts.Where(item =>
                     item.Findings.Any(finding => finding.Code.StartsWith("COMPUTED.", StringComparison.Ordinal))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var finding in artifact.Findings.Where(item =>
                         item.Code.StartsWith("COMPUTED.", StringComparison.Ordinal)))
            {
                if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                sheet.Cell(row, 1).Value = artifact.SourceObjectId.ToString();
                sheet.Cell(row, 2).Value = artifact.TargetObjectId.QualifiedName;
                sheet.Cell(row, 3).Value = finding.Evidence ?? artifact.SourceDefinition;
                sheet.Cell(row, 4).Value = artifact.PostgreSqlDefinition;
                sheet.Cell(row, 5).Value = finding.Code == "COMPUTED.GENERATED"
                    ? "GeneratedStored"
                    : "PopulateDuringDataMigrationOrManual";
                sheet.Cell(row, 6).Value = finding.Message;
                sheet.Cell(row, 7).Value = string.Join(", ", artifact.UnsupportedConstructs);
                sheet.Cell(row, 8).Value = finding.Severity.ToString();
                row++;
            }
        }
    }

    private static void AddFindings(
        XLWorkbook workbook,
        string sheetName,
        ConversionRun run,
        Func<InventoryFinding, bool> predicate,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet(sheetName);
        WriteHeaders(sheet, "Severity", "Code", "Object ID", "Message", "Evidence");
        var row = 2;
        foreach (var item in run.Findings.Where(predicate))
        {
            if ((row & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(row, 1).Value = item.Severity.ToString();
            sheet.Cell(row, 2).Value = item.Code;
            sheet.Cell(row, 3).Value = item.ObjectId?.ToString() ?? string.Empty;
            sheet.Cell(row, 4).Value = item.Message;
            sheet.Cell(row, 5).Value = item.Evidence ?? string.Empty;
            row++;
        }
    }

    private static void AddExtensions(
        XLWorkbook workbook,
        ConversionRun run,
        CancellationToken cancellationToken)
    {
        var sheet = workbook.AddWorksheet("Required Extensions");
        WriteHeaders(sheet, "Extension");
        for (var index = 0; index < run.RequiredExtensions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sheet.Cell(index + 2, 1).Value = run.RequiredExtensions[index];
        }
    }

    private static async Task WriteCsvAsync(
        ConversionRun run,
        string directory,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.Combine(directory, "Identifier_Mapping.csv"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(true), 65536);
        await writer.WriteLineAsync(
            "Object Type,Parent Object,Source Database,Source Schema,Source Name,Source Qualified Name,Target Schema,Target Name,Target Qualified Name,Source UTF-8 Byte Length,Target UTF-8 Byte Length,Source Character Length,Target Character Length,Is Reserved Word,Requires Quoting,Was Quoted,Was Case-Normalized,Was Shortened,Collision Detected,Collision Resolved,Mapping Status,Transformation Reason,Hash Suffix,Severity,Manual Review Required,Source Object ID,Target Parent Object,Mapping Action,Invalid-Character Replacement,Auto-Recovered,Included In Scope,Conversion Classification,Collision Group,Collision Resolution,Warnings")
            .ConfigureAwait(false);
        var processed = 0;
        foreach (var item in run.IdentifierMappings)
        {
            if ((processed++ & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            await writer.WriteLineAsync(string.Join(",", IdentifierValues(item).Select(Csv)))
                .ConfigureAwait(false);
        }
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(
        ConversionRun run,
        string directory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = string.Join(
            Environment.NewLine,
            run.Artifacts.Select((item, index) =>
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                return
                $"<tr><td>{H(item.TargetObjectId.QualifiedName)}</td><td>{H(item.DeploymentPhase.ToString())}</td>" +
                $"<td>{H(item.Classification.ToString())}</td><td>{item.Confidence:P0}</td>" +
                $"<td>{(item.RequiresManualReview ? "Yes" : "No")}</td></tr>";
            }));
        var findings = string.Join(
            Environment.NewLine,
            run.Findings.Select((item, index) =>
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                return $"<tr><td>{H(item.Severity.ToString())}</td><td>{H(item.Code)}</td><td>{H(item.Message)}</td></tr>";
            }));
        var identifierData = JsonSerializer.Serialize(
            run.IdentifierMappings.Select((item, index) =>
            {
                if ((index & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return new
                {
                status = StatusText(item),
                css = StatusCss(item),
                type = item.ObjectType,
                schema = item.SourceSchema,
                action = item.MappingAction.ToString(),
                source = item.SourceQualifiedName,
                target = item.TargetQualifiedName,
                reason = item.TransformationReason,
                reserved = item.IsReservedWord,
                shortened = item.WasShortened,
                collision = item.HadCollision,
                blocking = item.IsBlocking,
                manual = item.ManualReviewRequired,
                warning = item.Warnings.Count > 0,
                autoRecovered = item.AutoRecovered,
                unresolved = item.MappingAction == IdentifierMappingAction.Unsupported || item.IsBlocking
                };
            }),
            CompactJsonOptions);
        cancellationToken.ThrowIfCancellationRequested();
        var html = $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><title>Migration conversion report</title>
            <style>body{font-family:Segoe UI,Arial,sans-serif;margin:2rem;color:#202124}table{border-collapse:collapse;width:100%;margin:1rem 0}th,td{border:1px solid #ccd0d5;padding:.45rem;text-align:left}th{background:#eef2f7}.metric{display:inline-block;margin:0 1.5rem 1rem 0;font-size:1.15rem}.badge{font-weight:600;padding:.15rem .45rem;border-radius:.3rem}.safe .badge{background:#d9ead3}.reserved .badge{background:#fff2cc}.transformed .badge{background:#fce5cd}.blocking{background:#b71c1c;color:#fff}.blocking .badge{background:#7f0000}label{margin-right:1rem}</style></head>
            <body><h1>SQL Server to PostgreSQL conversion report</h1>
            <p>Source: <strong>{{H(run.SourceDatabase)}}</strong> · PostgreSQL {{run.TargetVersion.Major}} · Run {{run.RunId:N}}</p>
            <div class="metric">Artifacts: <strong>{{run.Artifacts.Count}}</strong></div>
            <div class="metric">Manual review: <strong>{{run.Artifacts.Count(item => item.RequiresManualReview)}}</strong></div>
            <div class="metric">Findings: <strong>{{run.Findings.Count}}</strong></div>
            <h2>Identifier mapping</h2>
            <p><label>Object type <select id="identifier-type"><option value="">All</option></select></label><label>Source schema <select id="identifier-schema"><option value="">All</option></select></label><label>Mapping action <select id="identifier-action"><option value="">All</option></select></label></p>
            <p><label><input type="checkbox" data-filter="reserved"> Reserved words</label><label><input type="checkbox" data-filter="shortened"> Truncated</label><label><input type="checkbox" data-filter="collision"> Collisions</label><label><input type="checkbox" data-filter="warning"> Warnings</label><label><input type="checkbox" data-filter="autoRecovered"> Auto-recovered</label><label><input type="checkbox" data-filter="unresolved"> Unresolved</label><label><input type="checkbox" data-filter="blocking"> Blocking</label><label><input type="checkbox" data-filter="manual"> Manual review</label></p>
            <p><label>Search <input id="identifier-search" type="search"></label><button id="identifier-prev" type="button">Previous</button> <span id="identifier-page"></span> <button id="identifier-next" type="button">Next</button></p>
            <table id="identifier-mapping"><thead><tr><th>Status</th><th>Type</th><th>Source</th><th>Target</th><th>Reason</th></tr></thead><tbody></tbody></table>
            <script type="application/json" id="identifier-data">{{identifierData}}</script>
            <h2>Object conversion inventory</h2><table><thead><tr><th>Target</th><th>Phase</th><th>Classification</th><th>Confidence</th><th>Manual review</th></tr></thead><tbody>{{rows}}</tbody></table>
            <h2>Findings</h2><table><thead><tr><th>Severity</th><th>Code</th><th>Message</th></tr></thead><tbody>{{findings}}</tbody></table>
            <script>(function(){'use strict';var all=JSON.parse(document.getElementById('identifier-data').textContent),page=0,size=100,body=document.querySelector('#identifier-mapping tbody'),search=document.getElementById('identifier-search'),type=document.getElementById('identifier-type'),schema=document.getElementById('identifier-schema'),action=document.getElementById('identifier-action');function options(select,values){Array.from(new Set(values)).sort().forEach(function(value){var option=document.createElement('option');option.value=value;option.textContent=value;select.appendChild(option);});}options(type,all.map(function(x){return x.type;}));options(schema,all.map(function(x){return x.schema;}));options(action,all.map(function(x){return x.action;}));function cell(row,value,cls){var c=document.createElement('td');if(cls){var badge=document.createElement('span');badge.className='badge';badge.textContent=value;c.appendChild(badge);}else{c.textContent=value;}row.appendChild(c);}function filtered(){var active=Array.from(document.querySelectorAll('[data-filter]:checked')).map(function(x){return x.dataset.filter;}),term=search.value.toLocaleLowerCase();return all.filter(function(x){return(!type.value||x.type===type.value)&&(!schema.value||x.schema===schema.value)&&(!action.value||x.action===action.value)&&(active.length===0||active.some(function(name){return x[name]===true;}))&&(!term||(x.source+' '+x.target+' '+x.type+' '+x.reason).toLocaleLowerCase().includes(term));});}function render(){var data=filtered(),pages=Math.max(1,Math.ceil(data.length/size));page=Math.min(page,pages-1);body.replaceChildren();data.slice(page*size,(page+1)*size).forEach(function(x){var row=document.createElement('tr');row.className=x.css;cell(row,x.status,true);cell(row,x.type);cell(row,x.source);cell(row,x.target);cell(row,x.reason);body.appendChild(row);});document.getElementById('identifier-page').textContent='Page '+(page+1)+' of '+pages+' — '+data.length+' mappings';document.getElementById('identifier-prev').disabled=page===0;document.getElementById('identifier-next').disabled=page>=pages-1;}document.querySelectorAll('[data-filter],#identifier-type,#identifier-schema,#identifier-action').forEach(function(x){x.addEventListener('change',function(){page=0;render();});});search.addEventListener('input',function(){page=0;render();});document.getElementById('identifier-prev').addEventListener('click',function(){page--;render();});document.getElementById('identifier-next').addEventListener('click',function(){page++;render();});render();}());</script>
            </body></html>
            """;
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Conversion_Report.html"),
            html,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "Identifier_Mapping.html"),
            html,
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        ConversionRun run,
        string directory,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            Path.Combine(directory, "Identifier_Mapping.json"),
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(
            stream,
            run.IdentifierMappings,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    private static void WriteHeaders(IXLWorksheet sheet, params string[] headers)
    {
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
        }
        StyleHeader(sheet.Range(1, 1, 1, headers.Length));
        sheet.SheetView.FreezeRows(1);
    }

    private static void StyleHeader(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        range.SetAutoFilter();
    }

    private static string Csv(string value) =>
        SpreadsheetCellSanitizer.Escape(value) is var escaped &&
        escaped.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{escaped.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : escaped;

    private static string H(string value) => WebUtility.HtmlEncode(value);

    private static string StatusText(IdentifierMappingEntry item) =>
        item.MappingStatus switch
        {
            IdentifierMappingStatus.ReservedWordSafelyQuoted => "Reserved word — safely quoted",
            IdentifierMappingStatus.AutomaticallyShortened => "Long identifier — automatically shortened",
            IdentifierMappingStatus.CollisionResolved => "Collision — automatically resolved",
            IdentifierMappingStatus.BlockingConflict => "Blocking identifier conflict",
            _ => "Safe"
        };

    private static string StatusCss(IdentifierMappingEntry item) =>
        item.MappingStatus switch
        {
            IdentifierMappingStatus.ReservedWordSafelyQuoted => "reserved",
            IdentifierMappingStatus.AutomaticallyShortened or
                IdentifierMappingStatus.CollisionResolved => "transformed",
            IdentifierMappingStatus.BlockingConflict => "blocking",
            _ => "safe"
        };

    private static XLColor IdentifierStatusColor(IdentifierMappingEntry item) =>
        item.MappingStatus switch
        {
            IdentifierMappingStatus.ReservedWordSafelyQuoted => XLColor.FromHtml("#FFF2CC"),
            IdentifierMappingStatus.AutomaticallyShortened or
                IdentifierMappingStatus.CollisionResolved => XLColor.FromHtml("#F4B183"),
            IdentifierMappingStatus.BlockingConflict => XLColor.FromHtml("#C00000"),
            _ => XLColor.FromHtml("#D9EAD3")
        };

    private static void AddIdentifierLegend(IXLWorksheet sheet, int row, int column)
    {
        sheet.Cell(row, column).Value = "Identifier status legend";
        sheet.Cell(row, column).Style.Font.Bold = true;
        var entries = new[]
        {
            ("Safe", "#D9EAD3"),
            ("Reserved word — safely quoted", "#FFF2CC"),
            ("Long identifier or collision — automatically resolved", "#F4B183"),
            ("Blocking identifier conflict", "#C00000")
        };
        for (var index = 0; index < entries.Length; index++)
        {
            var cell = sheet.Cell(row + index + 1, column);
            cell.Value = entries[index].Item1;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(entries[index].Item2);
            if (entries[index].Item2 == "#C00000")
            {
                cell.Style.Font.FontColor = XLColor.White;
            }
        }
    }
}
