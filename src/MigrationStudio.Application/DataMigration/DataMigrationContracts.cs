using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.DataMigration;

public sealed record DataMigrationRequest(
    InventorySnapshot Inventory,
    ConversionRun Conversion,
    SqlServerConnectionOptions SourceConnection,
    string TargetConnectionString,
    DataMigrationOptions Options,
    Guid? ResumeRunId = null,
    IReadOnlySet<InventoryObjectId>? SelectedTables = null);

public sealed record RowTransformationContext(
    Guid RunId,
    TableLoadPlan Table,
    ColumnMapping Column,
    long RowOrdinal,
    IReadOnlyDictionary<string, object?> SafeMetadata);

public interface IDataValueTransformer
{
    bool CanTransform(ColumnMapping mapping);

    ValueTask<object?> TransformAsync(
        object? sourceValue,
        RowTransformationContext context,
        CancellationToken cancellationToken);
}

public interface IDataMigrationPlanner
{
    DataMigrationPlan CreatePlan(DataMigrationRequest request);
}

public interface IDataMigrationEngine
{
    Task<DataMigrationResult> ExecuteAsync(
        DataMigrationRequest request,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken cancellationToken);

    Task<DataMigrationResult> ResumeAsync(
        DataMigrationRequest request,
        IProgress<DataMigrationProgress>? progress,
        CancellationToken cancellationToken);

    Task RestartTableAsync(Guid runId, InventoryObjectId tableId, CancellationToken cancellationToken);

    Task RestartRunAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IDataMigrationSession
{
    DataMigrationPlan? CurrentPlan { get; }

    DataMigrationResult? CurrentResult { get; }

    event EventHandler? Changed;

    void SetPlan(DataMigrationPlan plan);

    void SetResult(DataMigrationResult result);
}

public interface IMigrationCheckpointStore
{
    Task<string> SaveAsync(MigrationCheckpoint checkpoint, CancellationToken cancellationToken);

    Task<MigrationCheckpoint?> LoadAsync(Guid runId, CancellationToken cancellationToken);

    Task DeleteTableAsync(Guid runId, InventoryObjectId tableId, CancellationToken cancellationToken);

    Task DeleteRunAsync(Guid runId, CancellationToken cancellationToken);
}

public interface IDataTransferStrategy
{
    DataTransferStrategy Strategy { get; }

    bool CanExecute(TableLoadPlan table);

    Task<BatchWriteResult> WriteBatchAsync(
        DataTransferContext context,
        IReadOnlyList<DataRowBuffer> rows,
        CancellationToken cancellationToken);
}

public sealed record DataTransferContext(
    Guid RunId,
    TableLoadPlan Table,
    string TargetConnectionString,
    int CommandTimeoutSeconds,
    IReadOnlyList<IDataValueTransformer> Transformers,
    IStreamingStageObserver? StageObserver = null,
    long CurrentBatch = 0,
    long RowsRead = 0,
    long RowsWritten = 0);

public interface IStreamingStageObserver
{
    Guid Start(
        StreamingExecutionStage stage,
        TableLoadPlan? table = null,
        long currentBatch = 0,
        long rowsRead = 0,
        long rowsWritten = 0,
        string? currentReader = null,
        string? currentWriter = null,
        string? sqlServerQuery = null,
        string? postgreSqlQuery = null,
        string? copySql = null,
        string? insertSql = null);

    void Succeed(Guid executionId);

    void Fail(Guid executionId, Exception exception);

    IReadOnlyList<StreamingStageDiagnostic> Snapshot();
}

public sealed record DataRowBuffer(
    long Ordinal,
    IReadOnlyList<object?> Values,
    long ApproximateBytes,
    string? StableKeyCanonical);

public sealed record BatchWriteResult(long RowsWritten, long BytesWritten, TimeSpan Duration);

public interface IDataMigrationValidator
{
    Task<TableValidationResult> ValidateAsync(
        DataMigrationRequest request,
        TableLoadPlan table,
        CancellationToken cancellationToken);
}

public interface ISequenceResetService
{
    Task<IReadOnlyList<SequenceResetResult>> ResetAsync(
        DataMigrationRequest request,
        IReadOnlyList<TableMigrationMetrics> completedTables,
        CancellationToken cancellationToken);
}

public interface IFailedRowExporter
{
    Task<string> ExportJsonAsync(
        Guid runId,
        IReadOnlyList<FailedRowRecord> rows,
        bool includeUnmaskedSensitiveValues,
        CancellationToken cancellationToken);

    Task<string> ExportCsvAsync(
        Guid runId,
        IReadOnlyList<FailedRowRecord> rows,
        bool includeUnmaskedSensitiveValues,
        CancellationToken cancellationToken);
}

public interface IDataMigrationReportWriter
{
    Task WriteAsync(
        DataMigrationResult result,
        string reportsDirectory,
        CancellationToken cancellationToken);
}

public sealed record FailedRowRecord(
    string Table,
    string? SafeKey,
    IReadOnlyDictionary<string, FailedRowValue> Values,
    string ErrorReason);

public sealed record FailedRowValue(object? Value, bool IsSensitive, bool IsBinary);

public interface ISensitiveColumnClassifier
{
    bool IsSensitive(ColumnInventory column, SensitiveDataOptions options);
}

public interface ICanonicalValueFormatter
{
    string Format(object? value, DataTransportKind kind);

    string ComputeRowHash(IReadOnlyList<(object? Value, DataTransportKind Kind)> values);
}

public interface ITransientErrorClassifier
{
    FailureCategory Classify(Exception exception);

    bool IsTransient(Exception exception);
}

public interface IMigrationPauseController
{
    bool IsPaused { get; }

    void Pause();

    void Unpause();

    Task WaitIfPausedAsync(CancellationToken cancellationToken);
}
