using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Tests.Desktop;

public sealed class ConversionOperationProgressTrackerTests
{
    [Fact]
    public async Task ProgressStream_PublishesTheSameAuthoritativeSnapshotToOperationAndUi()
    {
        var operations = new List<OperationProgress>();
        var presented = new List<ConversionProgressSnapshot>();
        using var tracker = new ConversionOperationProgressTracker(
            OperationId.New(),
            operations.Add,
            presented.Add,
            NullLogger.Instance,
            heartbeatInterval: TimeSpan.FromSeconds(1));

        var result = await tracker.RunAsync(
            (progress, _) =>
            {
                progress.Report(Item(ConversionStage.ValidatingIdentifiers, 10, 100));
                progress.Report(Item(ConversionStage.PublishingIdentifierMap, 100, 100));
                progress.Report(Item(ConversionStage.ConvertingObjects, 1, 50));
                return Task.FromResult(42);
            },
            CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(operations.Count, presented.Count);
        Assert.All(
            operations.Zip(presented),
            pair => Assert.Same(pair.First.Conversion, pair.Second));
        Assert.Equal(
            operations.Select(item => item.Percentage).Order().ToArray(),
            operations.Select(item => item.Percentage).ToArray());
    }

    [Fact]
    public async Task Watchdog_ResetsStaleRateCapturesDiagnosticAndFailsTheWorker()
    {
        var diagnostics = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var presented = new List<ConversionProgressSnapshot>();
        try
        {
            using var tracker = new ConversionOperationProgressTracker(
                OperationId.New(),
                _ => { },
                presented.Add,
                NullLogger.Instance,
                heartbeatInterval: TimeSpan.FromMilliseconds(20),
                staleRateAfter: TimeSpan.FromMilliseconds(30),
                unresponsiveAfter: TimeSpan.FromMilliseconds(40),
                diagnosticAfter: TimeSpan.FromMilliseconds(70),
                failAfter: TimeSpan.FromMilliseconds(120),
                diagnosticsDirectory: diagnostics);

            var exception = await Assert.ThrowsAsync<ConversionStalledException>(() =>
                tracker.RunAsync(
                    async (progress, cancellationToken) =>
                    {
                        progress.Report(Item(
                            ConversionStage.ValidatingIdentifiers,
                            341_248,
                            386_861) with { ObjectsPerSecond = 250_000 });
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return 0;
                    },
                    CancellationToken.None));

            Assert.Contains(presented, item => !item.IsResponsive);
            Assert.Contains(presented, item => item.RatePerSecond == 0);
            Assert.True(File.Exists(exception.DiagnosticFilePath));
            Assert.Equal(ConversionStage.ValidatingIdentifiers, exception.Stage);
        }
        finally
        {
            if (Directory.Exists(diagnostics))
            {
                Directory.Delete(diagnostics, recursive: true);
            }
        }
    }

    private static ConversionProgress Item(
        ConversionStage stage,
        int processed,
        int total) =>
        new(stage, processed, total, $"{stage}: {processed}/{total}")
        {
            CurrentObjectType = "Column",
            CurrentObject = "dbo.fixture.value",
            LastProgressAt = DateTimeOffset.UtcNow
        };
}
