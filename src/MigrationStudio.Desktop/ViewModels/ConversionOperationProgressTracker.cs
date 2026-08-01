using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Desktop.ViewModels;

internal sealed partial class ConversionOperationProgressTracker : IDisposable
{
    private const int RollingCapacity = 100;
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly object _gate = new();
    private readonly OperationId _operationId;
    private readonly Action<OperationProgress> _publish;
    private readonly Action<ConversionProgressSnapshot> _present;
    private readonly ILogger _logger;
    private readonly string _diagnosticsDirectory;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _staleRateAfter;
    private readonly TimeSpan _unresponsiveAfter;
    private readonly TimeSpan _diagnosticAfter;
    private readonly TimeSpan _failAfter;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Queue<ConversionProgressSnapshot> _rolling = new(RollingCapacity);
    private ConversionProgressSnapshot? _current;
    private DateTimeOffset _lastWorkerActivityAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastProcessedChangeAt = DateTimeOffset.UtcNow;
    private ConversionStage _lastStage;
    private long _lastProcessed = -1;
    private string? _diagnosticPath;

    public ConversionOperationProgressTracker(
        OperationId operationId,
        Action<OperationProgress> publish,
        Action<ConversionProgressSnapshot> present,
        ILogger logger,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? staleRateAfter = null,
        TimeSpan? unresponsiveAfter = null,
        TimeSpan? diagnosticAfter = null,
        TimeSpan? failAfter = null,
        string? diagnosticsDirectory = null)
    {
        _operationId = operationId;
        _publish = publish;
        _present = present;
        _logger = logger;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(1);
        _staleRateAfter = staleRateAfter ?? TimeSpan.FromSeconds(2);
        _unresponsiveAfter = unresponsiveAfter ?? TimeSpan.FromSeconds(15);
        _diagnosticAfter = diagnosticAfter ?? TimeSpan.FromSeconds(30);
        _failAfter = failAfter ?? TimeSpan.FromSeconds(60);
        _diagnosticsDirectory = diagnosticsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MigrationStudio",
            "Diagnostics");
    }

    public IProgress<ConversionProgress> Progress { get; private set; } = null!;

    public void Initialize() => Progress = new SynchronousProgress<ConversionProgress>(Report);

    public async Task<T> RunAsync<T>(
        Func<IProgress<ConversionProgress>, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Initialize();
        using var workerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = operation(Progress, workerCancellation.Token);
        var watchdog = WatchAsync(workerCancellation.Token);
        var completed = await Task.WhenAny(worker, watchdog).ConfigureAwait(false);
        if (completed == worker)
        {
            workerCancellation.Cancel();
            try
            {
                await watchdog.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
            {
                // Expected when normal completion stops the watchdog.
            }
            return await worker.ConfigureAwait(false);
        }

        try
        {
            await watchdog.ConfigureAwait(false);
            throw new InvalidOperationException("The conversion watchdog ended unexpectedly.");
        }
        catch
        {
            workerCancellation.Cancel();
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (workerCancellation.IsCancellationRequested)
            {
                // The watchdog owns the failure that will be propagated below.
            }
            throw;
        }
    }

    private void Report(ConversionProgress progress)
    {
        ConversionProgressSnapshot snapshot;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _lastWorkerActivityAt = now;
            if (progress.Stage != _lastStage || progress.CompletedObjects != _lastProcessed)
            {
                _lastProcessedChangeAt = now;
                _lastStage = progress.Stage;
                _lastProcessed = progress.CompletedObjects;
            }
            snapshot = CreateSnapshot(progress, now, isResponsive: true);
            _current = snapshot;
            AddRolling(snapshot);
        }
        Publish(snapshot);
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(_heartbeatInterval, cancellationToken).ConfigureAwait(false);
            ConversionProgressSnapshot? heartbeat;
            TimeSpan idle;
            lock (_gate)
            {
                if (_current is null)
                {
                    continue;
                }
                var now = DateTimeOffset.UtcNow;
                idle = now - _lastWorkerActivityAt;
                var processedIdle = now - _lastProcessedChangeAt;
                heartbeat = _current with
                {
                    Elapsed = _elapsed.Elapsed,
                    RatePerSecond = processedIdle >= _staleRateAfter
                        ? 0
                        : _current.RatePerSecond,
                    EstimatedRemaining = processedIdle >= _staleRateAfter
                        ? null
                        : _current.EstimatedRemaining,
                    IsResponsive = idle < _unresponsiveAfter,
                    Message = idle >= _unresponsiveAfter
                        ? $"No progress for {Math.Floor(idle.TotalSeconds):N0} seconds · " +
                          $"{_current.Stage} · {_current.Processed:N0}/{_current.Total:N0}"
                        : _current.Message
                };
                _current = heartbeat;
                AddRolling(heartbeat);
            }
            Publish(heartbeat);

            if (idle >= _diagnosticAfter && _diagnosticPath is null)
            {
                _diagnosticPath = WriteDiagnostic(heartbeat, idle);
            }
            if (idle >= _failAfter)
            {
                _diagnosticPath ??= WriteDiagnostic(heartbeat, idle);
                throw new ConversionStalledException(
                    heartbeat.Stage,
                    heartbeat.Processed,
                    heartbeat.Total,
                    heartbeat.CurrentObject,
                    heartbeat.LastProgressAt,
                    heartbeat.MappingSetId,
                    heartbeat.OperationId,
                    _diagnosticPath);
            }
        }
    }

    private ConversionProgressSnapshot CreateSnapshot(
        ConversionProgress progress,
        DateTimeOffset now,
        bool isResponsive)
    {
        var rate = progress.ObjectsPerSecond;
        if (rate <= 0 && _elapsed.Elapsed.TotalSeconds > 0)
        {
            rate = progress.CompletedObjects / _elapsed.Elapsed.TotalSeconds;
        }
        TimeSpan? remaining = rate > 0 && progress.TotalObjects > progress.CompletedObjects
            ? TimeSpan.FromSeconds((progress.TotalObjects - progress.CompletedObjects) / rate)
            : null;
        return new ConversionProgressSnapshot(
            _operationId,
            progress.MappingSetId,
            progress.Stage,
            (int)progress.Stage,
            Enum.GetValues<ConversionStage>().Length,
            progress.CompletedObjects,
            progress.TotalObjects,
            progress.Percentage,
            progress.CurrentObjectType,
            progress.CurrentObject,
            _startedAt,
            progress.LastProgressAt == default ? now : progress.LastProgressAt,
            _elapsed.Elapsed,
            rate,
            remaining,
            isResponsive,
            progress.Message);
    }

    private void Publish(ConversionProgressSnapshot snapshot)
    {
        _publish(new OperationProgress(
            snapshot.Percent,
            snapshot.Message,
            snapshot.Processed,
            snapshot.Total,
            snapshot));
        _present(snapshot);
    }

    private void AddRolling(ConversionProgressSnapshot snapshot)
    {
        if (_rolling.Count == RollingCapacity)
        {
            _rolling.Dequeue();
        }
        _rolling.Enqueue(snapshot);
    }

    private string WriteDiagnostic(ConversionProgressSnapshot snapshot, TimeSpan idle)
    {
        Directory.CreateDirectory(_diagnosticsDirectory);
        var path = Path.Combine(
            _diagnosticsDirectory,
            $"conversion-stall-{_operationId}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        ConversionProgressSnapshot[] rolling;
        lock (_gate)
        {
            rolling = _rolling.ToArray();
        }
        var diagnostic = new
        {
            ApplicationVersion = typeof(ConversionOperationProgressTracker).Assembly
                .GetName().Version?.ToString(),
            ProcessId = Environment.ProcessId,
            ThreadId = Environment.CurrentManagedThreadId,
            TaskId = Task.CurrentId,
            OperationId = _operationId.ToString(),
            snapshot.MappingSetId,
            snapshot.Stage,
            snapshot.Processed,
            snapshot.Total,
            snapshot.CurrentObjectType,
            snapshot.CurrentObject,
            snapshot.StartedAt,
            snapshot.LastProgressAt,
            IdleSeconds = idle.TotalSeconds,
            CancellationRequested = false,
            CurrentCallSite = nameof(ConversionOperationProgressTracker),
            RollingProgress = rolling
        };
        File.WriteAllText(path, JsonSerializer.Serialize(diagnostic, DiagnosticJsonOptions));
        LogWatchdogDiagnostic(_logger, path, _operationId.ToString());
        return path;
    }

    [LoggerMessage(
        EventId = 2130,
        Level = LogLevel.Warning,
        Message = "Conversion watchdog captured no-progress diagnostic {DiagnosticPath} for operation {OperationId}.")]
    private static partial void LogWatchdogDiagnostic(
        ILogger logger,
        string diagnosticPath,
        string operationId);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
