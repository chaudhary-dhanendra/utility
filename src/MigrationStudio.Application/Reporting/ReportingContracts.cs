using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Application.Reporting;

public sealed record ReportGenerationProgress(
    string Stage,
    int Completed,
    int Total,
    string CurrentFile)
{
    public double Percentage => Total == 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public interface IMigrationReportEngine
{
    Task<ReportPackageResult> GenerateAsync(
        MigrationReportRequest request,
        string parentDirectory,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken);

    Task<ReportPackageResult> RegenerateAsync(
        Guid reportRunId,
        string parentDirectory,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IReportTemplateValidator
{
    ReportTemplate Validate(ReportTemplate reportTemplate);
}

public interface IManualReviewStore
{
    Task<IReadOnlyList<ManualReviewItem>> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(ManualReviewItem item, CancellationToken cancellationToken);

    Task ReopenAsync(Guid id, string comment, CancellationToken cancellationToken);
}

public interface IRunHistoryStore
{
    Task SaveAsync(RunHistoryRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunHistoryEntry>> ListAsync(CancellationToken cancellationToken);

    Task<RunHistoryRecord?> LoadAsync(Guid runId, CancellationToken cancellationToken);
}

public interface ISanitizedLogExporter
{
    Task<string> ExportAsync(
        string destinationDirectory,
        IReadOnlySet<Guid> correlationIds,
        CancellationToken cancellationToken);
}
