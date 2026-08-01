using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.DataMigration;

public enum DataMigrationMode
{
    SchemaOnly,
    DataOnly,
    SchemaAndData
}

public enum DataMigrationExecutionMode
{
    Execute,
    Preview,
    ValidationOnly
}

public enum DataTransferStrategy
{
    PostgreSqlBinaryCopy,
    PostgreSqlTextCopy,
    ParameterizedBatchInsert,
    CustomTransformer
}

public enum ParallelismMode
{
    Sequential,
    Fixed,
    Adaptive
}

public enum TableLoadOrderingStrategy
{
    ParentFirst,
    ForeignKeysAfterData,
    DeferredConstraints,
    CycleGroupsAfterData,
    AdministratorDefined
}

public enum DataConsistencyMode
{
    BestEffort,
    ReadCommitted,
    SnapshotWhereAvailable,
    DatabaseSnapshotConfiguredExternally,
    SourceQuiesced
}

public enum TargetPreparationStrategy
{
    FailIfNotEmpty,
    Truncate,
    Delete,
    Append,
    Upsert,
    Recreate
}

public enum EncryptedColumnStrategy
{
    CopyCiphertextAsOpaqueData,
    DecryptAndReencryptThroughConfiguredTransformer,
    ExcludeColumn,
    ManualMigration
}

public enum GeneratedColumnLoadStrategy
{
    ExcludeGenerated,
    PopulateFromSource,
    TriggerMaintained,
    ManualMigration
}

public enum ManualColumnPolicy
{
    StopTable,
    SkipColumn
}

public enum MigrationFailurePolicy
{
    FailFast,
    ContinueTables,
    SkipFailedRows
}

public enum TableMigrationState
{
    Pending,
    Running,
    Paused,
    Completed,
    CompletedWithFailures,
    Failed,
    Cancelled,
    Skipped,
    ValidationOnly
}

public enum MigrationRunState
{
    Planned,
    Running,
    Paused,
    Completed,
    CompletedWithFailures,
    Failed,
    Cancelled,
    ValidationOnly
}

public enum FailureDisposition
{
    Retried,
    RowSkipped,
    TableStopped,
    MigrationStopped
}

public enum FailureCategory
{
    TransientSqlServer,
    TransientPostgreSql,
    PermanentDatabase,
    Conversion,
    Configuration,
    Cancellation
}

public enum StreamingExecutionStage
{
    CreateCheckpoint = 1,
    LoadMigrationPlan = 2,
    OpenSqlServerConnection = 3,
    BeginSqlServerTransaction = 4,
    OpenSqlReader = 5,
    ReadSourceSchema = 6,
    ResolvePostgreSqlTable = 7,
    GenerateWritePlan = 8,
    OpenPostgreSqlConnection = 9,
    BeginPostgreSqlTransaction = 10,
    CreatePostgreSqlWriter = 11,
    InitializeCopy = 12,
    ReadFirstRow = 13,
    ConvertFirstRow = 14,
    WriteFirstRow = 15,
    FlushFirstBatch = 16,
    Commit = 17
}

public enum StreamingStageOutcome
{
    Running,
    Succeeded,
    Failed,
    Cancelled
}

public enum ChecksumMode
{
    None,
    WholeTable,
    Chunk,
    PrimaryKeyRange,
    Sample
}

public enum ValidationOutcome
{
    NotRun,
    Passed,
    Warning,
    Failed,
    Inconclusive
}

public enum DataTransportKind
{
    Boolean,
    Signed16,
    Signed32,
    Signed64,
    ExactNumeric,
    Floating32,
    Floating64,
    Date,
    Time,
    DateTime,
    DateTimeOffset,
    Text,
    Binary,
    Uuid,
    Xml,
    Json,
    Spatial,
    Opaque
}

public sealed record SensitiveDataOptions
{
    public static IReadOnlyList<string> DefaultPatterns { get; } =
    [
        "password", "passwd", "pwd", "passcode", "passwordhash", "password_hash",
        "salt", "pin", "secret", "token", "refresh_token", "access_token",
        "credential", "api_key", "apikey", "access_key", "private_key", "encryption_key"
    ];

    public IReadOnlyList<string> NamePatterns { get; init; } = DefaultPatterns;

    public bool InspectMetadata { get; init; } = true;

    public bool MaskFailedRows { get; init; } = true;

    public bool EnableFailedRowExport { get; init; }

    public bool AllowUnmaskedFailedRowExport { get; init; }
}

public sealed record ValidationOptions
{
    public bool CompareRowCounts { get; init; } = true;

    public bool CompareNullCounts { get; init; }

    public bool CompareMinMax { get; init; }

    public IReadOnlyList<string> AggregateColumns { get; init; } = [];

    public ChecksumMode ChecksumMode { get; init; }

    public int SampleSize { get; init; } = 1_000;
}

public sealed record DataMigrationOptions
{
    public DataMigrationMode MigrationMode { get; init; } = DataMigrationMode.SchemaAndData;

    public DataMigrationExecutionMode ExecutionMode { get; init; } = DataMigrationExecutionMode.Execute;

    public ParallelismMode ParallelismMode { get; init; } = ParallelismMode.Adaptive;

    public int MaximumConcurrentTables { get; init; } = 4;

    public int MaximumConcurrentReaders { get; init; } = 4;

    public int MaximumConcurrentWriters { get; init; } = 4;

    public int FetchSize { get; init; } = 2_000;

    public int BatchRowCount { get; init; } = 5_000;

    public long BatchByteSize { get; init; } = 32L * 1024 * 1024;

    public long MaximumRowSize { get; init; } = 256L * 1024 * 1024;

    public long LargeRowWarningThreshold { get; init; } = 16L * 1024 * 1024;

    public int CommandTimeoutSeconds { get; init; } = 300;

    public int RetryCount { get; init; } = 3;

    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public int MaximumFailedRows { get; init; } = 100;

    public MigrationFailurePolicy FailurePolicy { get; init; } = MigrationFailurePolicy.FailFast;

    public int CommitInterval { get; init; } = 5_000;

    public int CheckpointInterval { get; init; } = 5_000;

    public TableLoadOrderingStrategy LoadOrdering { get; init; } =
        TableLoadOrderingStrategy.ForeignKeysAfterData;

    public DataConsistencyMode ConsistencyMode { get; init; } =
        DataConsistencyMode.SnapshotWhereAvailable;

    public TargetPreparationStrategy TargetPreparation { get; init; } =
        TargetPreparationStrategy.FailIfNotEmpty;

    public ManualColumnPolicy ManualColumnPolicy { get; init; } = ManualColumnPolicy.StopTable;

    public SensitiveDataOptions SensitiveData { get; init; } = new();

    public ValidationOptions Validation { get; init; } = new();

    public IReadOnlyList<TableMigrationOverride> TableOverrides { get; init; } = [];

    public IReadOnlyList<InventoryObjectId> ExplicitTableOrder { get; init; } = [];

    public bool IsDestructiveTargetPreparationConfirmed { get; init; }

    public DataMigrationOptions Validate()
    {
        if (MaximumConcurrentTables < 1 || MaximumConcurrentReaders < 1 || MaximumConcurrentWriters < 1)
        {
            throw new InvalidOperationException("All concurrency limits must be positive.");
        }

        if (BatchRowCount < 1 || CommitInterval < 1 || CheckpointInterval < 1 ||
            BatchByteSize < 1 || MaximumRowSize < 1)
        {
            throw new InvalidOperationException("Batch, commit, checkpoint and row-size limits must be positive.");
        }

        if (TargetPreparation is TargetPreparationStrategy.Truncate
            or TargetPreparationStrategy.Delete
            or TargetPreparationStrategy.Recreate &&
            !IsDestructiveTargetPreparationConfirmed)
        {
            throw new InvalidOperationException("Destructive target preparation requires explicit confirmation.");
        }

        if (SensitiveData.AllowUnmaskedFailedRowExport && !SensitiveData.EnableFailedRowExport)
        {
            throw new InvalidOperationException("Unmasked export cannot be enabled while failed-row export is disabled.");
        }

        return this;
    }
}

public sealed record TableMigrationOverride(
    InventoryObjectId TableId,
    bool IsExcluded = false,
    IReadOnlyList<string>? IncludedColumns = null,
    string? SourcePredicate = null,
    DataTransferStrategy? TransferStrategy = null,
    TargetPreparationStrategy? TargetPreparation = null,
    int? BatchRowCount = null,
    int? CommandTimeoutSeconds = null,
    string? StableResumeKey = null,
    int? AdministratorOrder = null,
    string? TransformerId = null);

public sealed record ColumnMapping(
    int Ordinal,
    string SourceName,
    string TargetName,
    string SourceType,
    string TargetType,
    DataTransportKind TransportKind,
    bool IsNullable,
    bool IsSensitive,
    bool IsIdentity,
    decimal? IdentitySeed,
    decimal? IdentityIncrement,
    GeneratedColumnLoadStrategy GeneratedStrategy,
    EncryptedColumnStrategy? EncryptionStrategy,
    bool IsIncluded,
    string? TransformerId = null);

public sealed record TableLoadPlan(
    InventoryObjectId SourceTableId,
    string SourceSchema,
    string SourceTable,
    string TargetSchema,
    string TargetTable,
    long EstimatedRows,
    IReadOnlyList<ColumnMapping> Columns,
    IReadOnlyList<string> PrimaryKeyColumns,
    string? StableResumeKey,
    string? SourcePredicate,
    DataTransferStrategy TransferStrategy,
    TargetPreparationStrategy TargetPreparation,
    bool IsResumable,
    bool CanPartition,
    int LoadOrder,
    int DependencyGroup,
    bool HasSensitiveColumns,
    bool RequiresManualAction,
    string? ManualReason,
    string MetadataHash)
{
    public string SourceQualifiedName => $"{SourceSchema}.{SourceTable}";

    public string TargetQualifiedName => $"{TargetSchema}.{TargetTable}";
}

public sealed record DataMigrationPlan(
    Guid RunId,
    DateTimeOffset CreatedAt,
    string SourceDatabaseIdentity,
    string TargetDatabaseIdentity,
    string SourceMetadataHash,
    string ConfigurationHash,
    DataMigrationOptions Options,
    IReadOnlyList<TableLoadPlan> Tables,
    IReadOnlyList<string> Warnings,
    string ApplicationVersion)
{
    public IReadOnlyList<MigrationStudio.Domain.Conversion.IdentifierMappingEntry>
        RecoveredIdentifierMappings { get; init; } = [];
}

public sealed record TableCheckpoint(
    InventoryObjectId TableId,
    string SourceQualifiedName,
    string TargetQualifiedName,
    DataTransferStrategy Strategy,
    long LastCompletedBatch,
    string? LastStableKeyCanonical,
    long RowsRead,
    long RowsWritten,
    long RowsRejected,
    string? ChecksumState,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    TableMigrationState State,
    bool IsResumable);

public sealed record MigrationCheckpoint(
    int FormatVersion,
    Guid RunId,
    string SourceDatabaseIdentity,
    string SourceMetadataHash,
    string TargetDatabaseIdentity,
    string ConfigurationHash,
    string ApplicationVersion,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<TableCheckpoint> Tables)
{
    public const int CurrentFormatVersion = 1;
}

public sealed record MigrationFailure(
    string Table,
    long Batch,
    long? RowOrdinal,
    string? SafeSourceKey,
    string? TargetColumn,
    string? SourceDataType,
    string? TargetDataType,
    string? SqlState,
    string SanitizedMessage,
    int RetryCount,
    FailureCategory Category,
    FailureDisposition Disposition);

public sealed record TableMigrationMetrics(
    InventoryObjectId TableId,
    string Table,
    TableMigrationState State,
    long RowsRead,
    long RowsWritten,
    long RowsRejected,
    long BytesTransferred,
    TimeSpan ReadDuration,
    TimeSpan WriteDuration,
    TimeSpan ValidationDuration,
    TimeSpan TotalDuration,
    int RetryCount,
    int FailureCount,
    int EffectiveParallelism,
    long PeakManagedMemoryBytes,
    double RowsPerSecond,
    double BytesPerSecond,
    string? Message);

public sealed record SequenceResetResult(
    string Table,
    string Column,
    decimal? SourceMaximum,
    decimal? TargetMaximum,
    decimal RestartValue,
    decimal Increment,
    string Script);

public sealed record ColumnValidationResult(
    string Column,
    long? SourceNullCount,
    long? TargetNullCount,
    string? SourceMinimum,
    string? TargetMinimum,
    string? SourceMaximum,
    string? TargetMaximum,
    string? SourceAggregate,
    string? TargetAggregate,
    ValidationOutcome Outcome,
    string? Message);

public sealed record TableValidationResult(
    string Table,
    long? SourceRowCount,
    long? TargetRowCount,
    string? SourceChecksum,
    string? TargetChecksum,
    IReadOnlyList<ColumnValidationResult> Columns,
    ValidationOutcome Outcome,
    TimeSpan Duration,
    string? Message);

public sealed record DataMigrationTargetReadiness(
    int ExpectedSchemas,
    int ExistingSchemas,
    int MissingSchemas,
    int ExpectedTables,
    int ExistingTables,
    int MissingTables,
    int ExpectedColumns,
    int ExistingColumns,
    int MissingColumns,
    IReadOnlyList<string> MissingSchemaNames,
    IReadOnlyList<string> MissingTableNames,
    IReadOnlyList<string> MissingColumnNames)
{
    public bool IsReady =>
        MissingSchemas == 0 && MissingTables == 0 && MissingColumns == 0;
}

public sealed class DataMigrationTargetReadinessException(
    DataMigrationTargetReadiness readiness)
    : InvalidOperationException(BuildMessage(readiness))
{
    public DataMigrationTargetReadiness Readiness { get; } = readiness;

    private static string BuildMessage(DataMigrationTargetReadiness readiness)
    {
        var examples = readiness.MissingTableNames
            .Concat(readiness.MissingColumnNames)
            .Take(8)
            .ToArray();
        return $"PostgreSQL target readiness failed: {readiness.MissingSchemas:N0} missing schemas, " +
            $"{readiness.MissingTables:N0} missing tables, and {readiness.MissingColumns:N0} missing columns." +
            (examples.Length == 0 ? string.Empty : $" Missing examples: {string.Join(", ", examples)}.") +
            " Deploy or repair the target schema before starting data migration.";
    }
}

public sealed record DataMigrationProgress(
    Guid RunId,
    InventoryObjectId? TableId,
    string Stage,
    string Message,
    long RowsRead,
    long RowsWritten,
    long RowsRejected,
    long EstimatedRows,
    long CurrentBatch,
    int RetryCount,
    int ActiveReaders,
    int ActiveWriters,
    int ActiveTables,
    double RowsPerSecond,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TableMigrationState? TableState)
{
    public double Percentage => EstimatedRows <= 0
        ? 0
        : Math.Clamp(RowsWritten * 100d / EstimatedRows, 0, 100);

    public StreamingExecutionStage? StreamingStage { get; init; }

    public string? CurrentTable { get; init; }

    public string? CurrentReader { get; init; }

    public string? CurrentWriter { get; init; }

    public StreamingExecutionStage? LastSuccessfulStage { get; init; }

    public StreamingExecutionStage? FailureStage { get; init; }

    public string? FailureComponent { get; init; }

    public string? FailureReason { get; init; }

    public string? Remediation { get; init; }
}

public sealed record StreamingStageDiagnostic(
    Guid ExecutionId,
    StreamingExecutionStage Stage,
    StreamingStageOutcome Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    long ElapsedMilliseconds,
    string? SourceSchema,
    string? SourceTable,
    string? TargetTable,
    long CurrentBatch,
    long RowsRead,
    long RowsWritten,
    string? CurrentReader,
    string? CurrentWriter,
    string? SqlServerQuery,
    string? PostgreSqlQuery,
    string? CopySql,
    string? InsertSql,
    string? SqlState,
    string? FailureComponent,
    string? FailureReason,
    string? Remediation,
    string? ExceptionType,
    string? InnerException,
    string? StackTrace);

public sealed record DataMigrationResult(
    Guid RunId,
    MigrationRunState State,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<TableMigrationMetrics> Tables,
    IReadOnlyList<MigrationFailure> Failures,
    IReadOnlyList<TableValidationResult> Validations,
    IReadOnlyList<SequenceResetResult> SequenceResets,
    string CheckpointPath,
    int EffectiveTableParallelism,
    int PeakReaderConnections,
    int PeakWriterConnections,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<StreamingStageDiagnostic> StreamingStages { get; init; } = [];
}
