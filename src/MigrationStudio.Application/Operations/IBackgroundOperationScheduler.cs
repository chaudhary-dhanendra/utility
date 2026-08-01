using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Application.Operations;

public interface IBackgroundOperationScheduler
{
    ValueTask<OperationId> EnqueueAsync(
        BackgroundOperationDefinition operation,
        CancellationToken cancellationToken = default);

    bool Cancel(OperationId operationId);
}
