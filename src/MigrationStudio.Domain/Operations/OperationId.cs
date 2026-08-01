namespace MigrationStudio.Domain.Operations;

public readonly record struct OperationId(Guid Value)
{
    public static OperationId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}
