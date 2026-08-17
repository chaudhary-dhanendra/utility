using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Reporting;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public sealed class MigrationReportCoordinator(
    IInventorySession inventory,
    IConversionSession conversion,
    IDataMigrationSession dataMigration,
    IDeploymentSession deployment,
    IValidationSession validation,
    IMigrationReportEngine reports,
    IManualReviewStore manualReviewStore) : IMigrationReportCoordinator
{
    public async Task<MigrationReportRequest> CreateRequestAsync(
        MigrationReportRequestOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = inventory.Current ??
            throw new InvalidOperationException(
                "A discovery inventory is required before generating a report.");
        var manualReviews = options.ManualReviews ??
                            await manualReviewStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        return new MigrationReportRequest
        {
            Inventory = snapshot,
            Conversion = conversion.Current,
            DataMigration = dataMigration.CurrentResult,
            Deployment = deployment.Result,
            Validation = validation.Current,
            Source = new MigrationEndpointSummary(
                options.SourceServer,
                snapshot.Database.DatabaseName,
                snapshot.Database.ProductVersion,
                snapshot.Database.Edition),
            Target = new MigrationEndpointSummary(
                options.TargetServer,
                deployment.Result?.TargetDatabase ?? "Not recorded",
                conversion.Current?.TargetVersion.ToString() ?? "Not recorded",
                "PostgreSQL"),
            Template = options.Template,
            ManualReviews = manualReviews,
            ApplicationVersion = options.ApplicationVersion
        };
    }

    public async Task<ReportPackageResult> GenerateAsync(
        MigrationReportRequestOptions options,
        string parentDirectory,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var request = await CreateRequestAsync(options, cancellationToken).ConfigureAwait(false);
        return await reports.GenerateAsync(request, parentDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReportPackageResult> GenerateDefaultAsync(
        MigrationReportRequestOptions options,
        IProgress<ReportGenerationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var request = await CreateRequestAsync(options, cancellationToken).ConfigureAwait(false);
        var runId = ResolveRunId();
        var reportsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MigrationStudio",
            "Reports",
            SanitizeDirectoryName(runId.ToString("N")));
        return await reports.GenerateToDirectoryAsync(
            request, reportsDirectory, progress, cancellationToken).ConfigureAwait(false);
    }

    private Guid ResolveRunId() =>
        dataMigration.CurrentResult?.RunId ??
        validation.Current?.MigrationRunId ??
        deployment.Result?.DataMigrationRunId ??
        deployment.Result?.DeploymentId ??
        conversion.Current?.RunId ??
        validation.Current?.RunId ??
        throw new InvalidOperationException(
            "A migration, deployment, conversion, or validation run is required before generating reports.");

    internal static string SanitizeDirectoryName(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var sanitized = new string(value
            .Trim()
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "migration-run" : sanitized;
    }
}
