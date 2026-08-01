using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Domain.Conversion;

public enum ConversionStage
{
    CollectingIncludedObjects = 1,
    GeneratingIdentifierCandidates = 2,
    ResolvingCollisions = 3,
    ValidatingIdentifiers = 4,
    PublishingIdentifierMap = 5,
    ConvertingObjects = 6,
    OrderingDependencies = 7,
    BuildingDeploymentPackage = 8,
    ValidatingPackage = 9,
    CompletingReports = 10
}

public sealed record ConversionProgressSnapshot(
    OperationId OperationId,
    Guid MappingSetId,
    ConversionStage Stage,
    int StageNumber,
    int StageCount,
    long Processed,
    long Total,
    double Percent,
    string CurrentObjectType,
    string CurrentObject,
    DateTimeOffset StartedAt,
    DateTimeOffset LastProgressAt,
    TimeSpan Elapsed,
    double RatePerSecond,
    TimeSpan? EstimatedRemaining,
    bool IsResponsive,
    string Message);
