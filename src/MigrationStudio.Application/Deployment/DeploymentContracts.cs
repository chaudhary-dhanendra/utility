using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Deployment;

public sealed record DeploymentRequest(
    string PackageDirectory,
    PostgreSqlConnectionOptions Connection,
    DeploymentOptions Options,
    DataMigrationRequest? DataMigrationRequest = null,
    Guid? ResumeDeploymentId = null);

public interface IPostgreSqlDeploymentConnectionService
{
    Task<PostgreSqlCapabilityAssessment> AssessAsync(
        PostgreSqlConnectionOptions options,
        bool useMaintenanceDatabase,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> LoadDatabasesAsync(
        PostgreSqlConnectionOptions options,
        CancellationToken cancellationToken);
}

public interface IMigrationPackageReader
{
    Task<MigrationPackageManifest> ReadAndVerifyAsync(
        string packageDirectory,
        bool diagnosticMode,
        CancellationToken cancellationToken);

    string ComputePackageFingerprint(MigrationPackageManifest manifest);
}

public interface IPostgreSqlScriptParser
{
    IReadOnlyList<ParsedSqlStatement> Parse(string sql);
}

public interface IPreDeploymentAssessmentService
{
    Task<PreDeploymentAssessment> AssessAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken);
}

public interface IDatabaseProvisioningService
{
    Task<DatabaseProvisioningResult> EnsureDatabaseAsync(
        PostgreSqlConnectionOptions connection,
        DatabaseCreationOptions options,
        CancellationToken cancellationToken);
}

public sealed record DatabaseProvisioningResult(
    string RequestedDatabase,
    string EffectiveDatabase,
    bool WasCreated,
    bool WasDropped,
    bool UsedExisting,
    string Message);

public interface IDeploymentJournalStore
{
    Task<string> SaveAsync(DeploymentJournal journal, CancellationToken cancellationToken);

    Task<DeploymentJournal?> LoadAsync(Guid deploymentId, CancellationToken cancellationToken);
}

public interface IPostgreSqlDeploymentEngine
{
    Task<PreDeploymentAssessment> AssessAsync(
        DeploymentRequest request,
        CancellationToken cancellationToken);

    Task<DeploymentResult> DeployAsync(
        DeploymentRequest request,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken);

    Task<DeploymentResult> ResumeAsync(
        DeploymentRequest request,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken);

    Task<DeploymentResult> RetryFailedAsync(
        DeploymentRequest request,
        IReadOnlySet<InventoryObjectId> selectedObjects,
        IProgress<DeploymentProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IDeploymentSession
{
    PreDeploymentAssessment? Assessment { get; }

    DeploymentResult? Result { get; }

    event EventHandler? Changed;

    void SetAssessment(PreDeploymentAssessment assessment);

    void SetResult(DeploymentResult result);
}

public interface IDeploymentReportWriter
{
    Task WriteAsync(
        DeploymentResult result,
        string outputDirectory,
        CancellationToken cancellationToken);
}
