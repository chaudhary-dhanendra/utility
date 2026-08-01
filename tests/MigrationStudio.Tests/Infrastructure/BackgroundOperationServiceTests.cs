using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Settings;
using MigrationStudio.Domain.Operations;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Operations;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class BackgroundOperationServiceTests
{
    [Fact]
    public async Task EnqueuedOperation_ReportsProgressAndCompletes()
    {
        var monitor = new OperationMonitor();
        var completion = new TaskCompletionSource<OperationSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) =>
        {
            var terminal = monitor.Operations.FirstOrDefault(
                operation => operation.State is OperationState.Completed or OperationState.Failed);
            if (terminal is not null)
            {
                completion.TrySetResult(terminal);
            }
        };

        using var service = new BackgroundOperationService(
            Options.Create(new InfrastructureOptions { OperationQueueCapacity = 4 }),
            new TestSettingsService(),
            monitor,
            NullLogger<BackgroundOperationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.EnqueueAsync(
            new BackgroundOperationDefinition(
                "Test operation",
                (context, _) =>
                {
                    context.Report(new OperationProgress(50, "Halfway", 1, 2));
                    return ValueTask.CompletedTask;
                }));

        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(OperationState.Completed, result.State);
        Assert.Equal(100, result.Progress.Percentage);
        Assert.Equal("Test operation", result.Name);
    }

    [Fact]
    public async Task DuplicateActiveDeduplicationKey_IsRejected()
    {
        var monitor = new OperationMonitor();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(monitor);
        await service.StartAsync(CancellationToken.None);

        await service.EnqueueAsync(new BackgroundOperationDefinition(
            "Discovery",
            async (_, cancellationToken) =>
                await release.Task.WaitAsync(cancellationToken),
            "sqlserver-discovery:source:database"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.EnqueueAsync(new BackgroundOperationDefinition(
                "Duplicate discovery",
                (_, _) => ValueTask.CompletedTask,
                "sqlserver-discovery:source:database")));

        release.TrySetResult();
        await WaitForStateAsync(monitor, OperationState.Completed);
        await service.StopAsync(CancellationToken.None);
        Assert.Contains("already active", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_ReportsCancellingUntilDelegateReleasesResources()
    {
        var monitor = new OperationMonitor();
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = CreateService(monitor);
        await service.StartAsync(CancellationToken.None);
        var id = await service.EnqueueAsync(new BackgroundOperationDefinition(
            "Discovery",
            async (_, cancellationToken) =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    await release.Task;
                    throw;
                }
            }));

        await WaitForStateAsync(monitor, OperationState.Running);
        Assert.True(service.Cancel(id));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(OperationState.Cancelling, monitor.Operations.Single().State);

        release.TrySetResult();
        await WaitForStateAsync(monitor, OperationState.Cancelled);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SourceFailure_PreservesActionableStageAndQuery()
    {
        var monitor = new OperationMonitor();
        var correlationId = Guid.NewGuid();
        using var service = CreateService(monitor);
        await service.StartAsync(CancellationToken.None);
        await service.EnqueueAsync(new BackgroundOperationDefinition(
            "Discovery",
            (_, _) => ValueTask.FromException(new SourceDatabaseException(
                "Objects failed.",
                [new SqlServerError(229, 14, 1, "Permission denied.", null, 1)],
                new InvalidOperationException("Permission denied."),
                DiscoveryStage.DiscoveringObjects,
                "SQLSERVER.OBJECTS.V16",
                correlationId,
                false,
                "Grant VIEW DEFINITION."))));

        var failed = await WaitForStateAsync(monitor, OperationState.Failed);
        await service.StopAsync(CancellationToken.None);

        Assert.NotNull(failed.Failure);
        Assert.Equal("DiscoveringObjects", failed.Failure.Stage);
        Assert.Equal("SQLSERVER.OBJECTS.V16", failed.Failure.QueryId);
        Assert.Equal("229", failed.Failure.ErrorCode);
        Assert.Equal(correlationId.ToString("N"), failed.Failure.CorrelationId);
        Assert.Contains("Grant VIEW DEFINITION", failed.Failure.Remediation, StringComparison.Ordinal);
        Assert.NotEqual("Failed", failed.Progress.Message);
    }

    private static BackgroundOperationService CreateService(OperationMonitor monitor) =>
        new(
            Options.Create(new InfrastructureOptions { OperationQueueCapacity = 4 }),
            new TestSettingsService(),
            monitor,
            NullLogger<BackgroundOperationService>.Instance);

    private static async Task<OperationSnapshot> WaitForStateAsync(
        OperationMonitor monitor,
        OperationState state)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var operation = monitor.Operations.FirstOrDefault(item => item.State == state);
            if (operation is not null)
            {
                return operation;
            }
            await Task.Delay(10);
        }
        throw new TimeoutException($"Operation did not reach {state}.");
    }

    private sealed class TestSettingsService : ISettingsService
    {
        public ApplicationSettings Current { get; } = new() { MaximumConcurrentOperations = 1 };

        public event EventHandler<ApplicationSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
