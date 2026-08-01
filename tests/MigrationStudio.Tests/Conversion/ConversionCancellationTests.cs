using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Settings;
using MigrationStudio.Deployment;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Operations;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Operations;

namespace MigrationStudio.Tests.Conversion;

public sealed class ConversionCancellationTests
{
    [Fact]
    public void IdentifierMapping_CancellationInterruptsLargeValidationWithinTwoSeconds()
    {
        var objects = Enumerable.Range(1, 25_000)
            .Select(index => CreateObject(index))
            .ToArray();
        var inventory = TestInventory.CreateSnapshot(objects);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ConversionProgress>(item =>
        {
            if (item.CompletedObjects >= 512)
            {
                cancellation.Cancel();
            }
        });
        var stopwatch = Stopwatch.StartNew();

        Assert.Throws<OperationCanceledException>(() =>
            new PostgreSqlIdentifierMappingService().CreateMapper(
                inventory,
                new ConversionOptions(),
                cancellation.Token,
                progress));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
    }

    [Fact]
    public async Task CancelledPackage_IsNotPublished_AndRetrySucceeds()
    {
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        using var cancellation = new CancellationTokenSource();
        var run = EmptyRun();
        try
        {
            var cancellingWriter = new MigrationPackageWriter(
                new CancellingReportWriter(cancellation));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                cancellingWriter.WriteAsync(run, parent, cancellation.Token));

            Assert.Empty(Directory.EnumerateDirectories(parent));

            var package = await new MigrationPackageWriter(new EmptyReportWriter())
                .WriteAsync(run, parent, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(package, "manifest.json")));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(parent),
                path => Path.GetFileName(path).Contains(".partial-", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task SchedulerCancellation_EndsOperationAsCancelled_AndAllowsRetry()
    {
        var monitor = new OperationMonitor();
        using var service = new BackgroundOperationService(
            Options.Create(new InfrastructureOptions { OperationQueueCapacity = 4 }),
            new TestSettingsService(),
            monitor,
            NullLogger<BackgroundOperationService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstId = await service.EnqueueAsync(new BackgroundOperationDefinition(
                "Convert cancellation fixture",
                async (_, cancellationToken) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                "conversion:fixture"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(service.Cancel(firstId));
            var cancelled = await WaitForTerminalAsync(monitor, firstId);
            Assert.Equal(OperationState.Cancelled, cancelled.State);

            var retryId = await service.EnqueueAsync(new BackgroundOperationDefinition(
                "Convert retry fixture",
                (_, _) => ValueTask.CompletedTask,
                "conversion:fixture"));
            var retried = await WaitForTerminalAsync(monitor, retryId);
            Assert.Equal(OperationState.Completed, retried.State);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ApplicationShutdownCancellation_CancelsActiveWorkWithinBoundedWait()
    {
        var monitor = new OperationMonitor();
        using var service = new BackgroundOperationService(
            Options.Create(new InfrastructureOptions { OperationQueueCapacity = 4 }),
            new TestSettingsService(),
            monitor,
            NullLogger<BackgroundOperationService>.Instance);
        await service.StartAsync(CancellationToken.None);
        try
        {
            var entered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var operationId = await service.EnqueueAsync(new BackgroundOperationDefinition(
                "Conversion shutdown fixture",
                async (_, cancellationToken) =>
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                },
                "conversion:shutdown-fixture"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var stopwatch = Stopwatch.StartNew();
            foreach (var operation in monitor.Operations.Where(item => item.IsActive))
            {
                service.Cancel(operation.Id);
            }

            var cancelled = await WaitForTerminalAsync(monitor, operationId);
            Assert.Equal(OperationState.Cancelled, cancelled.State);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), stopwatch.Elapsed.ToString());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<OperationSnapshot> WaitForTerminalAsync(
        OperationMonitor monitor,
        OperationId id)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var operation = monitor.Operations.Single(item => item.Id == id);
            if (!operation.IsActive)
            {
                return operation;
            }
            await Task.Delay(20);
        }
        throw new TimeoutException("Operation did not reach a terminal state.");
    }

    private static InventoryObject CreateObject(int index)
    {
        var name = $"Table_{index:D6}";
        return new InventoryObject(
            InventoryObjectId.Create("fixture", InventoryObjectType.Table, "dbo", name, index),
            "fixture",
            "dbo",
            name,
            $"[dbo].[{name}]",
            InventoryObjectType.Table,
            index,
            null,
            null,
            null,
            false,
            true,
            SelectionReason.CompleteDatabase,
            0,
            0,
            [],
            ConversionClassification.Automatic,
            null,
            null,
            $"hash-{index}",
            [],
            DiscoveryStatus.Discovered);
    }

    private static ConversionRun EmptyRun() =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "fixture",
            new PostgreSqlVersion(18),
            new ConversionOptions(),
            [],
            [],
            [],
            [],
            [],
            "test");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>, IDisposable
    {
        public void Report(T value) => report(value);
        public void Dispose() { }
    }

    private sealed class CancellingReportWriter(CancellationTokenSource cancellation)
        : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string reportsDirectory,
            CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(reportsDirectory);
            File.WriteAllText(Path.Combine(reportsDirectory, "partial.txt"), "partial");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class EmptyReportWriter : IConversionReportWriter
    {
        public Task WriteAsync(
            ConversionRun run,
            string reportsDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public ApplicationSettings Current { get; } = new();
        public event EventHandler<ApplicationSettings>? SettingsChanged;
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken)
        {
            SettingsChanged?.Invoke(this, settings);
            return Task.CompletedTask;
        }
    }
}
