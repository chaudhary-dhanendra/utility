using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Application.Operations;

public interface IOperationMonitor
{
    IReadOnlyList<OperationSnapshot> Operations { get; }

    OperationSnapshot? Current { get; }

    event EventHandler? Changed;
}
