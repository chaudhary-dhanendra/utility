using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Application.Conversion;

public sealed class ConversionStalledException : TimeoutException
{
    public ConversionStalledException(
        ConversionStage stage,
        long processed,
        long total,
        string currentObject,
        DateTimeOffset lastProgressAt,
        Guid mappingSetId,
        OperationId operationId,
        string diagnosticFilePath)
        : base(
            $"Conversion made no progress for 60 seconds during {stage}. " +
            $"Processed {processed:N0}/{total:N0}; current object '{currentObject}'. " +
            $"Diagnostic: {diagnosticFilePath}")
    {
        Stage = stage;
        Processed = processed;
        Total = total;
        CurrentObject = currentObject;
        LastProgressAt = lastProgressAt;
        MappingSetId = mappingSetId;
        OperationId = operationId;
        DiagnosticFilePath = diagnosticFilePath;
    }

    public ConversionStage Stage { get; }
    public long Processed { get; }
    public long Total { get; }
    public string CurrentObject { get; }
    public DateTimeOffset LastProgressAt { get; }
    public Guid MappingSetId { get; }
    public OperationId OperationId { get; }
    public string DiagnosticFilePath { get; }
}
