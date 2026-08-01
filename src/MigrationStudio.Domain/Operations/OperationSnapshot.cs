namespace MigrationStudio.Domain.Operations;

public sealed record OperationFailureInfo(
    string Summary,
    string? Stage,
    string? QueryId,
    string? ErrorCode,
    string? Details,
    string? Remediation,
    string? CorrelationId,
    bool IsRetryable);

public sealed record OperationSnapshot(
    OperationId Id,
    string Name,
    OperationState State,
    OperationProgress Progress,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null,
    string? ErrorMessage = null,
    OperationFailureInfo? Failure = null)
{
    public bool IsActive => State is OperationState.Queued or OperationState.Running or OperationState.Cancelling;
}
