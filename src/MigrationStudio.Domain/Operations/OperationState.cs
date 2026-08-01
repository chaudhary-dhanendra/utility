namespace MigrationStudio.Domain.Operations;

public enum OperationState
{
    Queued,
    Running,
    Cancelling,
    Completed,
    Failed,
    Cancelled
}
