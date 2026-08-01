using System.Text.Json.Serialization;

namespace MigrationStudio.Application.Discovery;

public enum CatalogDiagnosticStatus
{
    Pending,
    Running,
    Retrying,
    Succeeded,
    Failed,
    Cancelled,
    Skipped
}

public enum DiscoveryDoctorMode
{
    QuickPreflight,
    FullDiagnostic,
    SelectedQueries
}

public enum CatalogFailurePhase
{
    QuerySelection,
    CommandCreation,
    QueryExecution,
    ReaderIteration,
    MetadataMapping,
    Aggregation,
    PostProcessing
}

public sealed record CatalogPhaseDiagnostic(
    CatalogFailurePhase Phase,
    CatalogDiagnosticStatus Status,
    string Summary,
    long RowsProcessed = 0);

public sealed record DiscoveryDoctorRequest(
    DiscoveryDoctorMode Mode,
    IReadOnlySet<string>? QueryIds = null);

public sealed record CatalogQueryDescriptor(
    string QueryId,
    DiscoveryStage Stage,
    bool IsRequired,
    int MinimumMajorVersion,
    string Description,
    [property: JsonIgnore] string QueryText,
    bool IncludeInQuickPreflight,
    string? RequiredCapability = null,
    bool IsMetadataOnly = true);

public sealed record DatabaseCapability(
    string Name,
    bool IsAvailable,
    string Value,
    string Impact);

public sealed record DatabaseCompatibilityAudit(
    string ProductVersion,
    int MajorVersion,
    string ProductLevel,
    string Edition,
    int EngineEdition,
    int CompatibilityLevel,
    IReadOnlyList<DatabaseCapability> Capabilities,
    IReadOnlyList<string> Findings);

public sealed record CatalogQueryDiagnostic(
    CatalogQueryDescriptor Descriptor,
    CatalogDiagnosticStatus Status,
    int Attempt,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long DurationMilliseconds,
    int ResultSetCount,
    long RowCount,
    long RowsMapped,
    IReadOnlyList<SqlServerError> Errors,
    string? ExceptionType,
    CatalogFailurePhase? FailurePhase,
    IReadOnlyList<CatalogPhaseDiagnostic> Phases,
    string Summary,
    string Remediation,
    bool CanRetry);

public sealed record DiscoveryDoctorProgress(
    string QueryId,
    DiscoveryStage Stage,
    CatalogDiagnosticStatus Status,
    int CompletedQueries,
    int TotalQueries,
    string Message)
{
    public double Percentage =>
        TotalQueries == 0 ? 0 : Math.Clamp(CompletedQueries * 100d / TotalQueries, 0, 100);
}

public sealed record DiscoveryDoctorReport(
    Guid CorrelationId,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Server,
    string Database,
    DatabaseCompatibilityAudit Audit,
    IReadOnlyList<CatalogQueryDiagnostic> Queries,
    int RegisteredQueryCount,
    int SelectedQueryCount,
    int ExecutedQueryCount,
    DiscoveryStage? ProductionFailureStage,
    string? ProductionFailureQueryId,
    string? ProductionFailureSummary,
    bool Cancelled);

public interface IDiscoveryDoctorService
{
    IReadOnlyList<CatalogQueryDescriptor> GetCatalog(int sqlServerMajorVersion);

    IReadOnlyList<CatalogQueryDescriptor> SelectCatalog(
        int sqlServerMajorVersion,
        DiscoveryDoctorRequest request);

    Task<DatabaseCompatibilityAudit> AuditAsync(
        SqlServerConnectionOptions connectionOptions,
        CancellationToken cancellationToken);

    Task<DiscoveryDoctorReport> DiagnoseAsync(
        SqlServerConnectionOptions connection,
        DiscoveryDoctorRequest request,
        IProgress<DiscoveryDoctorProgress>? progress,
        CancellationToken cancellationToken);
}
