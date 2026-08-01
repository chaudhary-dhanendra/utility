using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Reporting;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Reporting;

public static class MigrationReportDocumentBuilder
{
    public static MigrationReportDocument Build(MigrationReportRequest request, Guid reportRunId)
    {
        ArgumentNullException.ThrowIfNull(request);
        var data = request.DataMigration;
        var deployment = request.Deployment;
        var validation = request.Validation;
        var conversion = request.Conversion;
        var rowsRead = data?.Tables.Sum(item => item.RowsRead) ?? 0;
        var rowsWritten = data?.Tables.Sum(item => item.RowsWritten) ?? 0;
        var failedRows = data?.Tables.Sum(item => item.RowsRejected) ?? 0;
        var totalDuration = SumDurations(
            data is null ? null : data.CompletedAt - data.StartedAt,
            deployment is null ? null : deployment.CompletedAt - deployment.StartedAt,
            validation is null ? null : validation.CompletedAt - validation.StartedAt);
        var summary = new MigrationReportSummary(
            reportRunId,
            DateTimeOffset.UtcNow,
            request.ApplicationVersion,
            request.Source,
            request.Target,
            request.Inventory.ScopeMode,
            request.Inventory.Schemas.Where(item => item.InventoryObject.IsIncluded)
                .Select(item => item.InventoryObject.SourceSchema).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            request.Inventory.Objects.Count(item => item.IsIncluded),
            request.Inventory.Objects.Count(item => !item.IsIncluded),
            conversion is null ? "Not run" :
                conversion.RequiresManualReview ? "Completed with manual review" : "Completed",
            data?.State.ToString() ?? "Not run",
            deployment?.Status.ToString() ?? "Not run",
            validation?.Readiness.OverallStatus.ToString() ?? "Not run",
            validation?.Readiness.OverallStatus.ToString() ?? "Incomplete",
            validation?.Readiness.CriticalBlockers.Count ??
            request.Inventory.Findings.Count(item => item.Severity == FindingSeverity.Critical),
            CountWarnings(request),
            request.ManualReviews.Count(item =>
                item.Status is ManualReviewStatus.Open or ManualReviewStatus.InProgress) +
            (conversion?.Artifacts.Count(item => item.RequiresManualReview) ?? 0),
            conversion?.Artifacts.Count(item =>
                item.Classification == ConversionClassification.Unsupported) ?? 0,
            rowsRead,
            rowsWritten,
            failedRows,
            totalDuration,
            data?.Tables.Sum(item => item.TotalDuration.TotalSeconds) > 0
                ? rowsWritten / data.Tables.Sum(item => item.TotalDuration.TotalSeconds)
                : 0,
            deployment is null ? TimeSpan.Zero : deployment.CompletedAt - deployment.StartedAt,
            validation is null ? TimeSpan.Zero : validation.CompletedAt - validation.StartedAt);
        var reconciliation = BuildObjectReconciliation(request);
        var reconciliationSummary = new ObjectReconciliationSummary(
            reconciliation.Count,
            reconciliation.Count(item =>
                item.Status == SourceObjectFinalStatus.ConvertedDeployedValidated),
            reconciliation.Count(item =>
                item.Status == SourceObjectFinalStatus.ConvertedValidationFailed),
            reconciliation.Count(item =>
                item.Status == SourceObjectFinalStatus.ManualConversionRequired),
            reconciliation.Count(item => item.Status == SourceObjectFinalStatus.Unsupported),
            reconciliation.Count(item =>
                item.Status == SourceObjectFinalStatus.ExcludedExplicitly),
            reconciliation.Count(item =>
                item.Status == SourceObjectFinalStatus.NotApplicableToPostgreSql),
            reconciliation.Count(item => item.Status == SourceObjectFinalStatus.Unreconciled));
        return new MigrationReportDocument
        {
            Inventory = request.Inventory,
            Conversion = conversion,
            DataMigration = data,
            Deployment = deployment,
            Validation = validation,
            ManualReviews = request.ManualReviews,
            Template = request.Template,
            ObjectReconciliation = reconciliation,
            ReconciliationSummary = reconciliationSummary,
            Summary = summary with
            {
                OverallReadiness = reconciliationSummary.IsBalanced
                    ? summary.OverallReadiness
                    : "Incomplete - object totals do not reconcile"
            }
        };
    }

    private static List<SourceObjectReconciliation> BuildObjectReconciliation(
        MigrationReportRequest request)
    {
        var objectsById = request.Inventory.Objects.ToDictionary(item => item.Id);
        var artifactsBySource = (request.Conversion?.Artifacts ?? [])
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var journalBySource = (request.Deployment?.Objects ?? [])
            .Where(item => item.SourceObjectId is not null)
            .GroupBy(item => item.SourceObjectId!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<SourceObjectReconciliation>(request.Inventory.Objects.Count);

        foreach (var source in request.Inventory.Objects.OrderBy(item => item.Id.Value))
        {
            if (!source.IsIncluded)
            {
                result.Add(Item(
                    source,
                    SourceObjectFinalStatus.ExcludedExplicitly,
                    "The source object is outside the selected migration scope.",
                    null));
                continue;
            }

            var effectiveId = FindEffectiveArtifactSource(
                source,
                objectsById,
                artifactsBySource);
            if (effectiveId is null)
            {
                var status = source.ConversionClassification switch
                {
                    ConversionClassification.Unsupported =>
                        SourceObjectFinalStatus.Unsupported,
                    ConversionClassification.ManualConversion =>
                        SourceObjectFinalStatus.ManualConversionRequired,
                    _ => SourceObjectFinalStatus.NotApplicableToPostgreSql
                };
                result.Add(Item(
                    source,
                    status,
                    status == SourceObjectFinalStatus.NotApplicableToPostgreSql
                        ? "No standalone PostgreSQL artifact is applicable; the object is retained in reconciliation."
                        : $"Discovery classified the object as {source.ConversionClassification}.",
                    null));
                continue;
            }

            var artifacts = artifactsBySource[effectiveId.Value];
            if (artifacts.Any(item =>
                    item.Classification == ConversionClassification.Unsupported))
            {
                result.Add(Item(
                    source,
                    SourceObjectFinalStatus.Unsupported,
                    "The effective conversion artifact is unsupported.",
                    effectiveId));
                continue;
            }
            if (artifacts.Any(item =>
                    item.RequiresManualReview ||
                    item.Classification == ConversionClassification.ManualConversion))
            {
                result.Add(Item(
                    source,
                    SourceObjectFinalStatus.ManualConversionRequired,
                    "The effective conversion artifact requires manual conversion.",
                    effectiveId));
                continue;
            }
            if (artifacts.Any(item =>
                    item.Validation.Outcome == LiveSqlValidationOutcome.Failed))
            {
                result.Add(Item(
                    source,
                    SourceObjectFinalStatus.ConvertedValidationFailed,
                    "PostgreSQL rejected at least one effective artifact during live validation.",
                    effectiveId));
                continue;
            }

            var deployed = journalBySource.TryGetValue(effectiveId.Value, out var journal) &&
                journal.All(item => item.Status is DeploymentObjectStatus.Succeeded
                    or DeploymentObjectStatus.SkippedEquivalent);
            var validated = artifacts.All(item =>
                item.Validation.Outcome == LiveSqlValidationOutcome.Passed &&
                item.Validation.WasLiveValidated &&
                item.Validation.IsStructurallyValid);
            result.Add(Item(
                source,
                deployed && validated
                    ? SourceObjectFinalStatus.ConvertedDeployedValidated
                    : SourceObjectFinalStatus.Unreconciled,
                deployed
                    ? "Deployment is terminal but live PostgreSQL validation is incomplete."
                    : "The effective artifact has not reached a successful terminal deployment state.",
                effectiveId));
        }

        return result;
    }

    private static InventoryObjectId? FindEffectiveArtifactSource(
        InventoryObject source,
        Dictionary<InventoryObjectId, InventoryObject> objectsById,
        Dictionary<InventoryObjectId, ConversionArtifact[]> artifactsBySource)
    {
        InventoryObject? current = source;
        while (current is not null)
        {
            if (artifactsBySource.ContainsKey(current.Id))
            {
                return current.Id;
            }

            current = current.ParentObjectId is { } parentId &&
                objectsById.TryGetValue(parentId, out var parent)
                    ? parent
                    : null;
        }

        return null;
    }

    private static SourceObjectReconciliation Item(
        InventoryObject source,
        SourceObjectFinalStatus status,
        string reason,
        InventoryObjectId? effectiveId) =>
        new(
            source.Id,
            source.QualifiedSourceName,
            source.ObjectType,
            status,
            reason,
            effectiveId);

    private static int CountWarnings(MigrationReportRequest request) =>
        request.Inventory.Findings.Count(item => item.Severity == FindingSeverity.Warning) +
        (request.Conversion?.Findings.Count(item => item.Severity == FindingSeverity.Warning) ?? 0) +
        (request.Deployment?.Findings.Count(item =>
            item.Severity == Domain.Deployment.DeploymentFindingSeverity.Warning) ?? 0) +
        (request.Validation?.Findings.Count(item =>
            item.Severity == ValidationSeverity.Warning) ?? 0);

    private static TimeSpan SumDurations(params TimeSpan?[] values) =>
        TimeSpan.FromTicks(values.Where(item => item is not null).Sum(item => item!.Value.Ticks));
}

public sealed class ReportTemplateValidator : Application.Reporting.IReportTemplateValidator
{
    public ReportTemplate Validate(ReportTemplate reportTemplate)
    {
        ArgumentNullException.ThrowIfNull(reportTemplate);
        if (reportTemplate.TemplateId is not ("professional-light" or "dashboard-dark"))
        {
            throw new InvalidOperationException("Only built-in report templates are supported.");
        }
        if (!string.IsNullOrWhiteSpace(reportTemplate.LogoPath) &&
            !File.Exists(reportTemplate.LogoPath))
        {
            throw new FileNotFoundException("The configured report logo does not exist.", reportTemplate.LogoPath);
        }
        if (!string.IsNullOrWhiteSpace(reportTemplate.LogoPath))
        {
            var extension = Path.GetExtension(reportTemplate.LogoPath);
            if (extension is not (".png" or ".PNG" or ".jpg" or ".JPG" or ".jpeg" or ".JPEG"))
            {
                throw new InvalidOperationException("Report logos must use PNG or JPEG format.");
            }
            if (new FileInfo(reportTemplate.LogoPath).Length > 5 * 1024 * 1024)
            {
                throw new InvalidOperationException("Report logos cannot exceed 5 MB.");
            }
        }
        if (reportTemplate.OrganizationName.Length > 200 ||
            reportTemplate.ProjectName.Length > 200 ||
            reportTemplate.ReportTitle.Length > 200 ||
            reportTemplate.Footer.Length > 500)
        {
            throw new InvalidOperationException("Report template text exceeds the supported length.");
        }
        _ = DateTimeOffset.UtcNow.ToString(reportTemplate.DateTimeFormat, System.Globalization.CultureInfo.InvariantCulture);
        return reportTemplate;
    }
}
