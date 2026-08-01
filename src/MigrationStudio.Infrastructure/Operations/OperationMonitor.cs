using MigrationStudio.Application.Operations;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Infrastructure.Operations;

public sealed class OperationMonitor : IOperationMonitor
{
    private readonly object _gate = new();
    private readonly Dictionary<OperationId, OperationSnapshot> _operations = [];

    public IReadOnlyList<OperationSnapshot> Operations
    {
        get
        {
            lock (_gate)
            {
                return _operations.Values
                    .OrderByDescending(operation => operation.QueuedAt)
                    .ToArray();
            }
        }
    }

    public OperationSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _operations.Values
                    .Where(operation => operation.IsActive)
                    .OrderByDescending(operation => operation.StartedAt ?? operation.QueuedAt)
                    .FirstOrDefault();
            }
        }
    }

    public event EventHandler? Changed;

    internal void Add(OperationId id, string name, DateTimeOffset queuedAt)
    {
        var progress = new OperationProgress(0, "Queued");
        lock (_gate)
        {
            _operations.Add(id, new OperationSnapshot(id, name, OperationState.Queued, progress, queuedAt));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal void MarkRunning(OperationId id, DateTimeOffset startedAt) =>
        Update(id, current => current with
        {
            State = OperationState.Running,
            StartedAt = startedAt,
            Progress = new OperationProgress(current.Progress.Percentage, "Running")
        });

    internal void Report(OperationId id, OperationProgress progress) =>
        Update(id, current => current with { Progress = progress });

    internal void Complete(OperationId id, DateTimeOffset finishedAt) =>
        Update(id, current => current with
        {
            State = OperationState.Completed,
            FinishedAt = finishedAt,
            Progress = new OperationProgress(100, "Completed", current.Progress.TotalUnits, current.Progress.TotalUnits)
        });

    internal void Cancel(OperationId id, DateTimeOffset finishedAt) =>
        Update(id, current => current with
        {
            State = OperationState.Cancelled,
            FinishedAt = finishedAt,
            Progress = new OperationProgress(current.Progress.Percentage, "Cancelled")
        });

    internal void MarkCancelling(OperationId id) =>
        Update(id, current => current with
        {
            State = OperationState.Cancelling,
            Progress = new OperationProgress(current.Progress.Percentage, "Cancelling; waiting for resource release.")
        });

    internal void Fail(
        OperationId id,
        DateTimeOffset finishedAt,
        OperationFailureInfo failure) =>
        Update(id, current => current with
        {
            State = OperationState.Failed,
            FinishedAt = finishedAt,
            ErrorMessage = failure.Summary,
            Failure = failure,
            Progress = new OperationProgress(current.Progress.Percentage, failure.Summary)
        });

    private void Update(OperationId id, Func<OperationSnapshot, OperationSnapshot> update)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var current))
            {
                return;
            }

            _operations[id] = update(current);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
