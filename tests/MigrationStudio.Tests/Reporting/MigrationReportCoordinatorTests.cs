using System.IO;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Reporting;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Reporting;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Reporting;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Reporting;

public sealed class MigrationReportCoordinatorTests
{
    [Fact]
    public async Task DefaultGenerationUsesLocalApplicationDataAndExistingMigrationRunId()
    {
        var source = ReportingFixture.CreateRequest();
        var inventory = new InventorySession();
        inventory.SetCurrent(source.Inventory);
        var conversion = new StubConversionSession(source.Conversion!);
        var dataMigration = new DataMigrationSession();
        dataMigration.SetResult(source.DataMigration!);
        var deployment = new DeploymentSession();
        deployment.SetResult(source.Deployment!);
        var validation = new ValidationSession();
        validation.SetCurrent(source.Validation!);
        var reportEngine = new CapturingReportEngine();
        var manualReviews = new InMemoryManualReviewStore(source.ManualReviews);
        var coordinator = new MigrationReportCoordinator(
            inventory,
            conversion,
            dataMigration,
            deployment,
            validation,
            reportEngine,
            manualReviews);

        var result = await coordinator.GenerateDefaultAsync(
            new MigrationReportRequestOptions
            {
                SourceServer = "source-host",
                TargetServer = "target-host",
                ApplicationVersion = "test-version"
            },
            null,
            CancellationToken.None);

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MigrationStudio",
            "Reports",
            source.DataMigration!.RunId.ToString("N"));
        Assert.Equal(expected, reportEngine.DirectReportsDirectory);
        Assert.Equal(expected, result.ReportsDirectory);
        Assert.Equal("source-host", reportEngine.Request!.Source.Server);
        Assert.Equal("target-host", reportEngine.Request.Target.Server);
        Assert.Same(source.Inventory, reportEngine.Request.Inventory);
        Assert.Same(source.Conversion, reportEngine.Request.Conversion);
        Assert.Same(source.DataMigration, reportEngine.Request.DataMigration);
        Assert.Same(source.Deployment, reportEngine.Request.Deployment);
        Assert.Same(source.Validation, reportEngine.Request.Validation);
        Assert.Equal(source.ManualReviews, reportEngine.Request.ManualReviews);
    }

    private sealed class CapturingReportEngine : IMigrationReportEngine
    {
        public MigrationReportRequest? Request { get; private set; }

        public string? DirectReportsDirectory { get; private set; }

        public Task<ReportPackageResult> GenerateAsync(
            MigrationReportRequest request,
            string parentDirectory,
            IProgress<ReportGenerationProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReportPackageResult> RegenerateAsync(
            Guid reportRunId,
            string parentDirectory,
            IProgress<ReportGenerationProgress>? progress,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReportPackageResult> GenerateToDirectoryAsync(
            MigrationReportRequest request,
            string reportsDirectory,
            IProgress<ReportGenerationProgress>? progress,
            CancellationToken cancellationToken)
        {
            Request = request;
            DirectReportsDirectory = reportsDirectory;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ReportPackageResult(
                Guid.NewGuid(), reportsDirectory, [], now, now));
        }
    }

    private sealed class StubConversionSession(ConversionRun current) : IConversionSession
    {
        public ConversionRun? Current { get; private set; } = current;

        public event EventHandler? Changed;

        public void SetCurrent(ConversionRun run)
        {
            Current = run;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            Current = null;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class InMemoryManualReviewStore(IReadOnlyList<ManualReviewItem> items) :
        IManualReviewStore
    {
        public Task<IReadOnlyList<ManualReviewItem>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(items);

        public Task SaveAsync(ManualReviewItem item, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReopenAsync(Guid id, string comment, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
