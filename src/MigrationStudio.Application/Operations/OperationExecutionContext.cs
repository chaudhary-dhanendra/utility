using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Application.Operations;

public sealed class OperationExecutionContext
{
    private readonly Action<OperationProgress> _report;

    internal OperationExecutionContext(OperationId operationId, Action<OperationProgress> report)
    {
        OperationId = operationId;
        _report = report;
    }

    public OperationId OperationId { get; }

    public void Report(OperationProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _report(progress);
    }
}
