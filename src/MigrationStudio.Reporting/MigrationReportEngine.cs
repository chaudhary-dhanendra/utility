using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationStudio.Application.Reporting;
using MigrationStudio.Application.Security;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public sealed class MigrationReportEngine(
    IReportTemplateValidator templateValidator,
    ISensitiveDataRedactor redactor,
    IRunHistoryStore runHistory) : IMigrationReportEngine
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<ReportPackageResult> GenerateAsync(
        MigrationReportRequest request,
        string parentDirectory,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var started = DateTimeOffset.UtcNow;
        var reportId = Guid.NewGuid();
        var template = templateValidator.Validate(request.Template);
        var report = MigrationReportDocumentBuilder.Build(request with { Template = template }, reportId);
        report = Sanitize(report);
        return await WritePackageAsync(
            report, parentDirectory, started, null, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReportPackageResult> RegenerateAsync(
        Guid reportRunId,
        string parentDirectory,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        var historical = await runHistory.LoadAsync(reportRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Report-generation run '{reportRunId}' was not found.");
        if (historical.Entry.Kind != RunHistoryKind.ReportGeneration)
        {
            throw new InvalidOperationException("Only report-generation runs can be regenerated.");
        }

        var source = historical.Payload.Deserialize<MigrationReportDocument>(JsonOptions)
            ?? throw new InvalidDataException("The historical report payload is invalid.");
        var started = DateTimeOffset.UtcNow;
        var regenerated = source with
        {
            Summary = source.Summary with
            {
                ReportRunId = Guid.NewGuid(),
                GeneratedAt = started
            }
        };
        regenerated = Sanitize(regenerated);
        return await WritePackageAsync(
            regenerated, parentDirectory, started, reportRunId.ToString(), progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ReportPackageResult> WritePackageAsync(
        MigrationReportDocument report,
        string parentDirectory,
        DateTimeOffset started,
        string? sourceReportRunId,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var reportId = report.Summary.ReportRunId;
        var reportsDirectory = Path.Combine(parentDirectory, "Reports");
        Directory.CreateDirectory(reportsDirectory);
        var files = new List<string>();
        const int total = 11;

        var htmlPath = Path.Combine(reportsDirectory, "MigrationExecutiveSummary.html");
        progress?.Report(new ReportGenerationProgress("HTML", 0, total, htmlPath));
        var html = MigrationHtmlReportWriter.Build(report);
        await File.WriteAllTextAsync(
            htmlPath, html, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        files.Add(htmlPath);

        var pdfPath = Path.Combine(reportsDirectory, "MigrationExecutiveSummary.pdf");
        progress?.Report(new ReportGenerationProgress("PDF", 1, total, pdfPath));
        await Task.Run(
            () => MigrationPdfReportWriter.Write(report, pdfPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        files.Add(pdfPath);

        var workbookPath = Path.Combine(reportsDirectory, "MigrationReport.xlsx");
        progress?.Report(new ReportGenerationProgress("Excel", 2, total, workbookPath));
        await Task.Run(
            () => new MigrationExcelReportWriter().Write(report, workbookPath, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        files.Add(workbookPath);

        var jsonPath = Path.Combine(reportsDirectory, "MigrationReport.json");
        progress?.Report(new ReportGenerationProgress("JSON", 3, total, jsonPath));
        var json = JsonSerializer.Serialize(report, JsonOptions);
        await File.WriteAllTextAsync(
            jsonPath, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        files.Add(jsonPath);

        files.Add(await WriteObjectInventoryAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));
        progress?.Report(new ReportGenerationProgress("CSV", 5, total, "ObjectInventory.csv"));
        files.Add(await WriteObjectReconciliationAsync(
            report,
            reportsDirectory,
            cancellationToken).ConfigureAwait(false));
        files.Add(await WriteIdentifierMappingAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));
        files.Add(await WriteManualReviewAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));
        files.Add(await WriteUnsupportedAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));
        files.Add(await WriteDeploymentFailuresAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));
        files.Add(await WriteDataReconciliationAsync(report, reportsDirectory, cancellationToken).ConfigureAwait(false));

        var completed = DateTimeOffset.UtcNow;
        var historyEntry = new RunHistoryEntry(
            reportId,
            RunHistoryKind.ReportGeneration,
            RunHistoryStatus.Succeeded,
            started,
            completed,
            report.Summary.Source.Database,
            report.Summary.Target.Database,
            $"Generated {files.Count} report artifacts; readiness {report.Summary.OverallReadiness}.",
            "MigrationReport.json",
            sourceReportRunId ?? report.Validation?.RunId.ToString());
        using var payloadDocument = JsonDocument.Parse(json);
        await runHistory.SaveAsync(
            new RunHistoryRecord(historyEntry, payloadDocument.RootElement.Clone()),
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new ReportGenerationProgress("Complete", total, total, reportsDirectory));
        return new ReportPackageResult(reportId, reportsDirectory, files, started, completed);
    }

    private MigrationReportDocument Sanitize(MigrationReportDocument report)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        var sanitized = JsonSanitizer.Sanitize(json, redactor);
        return JsonSerializer.Deserialize<MigrationReportDocument>(sanitized, JsonOptions)
               ?? throw new InvalidDataException("The sanitized report document could not be reconstructed.");
    }

    private static Task<string> WriteObjectInventoryAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "ObjectInventory.csv"),
            ["ObjectId", "Type", "Schema", "Name", "QualifiedName", "Included", "Classification", "DiscoveryStatus"],
            report.Inventory.Objects.Select(item => new[]
            {
                item.Id.ToString(), item.ObjectType.ToString(), item.SourceSchema, item.SourceName,
                item.QualifiedSourceName, item.IsIncluded.ToString(CultureInfo.InvariantCulture),
                item.ConversionClassification.ToString(), item.DiscoveryStatus.ToString()
            }),
            cancellationToken);

    private static Task<string> WriteObjectReconciliationAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "ObjectReconciliation.csv"),
            ["ObjectId", "SourceObject", "ObjectType", "FinalStatus", "Reason", "EffectiveArtifactSourceObjectId"],
            report.ObjectReconciliation.Select(item => new[]
            {
                item.SourceObjectId.ToString(),
                item.SourceObject,
                item.ObjectType.ToString(),
                item.Status.ToString(),
                item.Reason,
                item.EffectiveArtifactSourceObjectId?.ToString() ?? string.Empty
            }),
            cancellationToken);

    private static Task<string> WriteIdentifierMappingAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "IdentifierMapping.csv"),
            [
                "ObjectType", "ParentObject", "SourceDatabase", "SourceSchema", "SourceName",
                "SourceQualifiedName", "TargetSchema", "TargetName", "TargetQualifiedName",
                "SourceUtf8ByteLength", "TargetUtf8ByteLength", "SourceCharacterLength",
                "TargetCharacterLength", "IsReservedWord", "RequiresQuoting", "WasQuoted",
                "WasCaseNormalized", "WasShortened", "CollisionDetected", "CollisionResolved",
                "MappingStatus", "TransformationReason", "HashSuffix", "Severity",
                "ManualReviewRequired"
            ],
            report.Conversion?.IdentifierMappings.Select(item => new[]
            {
                item.ObjectType, item.ParentObject, item.SourceDatabase, item.SourceSchema,
                item.SourceName, item.SourceQualifiedName, item.TargetSchema, item.TargetName,
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
                item.MappingStatus.ToString(), item.TransformationReason,
                item.HashSuffix ?? string.Empty, item.Severity.ToString(),
                item.ManualReviewRequired.ToString(CultureInfo.InvariantCulture)
            }) ?? [],
            cancellationToken);

    private static Task<string> WriteManualReviewAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "ManualReview.csv"),
            ["Id", "Status", "Critical", "Owner", "Title", "Source", "Comments", "Resolution", "ReviewedBy", "ReviewedAt"],
            report.ManualReviews.Select(item => new[]
            {
                item.Id.ToString(), item.Status.ToString(), item.IsCriticalBlocker.ToString(CultureInfo.InvariantCulture),
                item.Owner ?? string.Empty, item.Title, item.Source, item.Comments ?? string.Empty,
                item.Resolution ?? string.Empty, item.ReviewedBy ?? string.Empty, item.ReviewedAt?.ToString("O") ?? string.Empty
            }),
            cancellationToken);

    private static Task<string> WriteUnsupportedAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "UnsupportedFeatures.csv"),
            ["SourceObjectId", "TargetObject", "Rule", "UnsupportedConstructs"],
            report.Conversion?.Artifacts.Where(item =>
                    item.Classification == Domain.Inventory.ConversionClassification.Unsupported ||
                    item.UnsupportedConstructs.Count > 0)
                .Select(item => new[]
                {
                    item.SourceObjectId.ToString(), item.TargetObjectId.QualifiedName, item.RuleId,
                    string.Join("; ", item.UnsupportedConstructs)
                }) ?? [],
            cancellationToken);

    private static Task<string> WriteDeploymentFailuresAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "DeploymentFailures.csv"),
            ["Phase", "TargetObject", "SqlState", "Severity", "Script", "Line", "Started", "Ended"],
            report.Deployment?.Failures.Select(item => new[]
            {
                item.Phase.ToString(), item.TargetObject ?? string.Empty, item.SqlState ?? string.Empty,
                item.Severity ?? string.Empty, item.ScriptFile ?? string.Empty,
                item.Line?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                item.StartedAt.ToString("O"), item.EndedAt.ToString("O")
            }) ?? [],
            cancellationToken);

    private static Task<string> WriteDataReconciliationAsync(
        MigrationReportDocument report,
        string directory,
        CancellationToken cancellationToken) =>
        WriteCsvAsync(
            Path.Combine(directory, "DataReconciliation.csv"),
            ["SourceTable", "TargetTable", "SourceRows", "TargetRows", "Classification", "OrderedChecksum", "Detail"],
            report.Validation?.DataComparisons.Select(item => new[]
            {
                item.SourceTable, item.TargetTable, item.SourceRowCount.ToString(CultureInfo.InvariantCulture),
                item.TargetRowCount.ToString(CultureInfo.InvariantCulture), item.Classification.ToString(),
                item.IsOrderedChecksum.ToString(CultureInfo.InvariantCulture), item.Detail
            }) ?? [],
            cancellationToken);

    private static async Task<string> WriteCsvAsync(
        string path,
        IReadOnlyList<string> headers,
        IEnumerable<string[]> rows,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(",", headers.Select(Csv)));
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.AppendLine(string.Join(",", row.Select(Csv)));
        }
        await File.WriteAllTextAsync(
            path, text.ToString(), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string Csv(string value) =>
        $"\"{SpreadsheetCellSanitizer.Escape(value).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        options.Converters.Add(new ReadOnlySetJsonConverterFactory());
        return options;
    }

    private sealed class ReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) =>
            typeToConvert.IsGenericType &&
            typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var elementType = typeToConvert.GetGenericArguments()[0];
            return (JsonConverter)Activator.CreateInstance(
                typeof(ReadOnlySetJsonConverter<>).MakeGenericType(elementType))!;
        }
    }

    private sealed class ReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
        where T : notnull
    {
        public override IReadOnlySet<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<HashSet<T>>(ref reader, options) ?? [];

        public override void Write(
            Utf8JsonWriter writer,
            IReadOnlySet<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
