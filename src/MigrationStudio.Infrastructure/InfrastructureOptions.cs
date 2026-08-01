namespace MigrationStudio.Infrastructure;

public sealed class InfrastructureOptions
{
    public const string SectionName = "Infrastructure";

    public int OperationQueueCapacity { get; set; } = 100;
}
