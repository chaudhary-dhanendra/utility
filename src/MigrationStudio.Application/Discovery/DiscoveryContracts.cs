using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Discovery;

public sealed record DiscoveryOptions
{
    public bool IncludeServerLevelObjects { get; init; }

    public bool IncludeSqlAgent { get; init; }

    public bool IncludeReplication { get; init; } = true;

    public int MaximumConcurrentCommands { get; init; } = 2;

    public int MaximumTransientRetries { get; init; } = 2;

    public int InitialRetryDelayMilliseconds { get; init; } = 250;
}

public enum DiscoveryStage
{
    NotStarted,
    Initializing,
    TestingConnection,
    LoadingServerMetadata,
    LoadingDatabaseMetadata,
    DiscoveringSchemas,
    DiscoveringObjects,
    DiscoveringTables,
    DiscoveringColumns,
    DiscoveringConstraints,
    DiscoveringIndexes,
    DiscoveringProgrammableObjects,
    DiscoveringDependencies,
    DiscoveringExtendedProperties,
    DiscoveringServerTriggers,
    DiscoveringSecurity,
    DiscoveringAdvancedFeatures,
    DiscoveringExternalObjects,
    DiscoveringSqlAgent,
    BuildingDependencyGraph,
    DetectingCycles,
    ClassifyingObjects,
    FinalizingInventory,
    SavingInventorySnapshot,
    Completed,
    Cancelling,
    Cancelled,
    Failed
}

public enum DiscoveryStageState
{
    Pending,
    Running,
    Retrying,
    Completed,
    CompletedWithFindings,
    Cancelled,
    Failed
}

public sealed record InventoryDiscoveryRequest(
    SqlServerConnectionOptions Connection,
    MigrationScopeMode ScopeMode,
    IReadOnlySet<string> SelectedSchemas,
    IReadOnlySet<InventoryObjectId> SelectedObjectIds,
    IReadOnlySet<InventoryObjectId> ExcelMatchedTableIds,
    DependencyPolicy DependencyPolicy,
    DiscoveryOptions Options);

public sealed record DiscoveryProgress(
    DiscoveryStage Stage,
    DiscoveryStageState State,
    string QueryId,
    bool IsRequired,
    int Attempt,
    int StageNumber,
    int StageCount,
    long ObjectsDiscovered,
    string Message,
    DateTimeOffset Timestamp)
{
    public double Percentage => StageCount == 0 ? 0 : Math.Clamp(StageNumber * 100d / StageCount, 0, 100);
}

public sealed record DiscoveryStageDiagnostic(
    DiscoveryStage Stage,
    DiscoveryStageState State,
    string QueryId,
    bool IsRequired,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    long RowsAdded,
    IReadOnlyList<SqlServerError> Errors,
    string Summary,
    bool IsRetryable);

public sealed record DiscoveryDiagnosticReport(
    Guid CorrelationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Server,
    string Database,
    string? SqlServerVersion,
    DiscoveryStage FinalStage,
    DiscoveryStageState FinalState,
    IReadOnlyList<DiscoveryStageDiagnostic> Stages,
    string Summary,
    bool PartialInventoryDiscarded);

public interface IDiscoveryDiagnosticSession
{
    DiscoveryDiagnosticReport? Current { get; }

    DiscoveryDoctorReport? DoctorReport { get; }

    event EventHandler? Changed;

    void Publish(DiscoveryDiagnosticReport report);

    void PublishDoctor(DiscoveryDoctorReport report);

    void ClearDoctor();

    Task ExportAsync(string path, CancellationToken cancellationToken);

    Task ExportDoctorAsync(string path, CancellationToken cancellationToken);
}

public interface IInventoryDiscoveryService
{
    Task<InventorySnapshot> DiscoverAsync(
        InventoryDiscoveryRequest request,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IInventorySnapshotStore
{
    Task SaveAsync(InventorySnapshot snapshot, string path, CancellationToken cancellationToken);

    Task<InventorySnapshot> LoadAsync(string path, CancellationToken cancellationToken);
}

public interface IInventorySession
{
    InventorySnapshot? Current { get; }

    event EventHandler? Changed;

    void SetCurrent(InventorySnapshot snapshot);

    void Clear();

}
