using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Application.Operations;

public sealed class BackgroundOperationDefinition
{
    public BackgroundOperationDefinition(
        string name,
        Func<OperationExecutionContext, CancellationToken, ValueTask> executeAsync,
        string? deduplicationKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(executeAsync);

        Id = OperationId.New();
        Name = name.Trim();
        ExecuteAsync = executeAsync;
        DeduplicationKey = string.IsNullOrWhiteSpace(deduplicationKey)
            ? null
            : deduplicationKey.Trim();
    }

    public OperationId Id { get; }

    public string Name { get; }

    public string? DeduplicationKey { get; }

    internal Func<OperationExecutionContext, CancellationToken, ValueTask> ExecuteAsync { get; }
}
