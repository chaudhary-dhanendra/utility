using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Reporting;
using MigrationStudio.Application.Validation;
using MigrationStudio.Domain.Reporting;

namespace MigrationStudio.Reporting;

public sealed partial class RunHistoryRecorderService(
    IInventorySession inventory,
    IConversionSession conversion,
    IDataMigrationSession dataMigration,
    IDeploymentSession deployment,
    IValidationSession validation,
    IRunHistoryStore store,
    ILogger<RunHistoryRecorderService> logger) : IHostedService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationToken _stoppingToken;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingToken = cancellationToken;
        inventory.Changed += OnInventoryChanged;
        conversion.Changed += OnConversionChanged;
        dataMigration.Changed += OnDataMigrationChanged;
        deployment.Changed += OnDeploymentChanged;
        validation.Changed += OnValidationChanged;
        await RecordAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        inventory.Changed -= OnInventoryChanged;
        conversion.Changed -= OnConversionChanged;
        dataMigration.Changed -= OnDataMigrationChanged;
        deployment.Changed -= OnDeploymentChanged;
        validation.Changed -= OnValidationChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => _gate.Dispose();

    private void OnInventoryChanged(object? sender, EventArgs args) => QueueRecord(RecordInventoryAsync);

    private void OnConversionChanged(object? sender, EventArgs args) => QueueRecord(RecordConversionAsync);

    private void OnDataMigrationChanged(object? sender, EventArgs args) => QueueRecord(RecordDataMigrationAsync);

    private void OnDeploymentChanged(object? sender, EventArgs args) => QueueRecord(RecordDeploymentAsync);

    private void OnValidationChanged(object? sender, EventArgs args) => QueueRecord(RecordValidationAsync);

    private void QueueRecord(Func<CancellationToken, Task> action)
    {
        _ = RecordSafelyAsync(action, _stoppingToken);
    }

    private async Task RecordAllAsync(CancellationToken cancellationToken)
    {
        await RecordSafelyAsync(RecordInventoryAsync, cancellationToken).ConfigureAwait(false);
        await RecordSafelyAsync(RecordConversionAsync, cancellationToken).ConfigureAwait(false);
        await RecordSafelyAsync(RecordDataMigrationAsync, cancellationToken).ConfigureAwait(false);
        await RecordSafelyAsync(RecordDeploymentAsync, cancellationToken).ConfigureAwait(false);
        await RecordSafelyAsync(RecordValidationAsync, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordSafelyAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await action(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogPersistenceFailure(logger, exception);
        }
    }

    private Task RecordInventoryAsync(CancellationToken cancellationToken)
    {
        var run = inventory.Current;
        if (run is null)
        {
            return Task.CompletedTask;
        }
        var runId = StableGuid(
            $"{run.Database.DatabaseName}|{run.SnapshotTimestamp:O}|{run.DiscoveryEngineVersion}");
        return SaveAsync(
            new RunHistoryEntry(
                runId, RunHistoryKind.Discovery, RunHistoryStatus.Succeeded,
                run.SnapshotTimestamp, run.SnapshotTimestamp, run.Database.DatabaseName, null,
                $"{run.Objects.Count:N0} objects; {run.Findings.Count:N0} findings.",
                string.Empty),
            run,
            cancellationToken);
    }

    private Task RecordConversionAsync(CancellationToken cancellationToken)
    {
        var run = conversion.Current;
        if (run is null)
        {
            return Task.CompletedTask;
        }
        return SaveAsync(
            new RunHistoryEntry(
                run.RunId, RunHistoryKind.Conversion,
                run.RequiresManualReview ? RunHistoryStatus.SucceededWithWarnings : RunHistoryStatus.Succeeded,
                run.GeneratedAt, run.GeneratedAt, run.SourceDatabase, null,
                $"{run.Artifacts.Count:N0} artifacts; {run.Findings.Count:N0} findings.",
                string.Empty),
            run,
            cancellationToken);
    }

    private Task RecordDataMigrationAsync(CancellationToken cancellationToken)
    {
        var run = dataMigration.CurrentResult;
        if (run is null)
        {
            return Task.CompletedTask;
        }
        return SaveAsync(
            new RunHistoryEntry(
                run.RunId, RunHistoryKind.DataMigration, MapState(run.State.ToString()),
                run.StartedAt, run.CompletedAt, inventory.Current?.Database.DatabaseName ?? "Unknown",
                deployment.Result?.TargetDatabase,
                $"{run.Tables.Sum(item => item.RowsWritten):N0} rows written; {run.Failures.Count:N0} failures.",
                string.Empty),
            run,
            cancellationToken);
    }

    private Task RecordDeploymentAsync(CancellationToken cancellationToken)
    {
        var run = deployment.Result;
        if (run is null)
        {
            return Task.CompletedTask;
        }
        return SaveAsync(
            new RunHistoryEntry(
                run.DeploymentId, RunHistoryKind.Deployment, MapState(run.Status.ToString()),
                run.StartedAt, run.CompletedAt, inventory.Current?.Database.DatabaseName ?? "Unknown",
                run.TargetDatabase,
                $"{run.Objects.Count(item => item.Status == Domain.Deployment.DeploymentObjectStatus.Succeeded):N0} objects deployed; {run.Failures.Count:N0} failures.",
                string.Empty),
            run,
            cancellationToken);
    }

    private Task RecordValidationAsync(CancellationToken cancellationToken)
    {
        var run = validation.Current;
        if (run is null)
        {
            return Task.CompletedTask;
        }
        return SaveAsync(
            new RunHistoryEntry(
                run.RunId, RunHistoryKind.Validation,
                run.Readiness.OverallStatus == Domain.Validation.ReadinessStatus.Ready
                    ? RunHistoryStatus.Succeeded
                    : RunHistoryStatus.SucceededWithWarnings,
                run.StartedAt, run.CompletedAt, inventory.Current?.Database.DatabaseName ?? "Unknown",
                run.TargetDatabaseIdentity,
                $"{run.Findings.Count:N0} findings; readiness {run.Readiness.OverallStatus}.",
                string.Empty,
                run.DeploymentRunId?.ToString()),
            run,
            cancellationToken);
    }

    private Task SaveAsync<T>(
        RunHistoryEntry entry,
        T payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(payload);
        return store.SaveAsync(new RunHistoryRecord(entry, json), cancellationToken);
    }

    private static RunHistoryStatus MapState(string value)
    {
        if (value.Contains("Cancel", StringComparison.OrdinalIgnoreCase))
        {
            return RunHistoryStatus.Cancelled;
        }
        if (value.Contains("Fail", StringComparison.OrdinalIgnoreCase))
        {
            return RunHistoryStatus.Failed;
        }
        if (value.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Failures", StringComparison.OrdinalIgnoreCase))
        {
            return RunHistoryStatus.SucceededWithWarnings;
        }
        return RunHistoryStatus.Succeeded;
    }

    private static Guid StableGuid(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return new Guid(hash[..16]);
    }

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Error,
        Message = "Run history could not be persisted.")]
    private static partial void LogPersistenceFailure(
        ILogger logger,
        Exception exception);
}
