using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.Security;
using MigrationStudio.Application.Settings;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Infrastructure.Operations;

public sealed class BackgroundOperationService : BackgroundService, IBackgroundOperationScheduler
{
    private readonly Channel<QueuedOperation> _channel;
    private readonly ConcurrentDictionary<OperationId, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<string, OperationId> _deduplicationKeys =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ISettingsService _settings;
    private readonly OperationMonitor _monitor;
    private readonly ILogger<BackgroundOperationService> _logger;
    private readonly ISensitiveDataRedactor? _redactor;

    public BackgroundOperationService(
        IOptions<InfrastructureOptions> options,
        ISettingsService settings,
        OperationMonitor monitor,
        ILogger<BackgroundOperationService> logger,
        ISensitiveDataRedactor? redactor = null)
    {
        _settings = settings;
        _monitor = monitor;
        _logger = logger;
        _redactor = redactor;
        _channel = Channel.CreateBounded<QueuedOperation>(new BoundedChannelOptions(options.Value.OperationQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public async ValueTask<OperationId> EnqueueAsync(
        BackgroundOperationDefinition operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation.DeduplicationKey is { } deduplicationKey &&
            !_deduplicationKeys.TryAdd(deduplicationKey, operation.Id))
        {
            throw new InvalidOperationException(
                $"An operation with key '{deduplicationKey}' is already active.");
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_cancellations.TryAdd(operation.Id, cancellation))
        {
            if (operation.DeduplicationKey is { } failedKey)
            {
                _deduplicationKeys.TryRemove(failedKey, out _);
            }
            cancellation.Dispose();
            throw new InvalidOperationException($"Operation {operation.Id} is already queued.");
        }

        _monitor.Add(operation.Id, operation.Name, DateTimeOffset.UtcNow);

        try
        {
            await _channel.Writer.WriteAsync(new QueuedOperation(operation, cancellation), cancellationToken)
                .ConfigureAwait(false);
            return operation.Id;
        }
        catch
        {
            _cancellations.TryRemove(operation.Id, out _);
            if (operation.DeduplicationKey is { } failedKey)
            {
                _deduplicationKeys.TryRemove(failedKey, out _);
            }
            cancellation.Dispose();
            throw;
        }
    }

    public bool Cancel(OperationId operationId)
    {
        if (!_cancellations.TryGetValue(operationId, out var cancellation))
        {
            return false;
        }

        _monitor.MarkCancelling(operationId);
        cancellation.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workerCount = _settings.Current.MaximumConcurrentOperations;
        BackgroundOperationLog.WorkersStarting(_logger, workerCount);

        var workers = Enumerable.Range(0, workerCount)
            .Select(workerNumber => RunWorkerAsync(workerNumber, stoppingToken))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerNumber, CancellationToken stoppingToken)
    {
        await foreach (var queued in _channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken,
                queued.Cancellation.Token);
            var operation = queued.Definition;

            try
            {
                _monitor.MarkRunning(operation.Id, DateTimeOffset.UtcNow);
                BackgroundOperationLog.OperationStarted(
                    _logger,
                    workerNumber,
                    operation.Id.ToString(),
                    operation.Name);

                var context = new OperationExecutionContext(
                    operation.Id,
                    progress => _monitor.Report(operation.Id, progress));

                await operation.ExecuteAsync(context, linkedCancellation.Token).ConfigureAwait(false);
                _monitor.Complete(operation.Id, DateTimeOffset.UtcNow);
                BackgroundOperationLog.OperationCompleted(_logger, operation.Id.ToString());
            }
            catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
            {
                _monitor.Cancel(operation.Id, DateTimeOffset.UtcNow);
                BackgroundOperationLog.OperationCancelled(_logger, operation.Id.ToString());
            }
            catch (Exception exception)
            {
                _monitor.Fail(
                    operation.Id,
                    DateTimeOffset.UtcNow,
                    CreateFailure(exception));
                BackgroundOperationLog.OperationFailed(_logger, operation.Id.ToString(), exception);
            }
            finally
            {
                if (_cancellations.TryRemove(operation.Id, out var cancellation))
                {
                    cancellation.Dispose();
                }
                if (operation.DeduplicationKey is { } deduplicationKey)
                {
                    _deduplicationKeys.TryRemove(deduplicationKey, out _);
                }
            }
        }
    }

    public override void Dispose()
    {
        _channel.Writer.TryComplete();
        foreach (var cancellation in _cancellations.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _cancellations.Clear();
        _deduplicationKeys.Clear();
        base.Dispose();
    }

    private OperationFailureInfo CreateFailure(Exception exception)
    {
        if (exception is SourceDatabaseException source)
        {
            var first = source.Errors.Count == 0 ? null : source.Errors[0];
            var summary = Redact(
                $"{source.Stage} failed ({source.QueryId}). " +
                (first is null
                    ? source.Message
                    : $"SQL {first.Number}, state {first.State}, class {first.Class}: {first.Message}"));
            var details = source.Errors.Count == 0
                ? Redact(source.InnerException?.Message ?? source.Message)
                : Redact(string.Join(
                    Environment.NewLine,
                    source.Errors.Select(error =>
                        $"SQL {error.Number}; state {error.State}; class {error.Class}; " +
                        $"procedure {error.Procedure ?? "(none)"}; line {error.LineNumber}: {error.Message}")));
            return new OperationFailureInfo(
                summary,
                source.Stage.ToString(),
                source.QueryId,
                first is null ? source.InnerException?.GetType().Name : first.Number.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                details,
                Redact(source.Remediation),
                source.CorrelationId.ToString("N"),
                source.IsRetryable);
        }

        return new OperationFailureInfo(
            Redact(exception.Message),
            null,
            null,
            exception.GetType().Name,
            Redact(exception.Message),
            "Review the sanitized application log and retry only after correcting the reported cause.",
            null,
            false);
    }

    private string Redact(string? value) =>
        _redactor?.Redact(value) ?? value ?? string.Empty;

    private sealed record QueuedOperation(
        BackgroundOperationDefinition Definition,
        CancellationTokenSource Cancellation);
}

internal static partial class BackgroundOperationLog
{
    [LoggerMessage(1100, LogLevel.Information, "Starting {WorkerCount} background operation workers.")]
    public static partial void WorkersStarting(ILogger logger, int workerCount);

    [LoggerMessage(
        1101,
        LogLevel.Information,
        "Worker {WorkerNumber} started operation {OperationId} ({OperationName}).")]
    public static partial void OperationStarted(
        ILogger logger,
        int workerNumber,
        string operationId,
        string operationName);

    [LoggerMessage(1102, LogLevel.Information, "Operation {OperationId} completed.")]
    public static partial void OperationCompleted(ILogger logger, string operationId);

    [LoggerMessage(1103, LogLevel.Information, "Operation {OperationId} was cancelled.")]
    public static partial void OperationCancelled(ILogger logger, string operationId);

    [LoggerMessage(1104, LogLevel.Error, "Operation {OperationId} failed.")]
    public static partial void OperationFailed(ILogger logger, string operationId, Exception exception);
}
