using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.Deployment;

public enum DeploymentMode
{
    GenerateOnly,
    DeployToExistingDatabase,
    CreateDatabaseAndDeploy,
    ValidateOnly
}

public enum DeploymentScope
{
    SchemaOnly,
    DataOnly,
    ProgrammableObjectsOnly,
    SecurityOnly,
    CompletePackage,
    SelectedFailedObjects,
    SelectedPhases
}

public enum DatabaseExistsPolicy
{
    Fail,
    UseExisting,
    DropAndRecreate,
    CreateWithAlternateName
}

public enum PreDeploymentPolicy
{
    BlockOnErrors,
    BlockOnCriticalOnly,
    AllowWarnings,
    AdministratorOverride
}

public enum DeploymentTransactionMode
{
    TransactionPerObject,
    TransactionPerPhase,
    SingleTransactionWherePossible,
    NoWrappingTransaction
}

public enum DeploymentErrorPolicy
{
    StopImmediately,
    ContinueIndependentObjects,
    ContinueCurrentPhase,
    RetryTransientFailures,
    ManualDecision
}

public enum ExistingObjectConflictPolicy
{
    Fail,
    SkipWhenEquivalent,
    ReplaceWhenSafe,
    DropAndRecreate,
    RenameTarget,
    ManualDecision
}

public enum ConstraintDeploymentStrategy
{
    CreateAndValidateImmediately,
    AddAfterData,
    AddNotValidThenValidate,
    ValidateInLaterPhase
}

public enum DeploymentFindingSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum DeploymentObjectStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Blocked,
    RolledBack,
    Cancelled,
    SkippedEquivalent,
    BlockedByDependency,
    Manual,
    Unsupported
}

public enum DeploymentRunStatus
{
    Assessed,
    Blocked,
    Running,
    Succeeded,
    SucceededWithWarnings,
    Failed,
    Cancelled
}

public enum CommitStatus
{
    NotStarted,
    Pending,
    Committed,
    RolledBack,
    NonTransactional
}

public sealed record PostgreSqlConnectionOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 5432;

    public string MaintenanceDatabase { get; init; } = "postgres";

    public string TargetDatabase { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string? Password { get; init; }

    public string SslMode { get; init; } = "Prefer";

    public string? RootCertificate { get; init; }

    public string? ClientCertificate { get; init; }

    public string? ClientCertificateKey { get; init; }

    public bool TrustServerCertificate { get; init; }

    public int ConnectionTimeoutSeconds { get; init; } = 15;

    public int CommandTimeoutSeconds { get; init; } = 300;

    public int KeepAliveSeconds { get; init; } = 30;

    public bool Pooling { get; init; } = true;

    public string ApplicationName { get; init; } = "SQL Server to PostgreSQL Migration Studio";

    public string? SearchPath { get; init; }

    public PostgreSqlConnectionOptions Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetDatabase);
        ArgumentException.ThrowIfNullOrWhiteSpace(Username);
        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("PostgreSQL port must be between 1 and 65535.");
        }

        if (ConnectionTimeoutSeconds is < 1 or > 300 ||
            CommandTimeoutSeconds is < 1 or > 7200 ||
            KeepAliveSeconds is < 0 or > 3600)
        {
            throw new InvalidOperationException("PostgreSQL timeout or keepalive settings are invalid.");
        }

        return this;
    }
}

public sealed record DatabaseCreationOptions
{
    public DatabaseExistsPolicy ExistsPolicy { get; init; } = DatabaseExistsPolicy.Fail;

    public string Encoding { get; init; } = "UTF8";

    public string? Locale { get; init; }

    public string? Collation { get; init; }

    public string? CharacterType { get; init; }

    public string? Owner { get; init; }

    public int? ConnectionLimit { get; init; }

    public bool DestructiveActionConfirmed { get; init; }
}

public sealed record DeploymentOptions
{
    public DeploymentMode Mode { get; init; } = DeploymentMode.DeployToExistingDatabase;

    public DeploymentScope Scope { get; init; } = DeploymentScope.CompletePackage;

    public PreDeploymentPolicy PreDeploymentPolicy { get; init; } = PreDeploymentPolicy.BlockOnErrors;

    public DeploymentTransactionMode TransactionMode { get; init; } =
        DeploymentTransactionMode.TransactionPerObject;

    public DeploymentErrorPolicy ErrorPolicy { get; init; } = DeploymentErrorPolicy.StopImmediately;

    public ExistingObjectConflictPolicy ConflictPolicy { get; init; } =
        ExistingObjectConflictPolicy.Fail;

    public ConstraintDeploymentStrategy ConstraintStrategy { get; init; } =
        ConstraintDeploymentStrategy.AddAfterData;

    public DatabaseCreationOptions DatabaseCreation { get; init; } = new();

    public IReadOnlySet<DeploymentPhase> SelectedPhases { get; init; } =
        new HashSet<DeploymentPhase>();

    public IReadOnlySet<InventoryObjectId> SelectedObjects { get; init; } =
        new HashSet<InventoryObjectId>();

    public int RetryCount { get; init; } = 3;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(2);

    public bool InstallRequiredExtensions { get; init; } = true;

    public bool AnalyzeTables { get; init; } = true;

    public bool VacuumAnalyze { get; init; }

    public bool ValidateConstraints { get; init; } = true;

    public bool EnableRepairMode { get; init; }

    public bool DiagnosticManifestMode { get; init; }

    /// <summary>
    /// Requires every selected executable artifact to carry a successful live
    /// PostgreSQL validation result in the immutable package manifest.
    /// Desktop production deployments enable this gate explicitly.
    /// </summary>
    public bool RequireLivePostgreSqlValidation { get; init; }

    public bool AdministratorOverrideConfirmed { get; init; }

    public string? AdministratorOverrideReason { get; init; }

    public DataMigrationOptions? DataMigrationOptions { get; init; }

    public DeploymentOptions Validate()
    {
        if (RetryCount is < 0 or > 20 || RetryBaseDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException("Deployment retry settings are invalid.");
        }

        if (DatabaseCreation.ExistsPolicy == DatabaseExistsPolicy.DropAndRecreate &&
            !DatabaseCreation.DestructiveActionConfirmed)
        {
            throw new InvalidOperationException("Dropping a database requires explicit destructive confirmation.");
        }

        if (PreDeploymentPolicy == PreDeploymentPolicy.AdministratorOverride &&
            (!AdministratorOverrideConfirmed || string.IsNullOrWhiteSpace(AdministratorOverrideReason)))
        {
            throw new InvalidOperationException("Administrator override requires confirmation and a recorded reason.");
        }

        return this;
    }
}

public sealed record PackageFileManifest(
    string RelativePath,
    string Sha256,
    long Length,
    bool Required);

public sealed record PackageArtifactManifest(
    InventoryObjectId SourceObjectId,
    string TargetObjectType,
    string TargetSchema,
    string TargetName,
    DeploymentPhase Phase,
    string ScriptFile,
    string Sql,
    string SqlSha256,
    ConversionClassification Classification,
    IReadOnlyList<InventoryObjectId> Dependencies,
    IReadOnlyList<string> RequiredExtensions,
    bool RequiresManualReview,
    IReadOnlyList<string> UnsupportedConstructs,
    int DependencyComponent)
{
    /// <summary>
    /// Parent relation identity for relation-scoped PostgreSQL objects such as
    /// constraints and triggers. Empty for database/schema-scoped objects.
    /// </summary>
    public string TargetParentObject { get; init; } = string.Empty;

    /// <summary>
    /// PostgreSQL routine identity arguments (not argument names or defaults).
    /// Empty means the package predates routine-signature manifests.
    /// </summary>
    public string RoutineIdentityArguments { get; init; } = string.Empty;

    /// <summary>
    /// True when this manifest entry contains a deployment statement. False
    /// for traceability-only entries implemented by another artifact (for
    /// example, a constraint-owned index or an inline default).
    /// </summary>
    public bool IsExecutable { get; init; } = true;

    public SqlValidationResult LiveValidation { get; init; } =
        new(false, false, null, null, null);
}

public sealed record MigrationPackageManifest
{
    public const int CurrentFormatVersion = 5;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public Guid PackageId { get; init; }

    public Guid MigrationRunId { get; init; }

    public string? SourceServer { get; init; }

    public string SourceDatabase { get; init; } = string.Empty;

    public int TargetPostgreSqlVersion { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public string ApplicationVersion { get; init; } = string.Empty;

    public string SourceMetadataHash { get; init; } = string.Empty;

    public string ConversionConfigurationHash { get; init; } = string.Empty;

    public IReadOnlyList<PackageFileManifest> Files { get; init; } = [];

    public IReadOnlyList<PackageArtifactManifest> Artifacts { get; init; } = [];

    public IReadOnlyList<IdentifierMappingEntry> ObjectMappings { get; init; } = [];

    public IReadOnlyList<string> RequiredExtensions { get; init; } = [];

    public IReadOnlyList<string> DataReferences { get; init; } = [];

    public IReadOnlyList<string> ManualReviewItems { get; init; } = [];

    public IReadOnlyList<string> UnsupportedFeatures { get; init; } = [];

    public Guid? DeploymentPlanId { get; init; }

    public BlockedDependencyReconciliation? BlockedDependencyReconciliation { get; init; }

    public string SecurityClassification { get; init; } = "Contains schema metadata; no row values";
}

public sealed record PostgreSqlCapabilityAssessment(
    bool ConnectionSucceeded,
    string? ServerVersion,
    int? ServerMajorVersion,
    string? CurrentUser,
    string? CurrentDatabase,
    bool CanCreateDatabase,
    bool IsSuperUser,
    bool CanCreateRole,
    bool CanCreateSchema,
    IReadOnlyDictionary<string, string> InstalledExtensions,
    IReadOnlySet<string> AvailableExtensions,
    IReadOnlySet<string> RoleMemberships,
    string RedactedConnection,
    IReadOnlyList<string> Warnings);

public sealed record DeploymentFinding(
    string Code,
    DeploymentFindingSeverity Severity,
    string Message,
    DeploymentPhase? Phase = null,
    InventoryObjectId? ObjectId = null,
    bool CanOverride = false);

public sealed record ObjectConflict(
    InventoryObjectId SourceObjectId,
    string TargetObject,
    string ObjectType,
    bool Exists,
    bool IsEquivalent,
    bool ContainsData,
    ExistingObjectConflictPolicy SelectedPolicy,
    string? Detail);

public sealed record PackageObjectDuplicate(
    string ObjectKind,
    string TargetSchema,
    string TargetName,
    string TargetParentObject,
    string RoutineIdentityArguments,
    IReadOnlyList<InventoryObjectId> SourceObjectIds);

public sealed record PreDeploymentAssessment(
    Guid AssessmentId,
    DateTimeOffset AssessedAt,
    string PackageDirectory,
    MigrationPackageManifest? Manifest,
    PostgreSqlCapabilityAssessment? Capabilities,
    IReadOnlyList<DeploymentFinding> Findings,
    IReadOnlyList<ObjectConflict> Conflicts,
    bool PackageIntegrityValid,
    bool CanDeploy,
    bool AdministratorOverrideApplied,
    string? AdministratorOverrideReason)
{
    public IReadOnlyList<PackageObjectDuplicate> PackageDuplicates { get; init; } = [];
}

public sealed record ParsedSqlStatement(
    int Ordinal,
    string Sql,
    int StartLine,
    int EndLine,
    string Sha256,
    bool CanRunInTransaction);

public sealed record DeploymentRetryRecord(
    int Attempt,
    DateTimeOffset At,
    TimeSpan Delay,
    string Reason);

public sealed record DeploymentFailure(
    string Package,
    DeploymentPhase Phase,
    InventoryObjectId? ObjectId,
    string? TargetObject,
    string StatementHash,
    string? ScriptFile,
    int? Line,
    string? SqlState,
    string? Severity,
    string? Detail,
    string? Hint,
    int? Position,
    int? InternalPosition,
    string? Schema,
    string? Table,
    string? Column,
    string? Constraint,
    string? DataType,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int RetryCount);

public sealed record DeploymentObjectJournal(
    InventoryObjectId? SourceObjectId,
    string TargetObject,
    DeploymentPhase Phase,
    string ScriptFile,
    string ExecutedSqlHash,
    DeploymentObjectStatus Status,
    CommitStatus CommitStatus,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<InventoryObjectId> Dependencies,
    IReadOnlyList<DeploymentRetryRecord> Retries,
    DeploymentFailure? Failure,
    bool IsIdempotent,
    string? Message);

public sealed record DeploymentJournal(
    int FormatVersion,
    Guid DeploymentId,
    Guid PackageId,
    Guid MigrationRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    DeploymentRunStatus Status,
    string ApplicationVersion,
    string MachineName,
    string? OsUser,
    string PackageDirectory,
    string PackageFingerprint,
    string TargetServer,
    string TargetDatabase,
    string OptionsHash,
    IReadOnlyList<string> Overrides,
    IReadOnlyList<string> DestructiveConfirmations,
    IReadOnlyList<DeploymentObjectJournal> Objects,
    Guid? DataMigrationRunId,
    IReadOnlyList<DeploymentFinding> FinalFindings)
{
    public const int CurrentFormatVersion = 2;
}

public sealed record DeploymentProgress(
    Guid DeploymentId,
    DeploymentPhase Phase,
    InventoryObjectId? ObjectId,
    string CurrentObject,
    string Message,
    int Completed,
    int Failed,
    int Skipped,
    int Total,
    int RetryAttempt)
{
    public double Percentage => Total == 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record DeploymentResult(
    Guid DeploymentId,
    DeploymentRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string TargetDatabase,
    string JournalPath,
    IReadOnlyList<DeploymentObjectJournal> Objects,
    IReadOnlyList<DeploymentFailure> Failures,
    Guid? DataMigrationRunId,
    IReadOnlyList<DeploymentFinding> Findings);
