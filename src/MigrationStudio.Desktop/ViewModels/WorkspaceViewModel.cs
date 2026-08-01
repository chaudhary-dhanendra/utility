using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Npgsql;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Application.DataMigration;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Errors;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Security;
using MigrationStudio.Application.Validation;
using MigrationStudio.Desktop.Dialogs;
using MigrationStudio.Desktop.Collections;
using MigrationStudio.Desktop.Threading;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Operations;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly ISqlServerConnectionService _connectionService;
    private readonly IInventoryDiscoveryService _discoveryService;
    private readonly IInventorySnapshotStore _snapshotStore;
    private readonly IInventorySession _session;
    private readonly IExcelTableSelectionService _excelService;
    private readonly IBackgroundOperationScheduler _scheduler;
    private readonly IFileDialogService _dialogs;
    private readonly IErrorPresenter _errors;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<WorkspaceViewModel> _logger;
    private readonly IDiscoveryDiagnosticSession _discoveryDiagnostics;
    private readonly IDiscoveryDoctorService _discoveryDoctor;
    private readonly ISensitiveDataRedactor _redactor;
    private readonly IConversionEngine _conversionEngine;
    private readonly IConversionSession _conversionSession;
    private readonly IDeploymentPackageWriter _packageWriter;
    private readonly IMigrationPackageReader _packageReader;
    private readonly IApplicationPaths _applicationPaths;
    private readonly IConversionReportWriter _conversionReportWriter;
    private readonly IGeneratedSqlValidator _generatedSqlValidator;
    private readonly IDataMigrationEngine _dataMigrationEngine;
    private readonly IDataMigrationPlanner _dataMigrationPlanner;
    private readonly IMigrationPauseController _migrationPauseController;
    private readonly IDataMigrationReportWriter _dataMigrationReportWriter;
    private readonly IPostgreSqlDeploymentEngine _deploymentEngine;
    private readonly IPostgreSqlDeploymentConnectionService _deploymentConnectionService;
    private readonly IDeploymentReportWriter _deploymentReportWriter;
    private readonly IValidationEngine _validationEngine;
    private readonly IValidationRunStore _validationRunStore;
    private readonly IValidationReportWriter _validationReportWriter;
    private readonly IValidationSession _validationSession;
    private ExcelTableSelectionResult? _excelResult;
    private OperationId? _operationId;
    private OperationId? _conversionOperationId;
    private OperationId? _dataOperationId;
    private DataMigrationResult? _dataMigrationResult;
    private OperationId? _deploymentOperationId;
    private DeploymentResult? _deploymentResult;
    private OperationId? _validationOperationId;
    private ValidationRun? _validationRun;
    private readonly Dictionary<InventoryObjectId, InventoryObjectRowViewModel> _objectRows = [];
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _excelCancellation;
    private CancellationTokenSource? _doctorCancellation;
    private int _discoveryInFlight;
    private int _conversionInFlight;
    private int _deploymentInFlight;

    [ObservableProperty] private string _server = "localhost";
    [ObservableProperty] private int _port = 1433;
    [ObservableProperty] private bool _useWindowsAuthentication = true;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _encrypt = true;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private int _connectionTimeoutSeconds = 15;
    [ObservableProperty] private int _commandTimeoutSeconds = 120;
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private string _connectionStatus = "Not tested";
    [ObservableProperty] private MigrationScopeMode _scopeMode;
    [ObservableProperty] private DependencyPolicy _dependencyPolicy = DependencyPolicy.IncludeRequiredDependencies;
    [ObservableProperty] private string _excelPath = string.Empty;
    [ObservableProperty] private string? _selectedWorksheet;
    [ObservableProperty] private string _tableColumn = "Table";
    [ObservableProperty] private string _excelStatus = "No workbook selected";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private int _inventoryViewModelCount;
    [ObservableProperty] private int _displayedObjectCount;
    [ObservableProperty] private double _lastInventoryProjectionMilliseconds;
    [ObservableProperty] private double _lastInventoryFilterMilliseconds;
    [ObservableProperty] private InventoryObjectRowViewModel? _selectedObject;
    [ObservableProperty] private string _status = "Configure a source connection to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isDiscoveryFailureVisible;
    [ObservableProperty] private string _discoveryFailureStage = string.Empty;
    [ObservableProperty] private string _discoveryFailureQueryId = string.Empty;
    [ObservableProperty] private string _discoveryFailureErrorCode = string.Empty;
    [ObservableProperty] private string _discoveryFailureSummary = string.Empty;
    [ObservableProperty] private string _discoveryFailureDetails = string.Empty;
    [ObservableProperty] private string _discoveryFailureRemediation = string.Empty;
    [ObservableProperty] private string _discoveryCorrelationId = string.Empty;
    [ObservableProperty] private bool _canRetryDiscovery;
    [ObservableProperty] private int _selectedWorkspaceTabIndex;
    [ObservableProperty] private string _doctorStatus = "Select a database, then run the compatibility audit or Discovery Doctor.";
    [ObservableProperty] private double _doctorProgress;
    [ObservableProperty] private bool _isDoctorRunning;
    [ObservableProperty] private string _doctorAuditSummary = "Compatibility audit not run.";
    [ObservableProperty] private CatalogQueryDiagnostic? _selectedDoctorQuery;
    [ObservableProperty] private int _doctorRegisteredQueryCount;
    [ObservableProperty] private int _doctorSelectedQueryCount;
    [ObservableProperty] private int _doctorExecutedQueryCount;
    [ObservableProperty] private int _doctorPassedQueryCount;
    [ObservableProperty] private int _doctorFailedQueryCount;
    [ObservableProperty] private int _doctorSkippedQueryCount;
    [ObservableProperty] private string _doctorCurrentQuery = "None";
    [ObservableProperty] private string _doctorCurrentStage = "Not started";
    [ObservableProperty] private long _objectCount;
    [ObservableProperty] private long _includedCount;
    [ObservableProperty] private long _findingCount;
    [ObservableProperty] private long _unresolvedDependencyCount;
    [ObservableProperty] private int _targetPostgreSqlVersion = 17;
    [ObservableProperty] private IdentifierCaseMode _identifierCaseMode =
        IdentifierCaseMode.QuoteOnlyWhenRequired;
    [ObservableProperty] private SchemaMappingMode _schemaMappingMode = SchemaMappingMode.Preserve;
    [ObservableProperty] private IdentityConversionStrategy _identityStrategy = IdentityConversionStrategy.GeneratedByDefaultAsIdentity;
    [ObservableProperty] private SecurityConversionStrategy _securityStrategy = SecurityConversionStrategy.ReportOnly;
    [ObservableProperty] private bool _enablePgCrypto = true;
    [ObservableProperty] private bool _enablePostGis;
    [ObservableProperty] private ConversionArtifactViewModel? _selectedConversionArtifact;
    [ObservableProperty] private string _conversionStatus = "Run inventory discovery before conversion.";
    [ObservableProperty] private int _conversionProcessed;
    [ObservableProperty] private int _conversionTotal;
    [ObservableProperty] private double _conversionObjectsPerSecond;
    [ObservableProperty] private TimeSpan _conversionElapsed;
    [ObservableProperty] private string _conversionCurrentStage = "Not started";
    [ObservableProperty] private string _conversionCurrentObjectType = string.Empty;
    [ObservableProperty] private string _conversionCurrentObject = string.Empty;
    [ObservableProperty] private DateTimeOffset? _conversionLastProgressAt;
    [ObservableProperty] private bool _conversionIsResponsive = true;
    [ObservableProperty] private TimeSpan? _conversionEstimatedRemaining;
    [ObservableProperty] private string _conversionOperationIdentifier = string.Empty;
    [ObservableProperty] private string _conversionMappingSetIdentifier = string.Empty;
    [ObservableProperty] private string _identifierMappingStatus =
        "Identifier mapping is generated atomically during conversion.";
    [ObservableProperty] private int _selectedConversionTabIndex;
    [ObservableProperty] private int _automaticConversionCount;
    [ObservableProperty] private int _warningConversionCount;
    [ObservableProperty] private int _manualConversionCount;
    [ObservableProperty] private int _unsupportedConversionCount;
    [ObservableProperty] private int _selectedArtifactCount;
    [ObservableProperty] private int _convertedArtifactCount;
    [ObservableProperty] private int _packagedArtifactCount;
    [ObservableProperty] private int _packagedExecutableCount;
    [ObservableProperty] private int _packagedManualReviewCount;
    [ObservableProperty] private int _packagedUnsupportedCount;
    [ObservableProperty] private string _postgreSqlValidationConnectionString = string.Empty;
    [ObservableProperty] private double _liveValidationProgress;
    [ObservableProperty] private int _liveValidationCompleted;
    [ObservableProperty] private int _liveValidationTotal;
    [ObservableProperty] private int _liveValidationPassedCount;
    [ObservableProperty] private int _liveValidationFailedCount;
    [ObservableProperty] private int _liveValidationBlockedCount;
    [ObservableProperty] private int _liveValidationNotRunCount;
    [ObservableProperty] private int _liveValidationManualReviewCount;
    [ObservableProperty] private int _liveValidationReusedCount;
    [ObservableProperty] private string _liveValidationCurrentObject = string.Empty;
    [ObservableProperty] private string _liveValidationStatus = "Live PostgreSQL validation has not run.";
    [ObservableProperty] private LiveSqlValidationFailureViewModel? _selectedLiveValidationFailure;
    [ObservableProperty] private DataMigrationMode _dataMigrationMode = DataMigrationMode.SchemaAndData;
    [ObservableProperty] private DataMigrationExecutionMode _dataExecutionMode = DataMigrationExecutionMode.Execute;
    [ObservableProperty] private ParallelismMode _dataParallelismMode = ParallelismMode.Adaptive;
    [ObservableProperty] private TableLoadOrderingStrategy _loadOrdering = TableLoadOrderingStrategy.ForeignKeysAfterData;
    [ObservableProperty] private TargetPreparationStrategy _targetPreparation = TargetPreparationStrategy.FailIfNotEmpty;
    [ObservableProperty] private MigrationFailurePolicy _migrationFailurePolicy = MigrationFailurePolicy.FailFast;
    [ObservableProperty] private int _maximumConcurrentTables = 4;
    [ObservableProperty] private int _maximumConcurrentReaders = 4;
    [ObservableProperty] private int _maximumConcurrentWriters = 4;
    [ObservableProperty] private int _batchRowCount = 5000;
    [ObservableProperty] private long _batchByteSize = 33554432;
    [ObservableProperty] private bool _destructivePreparationConfirmed;
    [ObservableProperty] private bool _validateNullCounts;
    [ObservableProperty] private ChecksumMode _checksumMode;
    [ObservableProperty] private string _dataMigrationStatus = "Convert the selected inventory before migrating data.";
    [ObservableProperty] private string _migrationRunId = string.Empty;
    [ObservableProperty] private long _totalRowsRead;
    [ObservableProperty] private long _totalRowsWritten;
    [ObservableProperty] private long _totalRowsRejected;
    [ObservableProperty] private double _dataRowsPerSecond;
    [ObservableProperty] private double _dataBytesPerSecond;
    [ObservableProperty] private int _activeTables;
    [ObservableProperty] private int _activeReaders;
    [ObservableProperty] private int _activeWriters;
    [ObservableProperty] private int _targetExpectedTables;
    [ObservableProperty] private int _targetExistingTables;
    [ObservableProperty] private int _targetMissingTables;
    [ObservableProperty] private int _targetExpectedColumns;
    [ObservableProperty] private int _targetExistingColumns;
    [ObservableProperty] private int _targetMissingColumns;
    [ObservableProperty] private string _streamingCurrentStage = "Not started";
    [ObservableProperty] private string _streamingCurrentTable = string.Empty;
    [ObservableProperty] private long _streamingCurrentBatch;
    [ObservableProperty] private string _streamingCurrentReader = string.Empty;
    [ObservableProperty] private string _streamingCurrentWriter = string.Empty;
    [ObservableProperty] private string _streamingLastSuccessfulStage = string.Empty;
    [ObservableProperty] private string _streamingFailureStage = string.Empty;
    [ObservableProperty] private string _streamingFailureComponent = string.Empty;
    [ObservableProperty] private string _streamingFailureReason = string.Empty;
    [ObservableProperty] private string _streamingRemediation = string.Empty;
    [ObservableProperty] private bool _isMigrationPaused;
    [ObservableProperty] private DataMigrationTableRowViewModel? _selectedDataTable;
    [ObservableProperty] private string _deploymentPackagePath = string.Empty;
    [ObservableProperty] private string _deploymentHost = "localhost";
    [ObservableProperty] private int _deploymentPort = 5432;
    [ObservableProperty] private string _maintenanceDatabase = "postgres";
    [ObservableProperty] private string _deploymentTargetDatabase = string.Empty;
    [ObservableProperty] private string _deploymentUsername = "postgres";
    [ObservableProperty] private string _deploymentPassword = string.Empty;
    [ObservableProperty] private string _deploymentSslMode = "Prefer";
    [ObservableProperty] private string _rootCertificate = string.Empty;
    [ObservableProperty] private string _clientCertificate = string.Empty;
    [ObservableProperty] private DeploymentMode _deploymentMode = DeploymentMode.DeployToExistingDatabase;
    [ObservableProperty] private DeploymentScope _deploymentScope = DeploymentScope.CompletePackage;
    [ObservableProperty] private PreDeploymentPolicy _preDeploymentPolicy = PreDeploymentPolicy.BlockOnErrors;
    [ObservableProperty] private DeploymentTransactionMode _deploymentTransactionMode = DeploymentTransactionMode.TransactionPerObject;
    [ObservableProperty] private DeploymentErrorPolicy _deploymentErrorPolicy = DeploymentErrorPolicy.StopImmediately;
    [ObservableProperty] private ExistingObjectConflictPolicy _conflictPolicy = ExistingObjectConflictPolicy.Fail;
    [ObservableProperty] private DatabaseExistsPolicy _databaseExistsPolicy = DatabaseExistsPolicy.Fail;
    [ObservableProperty] private bool _deploymentDestructiveConfirmed;
    [ObservableProperty] private bool _administratorOverrideConfirmed;
    [ObservableProperty] private string _administratorOverrideReason = string.Empty;
    [ObservableProperty] private bool _installRequiredExtensions = true;
    [ObservableProperty] private bool _analyzeTables = true;
    [ObservableProperty] private bool _vacuumAnalyze;
    [ObservableProperty] private string _deploymentStatus = "Select or generate a migration package.";
    [ObservableProperty] private string _deploymentId = string.Empty;
    [ObservableProperty] private string _deploymentServerVersion = string.Empty;
    [ObservableProperty] private string _deploymentCurrentObject = string.Empty;
    [ObservableProperty] private int _deploymentCompleted;
    [ObservableProperty] private int _deploymentFailed;
    [ObservableProperty] private int _deploymentSkipped;
    [ObservableProperty] private double _deploymentProgress;
    [ObservableProperty] private string _deploymentPackageId = string.Empty;
    [ObservableProperty] private string _deploymentSourceDatabase = string.Empty;
    [ObservableProperty] private int _deploymentTargetVersion;
    [ObservableProperty] private int _deploymentArtifactCount;
    [ObservableProperty] private int _deploymentManualReviewCount;
    [ObservableProperty] private int _deploymentPackageDuplicateCount;
    [ObservableProperty] private int _deploymentBlockingFindingCount;
    [ObservableProperty] private int _deploymentEquivalentObjectCount;
    [ObservableProperty] private int _deploymentActualConflictCount;
    [ObservableProperty] private int _deploymentTargetSchemaCount;
    [ObservableProperty] private int _deploymentTargetTableCount;
    [ObservableProperty] private bool _deploymentPackageIntegrityValid;
    [ObservableProperty] private ValidationLevel _validationLevel = ValidationLevel.Full;
    [ObservableProperty]
    private KeylessTableValidationStrategy _keylessValidationStrategy =
        KeylessTableValidationStrategy.CountAndAggregatesOnly;
    [ObservableProperty] private int _validationSampleSize = 1000;
    [ObservableProperty] private int _validationChunkSize = 10000;
    [ObservableProperty] private bool _validationForeignKeyOrphans = true;
    [ObservableProperty] private bool _validationDistinctCounts;
    [ObservableProperty] private string _validationStatus = "Deploy a converted database, then configure validation.";
    [ObservableProperty] private double _validationProgress;
    [ObservableProperty] private string _validationCurrentObject = string.Empty;
    [ObservableProperty] private string _validationRunId = string.Empty;
    [ObservableProperty] private string _readinessStatus = "Not evaluated";
    [ObservableProperty] private decimal? _readinessScore;
    [ObservableProperty] private int _validationCriticalBlockers;

    public WorkspaceViewModel(
        ISqlServerConnectionService connectionService,
        IInventoryDiscoveryService discoveryService,
        IInventorySnapshotStore snapshotStore,
        IInventorySession session,
        IExcelTableSelectionService excelService,
        IBackgroundOperationScheduler scheduler,
        IDiscoveryDiagnosticSession discoveryDiagnostics,
        IDiscoveryDoctorService discoveryDoctor,
        ISensitiveDataRedactor redactor,
        IFileDialogService dialogs,
        IErrorPresenter errors,
        IUiDispatcher dispatcher,
        PostgreSqlConnectionViewModel postgreSqlTarget,
        IConversionEngine conversionEngine,
        IConversionSession conversionSession,
        IDeploymentPackageWriter packageWriter,
        IMigrationPackageReader packageReader,
        IApplicationPaths applicationPaths,
        IConversionReportWriter conversionReportWriter,
        IGeneratedSqlValidator generatedSqlValidator,
        IDataMigrationEngine dataMigrationEngine,
        IDataMigrationPlanner dataMigrationPlanner,
        IMigrationPauseController migrationPauseController,
        IDataMigrationReportWriter dataMigrationReportWriter,
        IPostgreSqlDeploymentEngine deploymentEngine,
        IPostgreSqlDeploymentConnectionService deploymentConnectionService,
        IDeploymentReportWriter deploymentReportWriter,
        IValidationEngine validationEngine,
        IValidationRunStore validationRunStore,
        IValidationReportWriter validationReportWriter,
        IValidationSession validationSession,
        ILogger<WorkspaceViewModel> logger)
    {
        _connectionService = connectionService;
        _discoveryService = discoveryService;
        _snapshotStore = snapshotStore;
        _session = session;
        _excelService = excelService;
        _scheduler = scheduler;
        _discoveryDiagnostics = discoveryDiagnostics;
        _discoveryDoctor = discoveryDoctor;
        _redactor = redactor;
        _dialogs = dialogs;
        _errors = errors;
        _dispatcher = dispatcher;
        PostgreSqlTarget = postgreSqlTarget;
        _conversionEngine = conversionEngine;
        _conversionSession = conversionSession;
        _packageWriter = packageWriter;
        _packageReader = packageReader;
        _applicationPaths = applicationPaths;
        _conversionReportWriter = conversionReportWriter;
        _generatedSqlValidator = generatedSqlValidator;
        _dataMigrationEngine = dataMigrationEngine;
        _dataMigrationPlanner = dataMigrationPlanner;
        _migrationPauseController = migrationPauseController;
        _dataMigrationReportWriter = dataMigrationReportWriter;
        _deploymentEngine = deploymentEngine;
        _deploymentConnectionService = deploymentConnectionService;
        _deploymentReportWriter = deploymentReportWriter;
        _validationEngine = validationEngine;
        _validationRunStore = validationRunStore;
        _validationReportWriter = validationReportWriter;
        _validationSession = validationSession;
        _logger = logger;
    }

    public string Title { get; } = "SQL Server Discovery & Inventory";

    public PostgreSqlConnectionViewModel PostgreSqlTarget { get; }

    public IReadOnlyList<MigrationScopeMode> ScopeModes { get; } = Enum.GetValues<MigrationScopeMode>();

    public IReadOnlyList<DependencyPolicy> DependencyPolicies { get; } = Enum.GetValues<DependencyPolicy>();

    public IReadOnlyList<int> PostgreSqlVersions { get; } = [14, 15, 16, 17, 18];

    public IReadOnlyList<IdentifierCaseMode> IdentifierCaseModes { get; } = Enum.GetValues<IdentifierCaseMode>();

    public IReadOnlyList<SchemaMappingMode> SchemaMappingModes { get; } = Enum.GetValues<SchemaMappingMode>();

    public IReadOnlyList<IdentityConversionStrategy> IdentityStrategies { get; } = Enum.GetValues<IdentityConversionStrategy>();

    public IReadOnlyList<SecurityConversionStrategy> SecurityStrategies { get; } = Enum.GetValues<SecurityConversionStrategy>();

    private static readonly Action<ILogger, Exception?> deploymentFailed =
    LoggerMessage.Define(
        LogLevel.Error,
        new EventId(1001, nameof(QueueDeploymentAsync)),
        "PostgreSQL schema deployment failed.");
    partial void OnSelectedDatabaseChanged(string? value)
    {
        ClearInventoryAfterDatabaseChange();

        StartDiscoveryCommand.NotifyCanExecuteChanged();
        RunDiscoveryDoctorCommand.NotifyCanExecuteChanged();
        RunQuickPreflightCommand.NotifyCanExecuteChanged();
        RunCompatibilityAuditCommand.NotifyCanExecuteChanged();
    }
    private void ClearInventoryAfterDatabaseChange()
    {
        _searchCancellation?.Cancel();

        _session.Clear();
        _conversionSession.Clear();

        _objectRows.Clear();

        Objects.Clear();
        Schemas.Clear();
        Findings.Clear();
        Dependencies.Clear();

        UnmatchedExcelRows.Clear();
        AmbiguousExcelRows.Clear();

        ConversionArtifacts.Clear();
        IdentifierMappings.Clear();
        TypeMappings.Clear();
        LiveValidationFailures.Clear();

        DataTables.Clear();
        DataFailures.Clear();
        DataValidations.Clear();
        SequenceResets.Clear();

        DeploymentPhases.Clear();
        DeploymentFindings.Clear();
        DeploymentConflicts.Clear();
        DeploymentPackageDuplicates.Clear();
        DeploymentJournalEntries.Clear();
        DeploymentExtensions.Clear();

        ValidationCategoryScores.Clear();
        ValidationObjectComparisons.Clear();
        ValidationDataComparisons.Clear();
        ValidationSequences.Clear();
        ValidationFindings.Clear();
        ValidationRoutineTestCases.Clear();

        ObjectCount = 0;
        IncludedCount = 0;
        FindingCount = 0;
        UnresolvedDependencyCount = 0;
        DisplayedObjectCount = 0;
        InventoryViewModelCount = 0;

        Progress = 0;

        Status = string.IsNullOrWhiteSpace(SelectedDatabase)
            ? "Select a source database."
            : $"Database changed to {SelectedDatabase}. Run discovery.";

        ConversionStatus = "Run discovery and conversion.";
        IdentifierMappingStatus = "No identifier mapping is available.";

        DeploymentPackagePath = string.Empty;
    }

    public ObservableCollection<CatalogQueryDiagnostic> DoctorQueries { get; } = [];

    public ObservableCollection<DatabaseCapability> DoctorCapabilities { get; } = [];

    public void ActivateDiscoveryDoctor()
    {
        SelectedWorkspaceTabIndex = 10;
    }

    public IReadOnlyList<DataMigrationMode> DataMigrationModes { get; } = Enum.GetValues<DataMigrationMode>();

    public IReadOnlyList<DataMigrationExecutionMode> DataExecutionModes { get; } = Enum.GetValues<DataMigrationExecutionMode>();

    public IReadOnlyList<ParallelismMode> DataParallelismModes { get; } = Enum.GetValues<ParallelismMode>();

    public IReadOnlyList<TableLoadOrderingStrategy> LoadOrderings { get; } = Enum.GetValues<TableLoadOrderingStrategy>();

    public IReadOnlyList<TargetPreparationStrategy> TargetPreparations { get; } = Enum.GetValues<TargetPreparationStrategy>();

    public IReadOnlyList<MigrationFailurePolicy> MigrationFailurePolicies { get; } = Enum.GetValues<MigrationFailurePolicy>();

    public IReadOnlyList<ChecksumMode> ChecksumModes { get; } = Enum.GetValues<ChecksumMode>();

    public IReadOnlyList<DeploymentMode> DeploymentModes { get; } = Enum.GetValues<DeploymentMode>();

    public IReadOnlyList<DeploymentScope> DeploymentScopes { get; } = Enum.GetValues<DeploymentScope>();

    public IReadOnlyList<PreDeploymentPolicy> PreDeploymentPolicies { get; } = Enum.GetValues<PreDeploymentPolicy>();

    public IReadOnlyList<DeploymentTransactionMode> DeploymentTransactionModes { get; } = Enum.GetValues<DeploymentTransactionMode>();

    public IReadOnlyList<DeploymentErrorPolicy> DeploymentErrorPolicies { get; } = Enum.GetValues<DeploymentErrorPolicy>();

    public IReadOnlyList<ExistingObjectConflictPolicy> ConflictPolicies { get; } = Enum.GetValues<ExistingObjectConflictPolicy>();

    public IReadOnlyList<DatabaseExistsPolicy> DatabaseExistsPolicies { get; } = Enum.GetValues<DatabaseExistsPolicy>();

    public IReadOnlyList<string> DeploymentSslModes { get; } =
        ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

    public IReadOnlyList<ValidationLevel> ValidationLevels { get; } = Enum.GetValues<ValidationLevel>();

    public IReadOnlyList<KeylessTableValidationStrategy> KeylessValidationStrategies { get; } =
        Enum.GetValues<KeylessTableValidationStrategy>();

    public ObservableCollection<string> Databases { get; } = [];

    public ObservableCollection<string> Worksheets { get; } = [];

    public ObservableCollection<SchemaSelectionViewModel> Schemas { get; } = [];

    public BulkObservableCollection<InventoryObjectRowViewModel> Objects { get; } = [];

    public ObservableCollection<InventoryFinding> Findings { get; } = [];

    public ObservableCollection<InventoryDependency> Dependencies { get; } = [];

    public ObservableCollection<ExcelTableNameEntry> UnmatchedExcelRows { get; } = [];

    public ObservableCollection<ExcelAmbiguousTableMatch> AmbiguousExcelRows { get; } = [];

    public BulkObservableCollection<ConversionArtifactViewModel> ConversionArtifacts { get; } = [];

    public BulkObservableCollection<IdentifierMappingEntry> IdentifierMappings { get; } = [];

    public BulkObservableCollection<TypeMappingResult> TypeMappings { get; } = [];

    public BulkObservableCollection<LiveSqlValidationFailureViewModel> LiveValidationFailures { get; } = [];

    public ObservableCollection<DataMigrationTableRowViewModel> DataTables { get; } = [];

    public ObservableCollection<MigrationFailure> DataFailures { get; } = [];

    public ObservableCollection<TableValidationResult> DataValidations { get; } = [];

    public ObservableCollection<SequenceResetResult> SequenceResets { get; } = [];

    public ObservableCollection<DeploymentPhaseRowViewModel> DeploymentPhases { get; } =
        new(Enum.GetValues<DeploymentPhase>()
            .Where(item => item != DeploymentPhase.ManualReview)
            .Select(item => new DeploymentPhaseRowViewModel(item, true)));

    public ObservableCollection<DeploymentFinding> DeploymentFindings { get; } = [];

    public ObservableCollection<ObjectConflict> DeploymentConflicts { get; } = [];

    public ObservableCollection<PackageObjectDuplicate> DeploymentPackageDuplicates { get; } = [];

    public ObservableCollection<DeploymentObjectJournal> DeploymentJournalEntries { get; } = [];

    public ObservableCollection<string> DeploymentExtensions { get; } = [];

    public ObservableCollection<ValidationCategoryScore> ValidationCategoryScores { get; } = [];

    public ObservableCollection<ObjectComparison> ValidationObjectComparisons { get; } = [];

    public ObservableCollection<TableDataComparison> ValidationDataComparisons { get; } = [];

    public ObservableCollection<SequenceValidationResult> ValidationSequences { get; } = [];

    public ObservableCollection<ValidationFinding> ValidationFindings { get; } = [];

    public ObservableCollection<RoutineValidationTestCase> ValidationRoutineTestCases { get; } = [];

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        try
        {
            IsBusy = true;
            ConnectionStatus = "Testing…";
            var result = await _connectionService.TestAsync(CreateConnection(requireDatabase: false), CancellationToken.None);
            ConnectionStatus = result.Succeeded
                ? $"Connected · SQL Server {result.ServerVersion} · {result.Duration.TotalMilliseconds:F0} ms"
                : string.Join(Environment.NewLine, result.Errors.Select(error => $"SQL {error.Number}: {error.Message}"));
        }
        catch (Exception exception)
        {
            HandleError("Connection test failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadDatabasesAsync()
    {
        try
        {
            IsBusy = true;
            var databases = await _connectionService.LoadDatabasesAsync(
                CreateConnection(requireDatabase: false),
                CancellationToken.None);
            Databases.Clear();
            foreach (var database in databases)
            {
                Databases.Add(database);
            }
            SelectedDatabase ??= Databases.FirstOrDefault();
            ConnectionStatus = $"{Databases.Count:N0} accessible databases loaded";
        }
        catch (Exception exception)
        {
            HandleError("Database discovery failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStartDiscovery() =>
        Volatile.Read(ref _discoveryInFlight) == 0 &&
        !string.IsNullOrWhiteSpace(SelectedDatabase);

    [RelayCommand(CanExecute = nameof(CanStartDiscovery))]
    private async Task StartDiscoveryAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedDatabase))
        {
            _errors.ShowRecoverable("Database required", "Select a source database before starting discovery.");
            return;
        }

        if (Interlocked.CompareExchange(ref _discoveryInFlight, 1, 0) != 0)
        {
            _errors.ShowRecoverable(
                "Discovery already running",
                "Wait for the active discovery to complete or cancel it before starting another run.");
            return;
        }

        StartDiscoveryCommand.NotifyCanExecuteChanged();
        RetryDiscoveryCommand.NotifyCanExecuteChanged();
        IsBusy = true;
        Progress = 0;
        Status = "Queued for discovery";
        ClearDiscoveryFailure();
        var request = CreateRequest();
        var definition = new BackgroundOperationDefinition(
            $"Discover {SelectedDatabase}",
            async (context, cancellationToken) =>
            {
                try
                {
                    var reporter = new Progress<DiscoveryProgress>(item =>
                    {
                        context.Report(new OperationProgress(
                            item.Percentage,
                            $"{item.Stage} [{item.QueryId}]: {item.Message}",
                            item.ObjectsDiscovered));
                        _dispatcher.Invoke(() =>
                        {
                            Progress = item.Percentage;
                            Status = $"{item.Stage} [{item.QueryId}]: {item.Message}";
                        });
                    });
                    var snapshot = await _discoveryService.DiscoverAsync(request, reporter, cancellationToken);
                    _dispatcher.Invoke(() =>
                    {
                        ApplySnapshot(snapshot);
                        Status = $"Discovery complete · {snapshot.Objects.Count:N0} objects";
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _dispatcher.Invoke(() =>
                        Status = "Discovery cancelled; commands, readers, and connection released.");
                    throw;
                }
                catch (Exception exception)
                {
                    _dispatcher.Invoke(() => ApplyDiscoveryFailure(exception));
                    throw;
                }
                finally
                {
                    _dispatcher.Invoke(() =>
                    {
                        IsBusy = false;
                        _operationId = null;
                        Interlocked.Exchange(ref _discoveryInFlight, 0);
                        StartDiscoveryCommand.NotifyCanExecuteChanged();
                        RetryDiscoveryCommand.NotifyCanExecuteChanged();
                        CancelDiscoveryCommand.NotifyCanExecuteChanged();
                    });
                }
            },
            $"sqlserver-discovery:{request.Connection.Server}:{request.Connection.Database}");
        try
        {
            _operationId = await _scheduler.EnqueueAsync(definition);
            CancelDiscoveryCommand.NotifyCanExecuteChanged();
        }
        catch
        {
            IsBusy = false;
            Interlocked.Exchange(ref _discoveryInFlight, 0);
            StartDiscoveryCommand.NotifyCanExecuteChanged();
            RetryDiscoveryCommand.NotifyCanExecuteChanged();
            throw;
        }
    }

    private bool CanCancelDiscovery() =>
        Volatile.Read(ref _discoveryInFlight) != 0 && _operationId is not null;

    [RelayCommand(CanExecute = nameof(CanCancelDiscovery))]
    private void CancelDiscovery()
    {
        if (_operationId is { } id && _scheduler.Cancel(id))
        {
            Status = "Cancelling; waiting for SQL command, reader, and connection release.";
        }
    }

    private bool CanRetryLastDiscovery() =>
        CanRetryDiscovery && Volatile.Read(ref _discoveryInFlight) == 0;

    [RelayCommand(CanExecute = nameof(CanRetryLastDiscovery))]
    private Task RetryDiscoveryAsync() => StartDiscoveryAsync();

    [RelayCommand]
    private async Task ExportDiscoveryDiagnosticsAsync()
    {
        if (_discoveryDiagnostics.Current is null)
        {
            _errors.ShowRecoverable(
                "No discovery diagnostics",
                "Run SQL Server discovery before exporting diagnostics.");
            return;
        }

        var path = _dialogs.Save(
            "JSON diagnostic files (*.json)|*.json",
            ".json",
            $"discovery-diagnostic-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        if (path is null)
        {
            return;
        }

        try
        {
            await _discoveryDiagnostics.ExportAsync(path, CancellationToken.None);
            Status = $"Sanitized discovery diagnostic exported to {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            HandleError("Diagnostic export failed", exception);
        }
    }

    private bool CanRunDoctor() =>
        !IsDoctorRunning && !string.IsNullOrWhiteSpace(SelectedDatabase);

    [RelayCommand(CanExecute = nameof(CanRunDoctor))]
    private async Task RunDiscoveryDoctorAsync()
    {
        await RunDoctorCoreAsync(
            new DiscoveryDoctorRequest(DiscoveryDoctorMode.FullDiagnostic),
            "Running every registered catalog query independently, then probing the production pipeline.");
    }

    [RelayCommand(CanExecute = nameof(CanRunDoctor))]
    private async Task RunQuickPreflightAsync()
    {
        await RunDoctorCoreAsync(
            new DiscoveryDoctorRequest(DiscoveryDoctorMode.QuickPreflight),
            "Running required Quick Preflight catalog queries and the exact production mapper pipeline.");
    }

    [RelayCommand(CanExecute = nameof(CanRunDoctor))]
    private async Task RunCompatibilityAuditAsync()
    {
        if (!CanRunDoctor())
        {
            return;
        }
        ActivateDiscoveryDoctor();
        _discoveryDiagnostics.ClearDoctor();
        IsDoctorRunning = true;
        _doctorCancellation?.Dispose();
        _doctorCancellation = new CancellationTokenSource();
        DoctorStatus = "Auditing SQL Server version, compatibility, permissions, and catalog capabilities.";
        NotifyDoctorCommands();
        try
        {
            var audit = await _discoveryDoctor.AuditAsync(
                CreateConnection(),
                _doctorCancellation.Token);
            ApplyCompatibilityAudit(audit);
            DoctorQueries.Clear();
            foreach (var descriptor in _discoveryDoctor.GetCatalog(audit.MajorVersion))
            {
                DoctorQueries.Add(CreatePendingDoctorQuery(descriptor));
            }
            DoctorRegisteredQueryCount = DoctorQueries.Count;
            DoctorSelectedQueryCount = 0;
            DoctorExecutedQueryCount = 0;
            DoctorPassedQueryCount = 0;
            DoctorFailedQueryCount = 0;
            DoctorSkippedQueryCount = 0;
            DoctorStatus =
                $"Compatibility audit completed. {DoctorQueries.Count:N0} catalog queries are registered; none were executed.";
        }
        catch (OperationCanceledException) when (_doctorCancellation.IsCancellationRequested)
        {
            DoctorStatus = "Compatibility audit cancelled.";
        }
        catch (Exception exception)
        {
            DoctorStatus = $"Compatibility audit failed: {_redactor.Redact(exception.Message)}";
            _errors.ShowRecoverable("Compatibility audit failed", DoctorStatus);
        }
        finally
        {
            IsDoctorRunning = false;
            _doctorCancellation.Dispose();
            _doctorCancellation = null;
            NotifyDoctorCommands();
        }
    }

    private bool CanRetryDoctorQuery() =>
        CanRunDoctor() && SelectedDoctorQuery is { Descriptor.IsMetadataOnly: true };

    [RelayCommand(CanExecute = nameof(CanRetryDoctorQuery))]
    private async Task RetryDoctorQueryAsync()
    {
        if (SelectedDoctorQuery is not { } selected)
        {
            return;
        }
        await RunDoctorCoreAsync(
            new DiscoveryDoctorRequest(
                DiscoveryDoctorMode.SelectedQueries,
                new HashSet<string>([selected.Descriptor.QueryId], StringComparer.OrdinalIgnoreCase)),
            $"Retrying only {selected.Descriptor.QueryId}.");
    }

    private bool CanCancelDoctor() => IsDoctorRunning && _doctorCancellation is not null;

    [RelayCommand(CanExecute = nameof(CanCancelDoctor))]
    private void CancelDoctor()
    {
        DoctorStatus = "Cancelling Discovery Doctor; waiting for the active metadata reader to close.";
        _doctorCancellation?.Cancel();
    }

    [RelayCommand]
    private async Task ExportDoctorDiagnosticsAsync()
    {
        if (_discoveryDiagnostics.DoctorReport is null)
        {
            _errors.ShowRecoverable(
                "No Discovery Doctor report",
                "Run the compatibility audit or Discovery Doctor before exporting.");
            return;
        }
        var path = _dialogs.Save(
            "JSON diagnostic files (*.json)|*.json",
            ".json",
            $"discovery-doctor-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");
        if (path is null)
        {
            return;
        }
        try
        {
            await _discoveryDiagnostics.ExportDoctorAsync(path, CancellationToken.None);
            DoctorStatus = $"Sanitized Discovery Doctor report exported to {Path.GetFileName(path)}.";
        }
        catch (Exception exception)
        {
            HandleError("Discovery Doctor export failed", exception);
        }
    }

    private async Task RunDoctorCoreAsync(DiscoveryDoctorRequest request, string initialStatus)
    {
        if (!CanRunDoctor())
        {
            return;
        }
        ActivateDiscoveryDoctor();
        _discoveryDiagnostics.ClearDoctor();
        _doctorCancellation?.Dispose();
        _doctorCancellation = new CancellationTokenSource();
        IsDoctorRunning = true;
        DoctorProgress = 0;
        DoctorStatus = initialStatus;
        NotifyDoctorCommands();
        try
        {
            var reporter = new Progress<DiscoveryDoctorProgress>(item =>
                _dispatcher.Invoke(() =>
                {
                    DoctorProgress = item.Percentage;
                    DoctorCurrentQuery = item.QueryId;
                    DoctorCurrentStage = item.Stage.ToString();
                    DoctorStatus = $"{item.Stage} [{item.QueryId}]: {item.Message}";
                }));
            var report = await _discoveryDoctor.DiagnoseAsync(
                CreateConnection(),
                request,
                reporter,
                _doctorCancellation.Token);
            _dispatcher.Invoke(() => ApplyDoctorReport(report));
        }
        catch (OperationCanceledException) when (_doctorCancellation.IsCancellationRequested)
        {
            DoctorStatus = "Discovery Doctor cancelled; active command, reader, and connection released.";
        }
        catch (Exception exception)
        {
            DoctorStatus = $"Discovery Doctor failed before query isolation completed: {_redactor.Redact(exception.Message)}";
            _errors.ShowRecoverable("Discovery Doctor failed", DoctorStatus);
        }
        finally
        {
            IsDoctorRunning = false;
            _doctorCancellation.Dispose();
            _doctorCancellation = null;
            NotifyDoctorCommands();
        }
    }

    private void ApplyDoctorReport(DiscoveryDoctorReport report)
    {
        ApplyCompatibilityAudit(report.Audit);
        DoctorRegisteredQueryCount = report.RegisteredQueryCount;
        DoctorSelectedQueryCount = report.SelectedQueryCount;
        DoctorExecutedQueryCount = report.ExecutedQueryCount;
        if (report.SelectedQueryCount > 1)
        {
            DoctorQueries.Clear();
        }
        foreach (var result in report.Queries)
        {
            var existing = DoctorQueries.FirstOrDefault(
                item => string.Equals(
                    item.Descriptor.QueryId,
                    result.Descriptor.QueryId,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                DoctorQueries.Remove(existing);
            }
            DoctorQueries.Add(result);
        }
        SelectedDoctorQuery = DoctorQueries.FirstOrDefault(item =>
            item.Status == CatalogDiagnosticStatus.Failed) ??
            DoctorQueries.FirstOrDefault();
        var failed = DoctorQueries.Count(item => item.Status == CatalogDiagnosticStatus.Failed);
        DoctorPassedQueryCount = DoctorQueries.Count(item =>
            item.Status == CatalogDiagnosticStatus.Succeeded);
        DoctorFailedQueryCount = failed;
        DoctorSkippedQueryCount = DoctorQueries.Count(item =>
            item.Status == CatalogDiagnosticStatus.Skipped);
        DoctorProgress = report.Cancelled ? DoctorProgress : 100;
        DoctorStatus = report.ProductionFailureStage is not null
            ? $"Diagnosis: production discovery failed at {report.ProductionFailureStage} " +
              $"[{report.ProductionFailureQueryId}]. {report.ProductionFailureSummary}"
            : $"Doctor completed: {DoctorQueries.Count:N0} queries, {failed:N0} failures. " +
              (report.Audit.Findings.Count == 0
                  ? "No compatibility blockers detected."
                  : string.Join(" ", report.Audit.Findings));
        RetryDoctorQueryCommand.NotifyCanExecuteChanged();
    }

    private void ApplyCompatibilityAudit(DatabaseCompatibilityAudit audit)
    {
        DoctorCapabilities.Clear();
        foreach (var capability in audit.Capabilities)
        {
            DoctorCapabilities.Add(capability);
        }
        DoctorAuditSummary =
            $"SQL Server {audit.ProductVersion} ({audit.Edition}); compatibility {audit.CompatibilityLevel}; " +
            $"{audit.Findings.Count:N0} audit findings.";
    }

    private static CatalogQueryDiagnostic CreatePendingDoctorQuery(
        CatalogQueryDescriptor descriptor) =>
        new(
            descriptor,
            CatalogDiagnosticStatus.Pending,
            0,
            DateTimeOffset.UtcNow,
            null,
            0,
            0,
            0,
            0,
            [],
            null,
            null,
            [new CatalogPhaseDiagnostic(
                CatalogFailurePhase.QuerySelection,
                CatalogDiagnosticStatus.Pending,
                "Registered but not selected for execution.")],
            "Not executed.",
            string.Empty,
            true);

    private void NotifyDoctorCommands()
    {
        RunDiscoveryDoctorCommand.NotifyCanExecuteChanged();
        RunQuickPreflightCommand.NotifyCanExecuteChanged();
        RunCompatibilityAuditCommand.NotifyCanExecuteChanged();
        RetryDoctorQueryCommand.NotifyCanExecuteChanged();
        CancelDoctorCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDoctorQueryChanged(CatalogQueryDiagnostic? value) =>
        RetryDoctorQueryCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private async Task BrowseExcelAsync()
    {
        var path = _dialogs.Open("Excel workbooks (*.xlsx)|*.xlsx");
        if (path is null)
        {
            return;
        }

        try
        {
            ExcelPath = path;
            Worksheets.Clear();
            foreach (var worksheet in await _excelService.GetWorksheetNamesAsync(path, CancellationToken.None))
            {
                Worksheets.Add(worksheet);
            }
            SelectedWorksheet = Worksheets.FirstOrDefault();
            ExcelStatus = $"{Worksheets.Count} worksheets available";
        }
        catch (Exception exception)
        {
            HandleError("Workbook could not be read", exception);
        }
    }

    [RelayCommand]
    private async Task MatchExcelAsync()
    {
        if (_session.Current is null)
        {
            _errors.ShowRecoverable("Inventory required", "Run database discovery before matching an Excel selection.");
            return;
        }

        try
        {
            IsBusy = true;
            _excelCancellation?.Dispose();
            _excelCancellation = new CancellationTokenSource();
            var cancellationToken = _excelCancellation.Token;
            ExcelStatus = "Reading workbookâ€¦";
            var progress = new Progress<ExcelSelectionProgress>(item =>
            {
                ExcelStatus =
                    $"{item.Stage} Â· {item.RowsProcessed:N0}/{item.TotalRows:N0} Â· " +
                    $"{item.Matched:N0} matched Â· {item.Unmatched:N0} unmatched Â· {item.Ambiguous:N0} ambiguous";
            });
            _excelResult = await _excelService.MatchAsync(
                new ExcelTableSelectionOptions(ExcelPath, SelectedWorksheet ?? string.Empty, TableColumn),
                _session.Current.Objects,
                progress,
                cancellationToken);
            UnmatchedExcelRows.Clear();
            AmbiguousExcelRows.Clear();
            foreach (var row in _excelResult.Unmatched)
            {
                UnmatchedExcelRows.Add(row);
            }
            foreach (var row in _excelResult.Ambiguous)
            {
                AmbiguousExcelRows.Add(row);
            }
            ExcelStatus = $"{_excelResult.Matched.Count} matched · {_excelResult.Unmatched.Count} unmatched · " +
                          $"{_excelResult.Ambiguous.Count} ambiguous · {_excelResult.DuplicatesRemoved} duplicates removed";
            ScopeMode = MigrationScopeMode.ExcelSelectedTables;
            ApplyScope();
        }
        catch (OperationCanceledException)
        {
            ExcelStatus = "Excel matching cancelled after workbook resources were released.";
        }
        catch (Exception exception)
        {
            HandleError("Excel selection failed", exception);
        }
        finally
        {
            IsBusy = false;
            _excelCancellation?.Dispose();
            _excelCancellation = null;
        }
    }

    [RelayCommand]
    private void CancelExcel()
    {
        if (_excelCancellation is null)
        {
            return;
        }

        ExcelStatus = "Cancelling Excel matchingâ€¦";
        _excelCancellation.Cancel();
    }

    [RelayCommand]
    private void ApplyScope()
    {
        if (_session.Current is null)
        {
            return;
        }

        var request = CreateRequest();
        var scoped = InventoryScopeSelector.Apply(_session.Current, request);
        ApplySnapshot(scoped);
        Status = $"Scope applied · {scoped.Objects.Count(item => item.IsIncluded):N0} objects included";
    }

    [RelayCommand]
    private async Task SaveInventoryAsync()
    {
        if (_session.Current is null)
        {
            return;
        }
        var path = _dialogs.Save("Migration inventory (*.msinventory)|*.msinventory", ".msinventory", $"{_session.Current.Database.DatabaseName}.msinventory");
        if (path is not null)
        {
            await _snapshotStore.SaveAsync(_session.Current, path, CancellationToken.None);
            Status = $"Inventory saved to {path}";
        }
    }

    [RelayCommand]
    private async Task LoadInventoryAsync()
    {
        var path = _dialogs.Open("Migration inventory (*.msinventory)|*.msinventory");
        if (path is not null)
        {
            ApplySnapshot(await _snapshotStore.LoadAsync(path, CancellationToken.None));
            Status = $"Inventory loaded from {path}";
        }
    }

    [RelayCommand]
    private async Task ExportExcelIssuesAsync()
    {
        if (_excelResult is null)
        {
            return;
        }
        var path = _dialogs.Save("Excel workbooks (*.xlsx)|*.xlsx", ".xlsx", "Excel-selection-issues.xlsx");
        if (path is not null)
        {
            await _excelService.ExportIssuesAsync(_excelResult, path, CancellationToken.None);
        }
    }

    [RelayCommand]
    private async Task StartConversionAsync()
    {
        if (_session.Current is null)
        {
            _errors.ShowRecoverable("Inventory required", "Discover or open an inventory before conversion.");
            return;
        }

        if (Interlocked.CompareExchange(ref _conversionInFlight, 1, 0) != 0)
        {
            return;
        }
        IsBusy = true;
        Progress = 0;
        ConversionProcessed = 0;
        ConversionTotal = 0;
        ConversionObjectsPerSecond = 0;
        ConversionElapsed = TimeSpan.Zero;
        ConversionCurrentStage = "Queued";
        ConversionCurrentObjectType = string.Empty;
        ConversionCurrentObject = string.Empty;
        ConversionLastProgressAt = null;
        ConversionIsResponsive = true;
        ConversionEstimatedRemaining = null;
        ConversionOperationIdentifier = string.Empty;
        ConversionMappingSetIdentifier = string.Empty;
        ConversionStatus = "Queued for conversion";
        DeploymentPackagePath = string.Empty;
        PackagedArtifactCount = 0;
        PackagedExecutableCount = 0;
        PackagedManualReviewCount = 0;
        PackagedUnsupportedCount = 0;
        LiveValidationFailures.Clear();
        LiveValidationProgress = 0;
        LiveValidationStatus = "Conversion changed. Live PostgreSQL validation must run again.";
        var inventory = _session.Current;
        var previousConversion = _conversionSession.Current;
        var options = CreateConversionOptions();
        var definition = new BackgroundOperationDefinition(
            $"Convert {inventory.Database.DatabaseName}",
            async (context, cancellationToken) =>
            {
                try
                {
                    using var tracker = new ConversionOperationProgressTracker(
                        context.OperationId,
                        context.Report,
                        snapshot => _dispatcher.Post(() => ApplyConversionProgress(snapshot)),
                        _logger);
                    var outcome = await tracker.RunAsync(
                        async (progress, workerCancellation) =>
                        {
                            var convertedRun = await _conversionEngine.ConvertAsync(
                                inventory,
                                options,
                                progress,
                                workerCancellation).ConfigureAwait(false);
                            var completedRun =
                                ConversionValidationResultReuse.ReuseUnchangedSuccessfulResults(
                                    convertedRun,
                                    previousConversion);
                            _conversionSession.SetCurrent(completedRun);

                            progress.Report(new ConversionProgress(
                                ConversionStage.CompletingReports,
                                1,
                                1,
                                "Conversion completed. Live PostgreSQL validation is required before package generation.")
                            {
                                MappingSetId = completedRun.MappingSet.MappingSetId,
                                LastProgressAt = DateTimeOffset.UtcNow
                            });
                            return completedRun;
                        },
                        cancellationToken);
                    var run = outcome;
                    var mappingSummary = run.IdentifierMappingSummary;
                    context.Report(new OperationProgress(
                        100,
                        $"Conversion completed. {mappingSummary.AutomaticallyMapped:N0} identifiers mapped; " +
                        $"{mappingSummary.Unresolved:N0} unresolved; awaiting live PostgreSQL validation.",
                        mappingSummary.AutomaticallyMapped,
                        mappingSummary.TotalIncludedObjects));
                    var completionFailures = ConversionCompletionBoundary.Execute(
                        () => { },
                        () => _dispatcher.Invoke(() =>
                    {
                        ApplyConversionRun(run);
                        DeploymentPackagePath = string.Empty;
                        IsBusy = false;
                        ConversionStatus =
                            $"Conversion complete · {run.Artifacts.Count:N0} artifacts · " +
                            $"{mappingSummary.AutomaticallyMapped:N0} identifiers mapped · " +
                            $"{mappingSummary.Unresolved:N0} unresolved · validate SQL to generate the deployment package";
                    }));

                    if (completionFailures.Count > 0)
                    {
                        foreach (var failure in completionFailures)
                        {
                            LogConversionPresentationFailure(
                                failure,
                                run.RunId,
                                run.Artifacts.Count);
                        }

                        try
                        {
                            _dispatcher.Invoke(() =>
                            {
                                IsBusy = false;
                                ConversionStatus =
                                    $"Conversion complete · {run.Artifacts.Count:N0} artifacts preserved; " +
                                    "the result view could not be fully rendered.";
                                _errors.ShowRecoverable(
                                    "Conversion result display failed",
                                    "Conversion completed successfully and its artifacts were preserved. " +
                                    "Reopen the conversion result view or review the application log.");
                            });
                        }
                        catch (Exception failure)
                        {
                            LogConversionPresentationFailure(
                                failure,
                                run.RunId,
                                run.Artifacts.Count);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _dispatcher.Invoke(() =>
                    {
                        IsBusy = false;
                        ConversionStatus = "Conversion cancelled. The last valid inventory was retained; no partial result was published.";
                    });
                    throw;
                }
                catch
                {
                    _dispatcher.Invoke(() => IsBusy = false);
                    throw;
                }
                finally
                {
                    _dispatcher.Invoke(() =>
                    {
                        _conversionOperationId = null;
                        Interlocked.Exchange(ref _conversionInFlight, 0);
                        CancelConversionCommand.NotifyCanExecuteChanged();
                    });
                }
            },
            $"conversion:{inventory.Database.DatabaseName}");
        try
        {
            _conversionOperationId = await _scheduler.EnqueueAsync(definition);
            if (Volatile.Read(ref _conversionInFlight) == 0)
            {
                _conversionOperationId = null;
            }
            CancelConversionCommand.NotifyCanExecuteChanged();
        }
        catch
        {
            Interlocked.Exchange(ref _conversionInFlight, 0);
            IsBusy = false;
            CancelConversionCommand.NotifyCanExecuteChanged();
            throw;
        }
    }

    public bool IsConversionRunning => Volatile.Read(ref _conversionInFlight) != 0;

    private void ApplyConversionProgress(ConversionProgressSnapshot snapshot)
    {
        Progress = snapshot.Percent;
        ConversionStatus = $"{snapshot.Stage}: {snapshot.Message}";
        ConversionProcessed = checked((int)Math.Min(snapshot.Processed, int.MaxValue));
        ConversionTotal = checked((int)Math.Min(snapshot.Total, int.MaxValue));
        ConversionObjectsPerSecond = snapshot.RatePerSecond;
        ConversionElapsed = snapshot.Elapsed;
        ConversionCurrentStage = snapshot.Stage.ToString();
        ConversionCurrentObjectType = snapshot.CurrentObjectType;
        ConversionCurrentObject = snapshot.CurrentObject;
        ConversionLastProgressAt = snapshot.LastProgressAt;
        ConversionIsResponsive = snapshot.IsResponsive;
        ConversionEstimatedRemaining = snapshot.EstimatedRemaining;
        ConversionOperationIdentifier = snapshot.OperationId.ToString();
        ConversionMappingSetIdentifier = snapshot.MappingSetId == Guid.Empty
            ? string.Empty
            : snapshot.MappingSetId.ToString("N");
    }

    private bool CanCancelConversion() =>
        IsConversionRunning && _conversionOperationId is not null;

    [RelayCommand(CanExecute = nameof(CanCancelConversion))]
    private void CancelConversion()
    {
        if (_conversionOperationId is { } id && _scheduler.Cancel(id))
        {
            ConversionStatus = "Cancelling conversion; incomplete mappings and package output will not be published.";
        }
    }

    [RelayCommand]
    private void ViewIdentifierMappings()
    {
        SelectedWorkspaceTabIndex = 6;
        SelectedConversionTabIndex = 2;
    }

    [RelayCommand]
    private async Task ExportMappingReportAsync()
    {
        if (_conversionSession.Current is null)
        {
            _errors.ShowRecoverable(
                "Conversion required",
                "Run conversion before exporting the Identifier Mapping Report.");
            return;
        }

        var directory = _dialogs.SelectFolder(
            "Select a folder for the Identifier Mapping Report");
        if (directory is null)
        {
            return;
        }

        try
        {
            await _conversionReportWriter.WriteAsync(
                _conversionSession.Current,
                directory,
                CancellationToken.None);
            IdentifierMappingStatus =
                $"Identifier Mapping Report exported to {directory}";
        }
        catch (Exception exception)
        {
            HandleError("Identifier Mapping Report export failed", exception);
        }
    }

    [RelayCommand]
    private async Task ExportPackageAsync()
    {
        if (_conversionSession.Current is null)
        {
            _errors.ShowRecoverable("Conversion required", "Run conversion before exporting a deployment package.");
            return;
        }
        var directory = _dialogs.SelectFolder("Select the parent folder for the migration package");
        if (directory is null)
        {
            return;
        }
        try
        {
            IsBusy = true;
            var editedRun = CreateCurrentEditedRun();
            EnsureValidatedForPackageGeneration(editedRun);
            _conversionSession.SetCurrent(editedRun);
            var package = await WriteVerifiedPackageAsync(
                editedRun,
                directory,
                CancellationToken.None);
            DeploymentPackagePath = package;
            ConversionStatus = $"Validated package exported to {package}";
        }
        catch (Exception exception)
        {
            HandleError("Package export failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ValidateOnPostgreSqlAsync(CancellationToken cancellationToken)
    {
        if (_conversionSession.Current is null ||
            string.IsNullOrWhiteSpace(PostgreSqlValidationConnectionString))
        {
            _errors.ShowRecoverable(
                "Validation configuration required",
                "Run conversion and provide an explicitly designated PostgreSQL validation connection.");
            return;
        }
        try
        {
            IsBusy = true;
            DeploymentPackagePath = string.Empty;
            LiveValidationFailures.Clear();
            LiveValidationProgress = 0;
            LiveValidationCompleted = 0;
            LiveValidationPassedCount = 0;
            LiveValidationFailedCount = 0;
            LiveValidationBlockedCount = 0;
            LiveValidationNotRunCount = 0;
            LiveValidationManualReviewCount = 0;
            LiveValidationReusedCount = 0;
            LiveValidationCurrentObject = string.Empty;
            ConversionStatus = "Validating generated SQL in an isolated PostgreSQL environment...";
            LiveValidationStatus = ConversionStatus;
            var current = _conversionSession.Current;
            var presentedArtifacts = ConversionArtifacts.Select(item => item.ToArtifact()).ToArray();
            var artifacts = ConversionArtifactReconciler.OverlayPresentedEdits(
                current.Artifacts,
                presentedArtifacts);
            var executableCount = artifacts.Count(
                ConversionArtifactReconciler.IsDeployableExecutable);
            var reusableCount = artifacts.Count(item =>
                ConversionArtifactReconciler.HasCurrentSuccessfulLiveValidation(item));
            var requiringValidationCount = executableCount - reusableCount;
            LiveValidationTotal = requiringValidationCount;
            LogLiveValidationStarting(
                artifacts.Count,
                executableCount,
                reusableCount,
                requiringValidationCount);
            var validationProgress = new Progress<LiveSqlValidationProgress>(item =>
                _dispatcher.Post(() =>
                {
                    LiveValidationCompleted = item.CompletedArtifacts;
                    LiveValidationTotal = item.TotalArtifacts;
                    LiveValidationCurrentObject = item.CurrentObject;
                    LiveValidationProgress = item.Percentage;
                    LiveValidationStatus = item.Message;
                    ConversionStatus = $"Live validation {item.Percentage:N1}% · {item.Message}";
                }));
            var workflow = await LiveValidationWorkflow.ExecuteAsync(
                current,
                artifacts,
                _generatedSqlValidator,
                new PostgreSqlValidationOptions(PostgreSqlValidationConnectionString)
                {
                    MaintenanceDatabase = MaintenanceDatabase,
                    Progress = validationProgress
                },
                cancellationToken);
            var run = workflow.Run;
            var updated = run.Artifacts;
            _conversionSession.SetCurrent(run);
            ApplyConversionRun(run);
            PopulateLiveValidationFailures(updated);
            LiveValidationPassedCount = workflow.PassedCount;
            LiveValidationFailedCount = workflow.FailedCount;
            LiveValidationBlockedCount = workflow.BlockedCount;
            LiveValidationNotRunCount = workflow.NotRunCount;
            LiveValidationManualReviewCount = workflow.ManualReviewCount;
            LiveValidationReusedCount = workflow.ReusedCount;
            LogLiveValidationCompleted(
                workflow.PassedCount,
                workflow.FailedCount,
                workflow.BlockedCount,
                workflow.ReusedCount,
                workflow.NotRunCount,
                workflow.TotalBefore,
                workflow.TotalAfter);
            var confidence = updated.Select(item => item.Validation.Confidence)
                .DefaultIfEmpty(LiveSqlValidationConfidence.None)
                .Max();
            var invalidDeployableCount = updated.Count(item =>
                ConversionArtifactReconciler.IsDeployableExecutable(item) &&
                !ConversionArtifactReconciler.HasCurrentSuccessfulLiveValidation(item));
            if (invalidDeployableCount == 0)
            {
                var packageRoot = Path.Combine(
                    _applicationPaths.ApplicationDataDirectory,
                    "MigrationPackages");
                var package = await GenerateValidatedPackageCoreAsync(
                    run,
                    packageRoot,
                    cancellationToken);
                DeploymentPackagePath = package;
                LiveValidationStatus =
                    $"Live PostgreSQL validation passed ({confidence}); validated package generated.";
                ConversionStatus = $"{LiveValidationStatus} Package: {package}";
            }
            else
            {
                LiveValidationStatus =
                    $"Live PostgreSQL validation completed with " +
                    $"{workflow.FailedCount:N0} failures, {workflow.BlockedCount:N0} dependency-blocked " +
                    $"and {workflow.NotRunCount:N0} not-run artifacts ({confidence}).";
                ConversionStatus = LiveValidationStatus;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LiveValidationStatus = "Live PostgreSQL validation cancelled. No package was published.";
            ConversionStatus = LiveValidationStatus;
            throw;
        }
        catch (Exception exception)
        {
            HandleError("Live PostgreSQL validation failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task GenerateValidatedPackageAsync(CancellationToken cancellationToken)
    {
        if (_conversionSession.Current is null)
        {
            _errors.ShowRecoverable("Conversion required", "Run conversion and live validation first.");
            return;
        }

        try
        {
            IsBusy = true;
            var run = CreateCurrentEditedRun();
            _conversionSession.SetCurrent(run);
            var packageRoot = Path.Combine(
                _applicationPaths.ApplicationDataDirectory,
                "MigrationPackages");
            DeploymentPackagePath = await GenerateValidatedPackageCoreAsync(
                run,
                packageRoot,
                cancellationToken);
            ConversionStatus = $"Validated package generated: {DeploymentPackagePath}";
        }
        catch (Exception exception)
        {
            HandleError("Validated package generation failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportLiveValidationReportAsync()
    {
        if (_conversionSession.Current is null)
        {
            return;
        }

        var directory = _dialogs.SelectFolder("Select a folder for the live PostgreSQL validation report");
        if (directory is null)
        {
            return;
        }

        await _conversionReportWriter.WriteAsync(
            _conversionSession.Current,
            directory,
            CancellationToken.None);
        LiveValidationStatus = $"Validation report exported to {directory}";
    }

    private ConversionRun CreateCurrentEditedRun()
    {
        var current = _conversionSession.Current ??
            throw new InvalidOperationException("Run conversion before validating or packaging.");
        var presented = ConversionArtifacts.Select(item => item.ToArtifact()).ToArray();
        return current with
        {
            Artifacts = ConversionArtifactReconciler.OverlayPresentedEdits(
                current.Artifacts,
                presented)
        };
    }

    internal static void EnsureAllDeployableArtifactsValidated(ConversionRun run)
    {
        var missing =
            ConversionArtifactReconciler.GetArtifactsWithoutCurrentSuccessfulLiveValidation(
                run.Artifacts);
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"{missing.Count:N0} executable artifacts require successful live PostgreSQL validation before package generation.");
        }
    }

    private async Task<string> GenerateValidatedPackageCoreAsync(
        ConversionRun run,
        string parentDirectory,
        CancellationToken cancellationToken)
    {
        EnsureValidatedForPackageGeneration(run);
        return await WriteVerifiedPackageAsync(
            run,
            parentDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    private void EnsureValidatedForPackageGeneration(ConversionRun run)
    {
        var missing =
            ConversionArtifactReconciler.GetArtifactsWithoutCurrentSuccessfulLiveValidation(
                run.Artifacts);
        if (missing.Count > 0)
        {
            LogPackageExportBlocked(
                missing.Count,
                string.Join(
                    ", ",
                    missing.Take(25).Select(item =>
                        item.TargetObjectId.QualifiedName)));
        }
        EnsureAllDeployableArtifactsValidated(run);
    }

    private async Task<string> WriteVerifiedPackageAsync(
        ConversionRun run,
        string parentDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(parentDirectory);
        var package = await _packageWriter.WriteAsync(
            run,
            parentDirectory,
            cancellationToken).ConfigureAwait(false);
        var manifest = await _packageReader.ReadAndVerifyAsync(
            package,
            diagnosticMode: false,
            cancellationToken).ConfigureAwait(false);
        var invalid = manifest.Artifacts.Count(item =>
            item.IsExecutable &&
            !item.RequiresManualReview &&
            item.Classification != ConversionClassification.Unsupported &&
            (item.LiveValidation.Outcome != LiveSqlValidationOutcome.Passed ||
             !item.LiveValidation.WasLiveValidated ||
             !item.LiveValidation.IsStructurallyValid ||
             !string.Equals(
    item.LiveValidation.ValidatedSqlHash,
    item.SqlSha256,
    StringComparison.OrdinalIgnoreCase)));
        if (invalid > 0)
        {
            throw new InvalidDataException(
                $"Generated package verification found {invalid:N0} executable artifacts without successful live validation.");
        }

        var runIds = run.Artifacts.Select(item => item.SourceObjectId).ToHashSet();
        var manifestIds = manifest.Artifacts.Select(item => item.SourceObjectId).ToHashSet();
        if (run.Artifacts.Count != manifest.Artifacts.Count ||
            !runIds.SetEquals(manifestIds))
        {
            throw new InvalidDataException(
                "Generated package lost conversion artifacts: " +
                $"conversion={run.Artifacts.Count:N0}, packaged={manifest.Artifacts.Count:N0}.");
        }

        _dispatcher.Invoke(() =>
        {
            PackagedArtifactCount = manifest.Artifacts.Count;
            PackagedExecutableCount = manifest.Artifacts.Count(item => item.IsExecutable);
            PackagedManualReviewCount = manifest.Artifacts.Count(item => item.RequiresManualReview);
            PackagedUnsupportedCount = manifest.Artifacts.Count(item =>
                item.Classification == ConversionClassification.Unsupported);
        });

        return package;
    }

    private void PopulateLiveValidationFailures(
        IReadOnlyList<ConversionArtifact> artifacts)
    {
        var namesBySourceId = artifacts
            .GroupBy(item => item.SourceObjectId)
            .ToDictionary(
                group => group.Key,
                group => string.Join(
                    " / ",
                    group.Select(item => item.TargetObjectId.QualifiedName)
                        .Distinct(StringComparer.Ordinal)
                        .Order(StringComparer.Ordinal)));
        LiveValidationFailures.ReplaceAll(artifacts
            .Where(item => item.Validation.Outcome is
                LiveSqlValidationOutcome.Failed or
                LiveSqlValidationOutcome.BlockedByDependency)
            .Select(item => new LiveSqlValidationFailureViewModel(
                item.TargetObjectId.QualifiedName,
                item.ScriptFileName,
                item.PostgreSqlDefinition,
                item.Validation.Message ?? "Blocked by dependency.",
                item.Validation.SqlState ?? string.Empty,
                LineFromPosition(item.PostgreSqlDefinition, item.Validation.ErrorPosition),
                string.Join(
                    ", ",
                    (item.Validation.BlockingDependencies.Count > 0
                        ? item.Validation.BlockingDependencies
                        : item.Dependencies)
                    .Select(dependency =>
                        namesBySourceId.GetValueOrDefault(dependency) ?? dependency.ToString())),
                SuggestedValidationFix(item.Validation))));
        SelectedLiveValidationFailure = LiveValidationFailures.FirstOrDefault();
    }

    private static int? LineFromPosition(string sql, int? position)
    {
        if (position is null or <= 0)
        {
            return null;
        }

        return 1 + sql.Take(Math.Min(position.Value - 1, sql.Length))
            .Count(character => character == '\n');
    }

    private static string SuggestedValidationFix(SqlValidationResult result) =>
        result.Outcome == LiveSqlValidationOutcome.BlockedByDependency
            ? "Fix and revalidate the failed prerequisite; unchanged successful artifacts will be reused."
            : result.SqlState switch
            {
                "42P01" => "Verify the referenced table or view mapping and dependency ordering.",
                "42703" => "Verify the referenced column mapping and generated identifier.",
                "42883" => "Verify the PostgreSQL routine signature and required extension.",
                "42601" => "Correct the generated PostgreSQL syntax for this artifact, then revalidate.",
                "42501" => "Grant the validation role the required object privilege.",
                _ => result.Hint ?? "Review the PostgreSQL detail and generated SQL, then revalidate this artifact."
            };

    [RelayCommand]
    private async Task PreviewDataPlanAsync()
    {
        try
        {
            await EnsureCurrentIdentifierMappingSetAsync();
            var request = CreateDataMigrationRequest(DataMigrationExecutionMode.Preview);
            var plan = _dataMigrationPlanner.CreatePlan(request);
            ApplyDataPlan(plan);
            ApplyRecoveredIdentifierMappings(plan);
            DataMigrationStatus =
                $"Plan ready · {plan.Tables.Count:N0} tables · " +
                $"{plan.Tables.Sum(item => item.EstimatedRows):N0} estimated rows" +
                RecoveryStatus(plan);
        }
        catch (Exception exception)
        {
            HandleError("Data migration plan failed", exception);
        }

        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task StartDataMigrationAsync()
    {
        await QueueDataMigrationAsync(resume: false);
    }

    [RelayCommand]
    private async Task ResumeDataMigrationAsync()
    {
        if (!Guid.TryParse(MigrationRunId, out _))
        {
            _errors.ShowRecoverable(
                "Run ID required",
                "Enter or retain a valid checkpoint run ID before resuming.");
            return;
        }

        await QueueDataMigrationAsync(resume: true);
    }

    [RelayCommand]
    private void PauseDataMigration()
    {
        _migrationPauseController.Pause();
        IsMigrationPaused = true;
        DataMigrationStatus = "Pause requested; active batches will stop at a safe row boundary.";
    }

    [RelayCommand]
    private void ContinueDataMigration()
    {
        _migrationPauseController.Unpause();
        IsMigrationPaused = false;
        DataMigrationStatus = "Migration resumed.";
    }

    [RelayCommand]
    private void CancelDataMigration()
    {
        if (_dataOperationId is { } id && _scheduler.Cancel(id))
        {
            DataMigrationStatus = "Cancellation requested; completed batches remain checkpointed.";
        }
    }

    [RelayCommand]
    private async Task RestartDataTableAsync()
    {
        if (SelectedDataTable is null || !Guid.TryParse(MigrationRunId, out var runId))
        {
            return;
        }

        await _dataMigrationEngine.RestartTableAsync(
            runId,
            SelectedDataTable.TableId,
            CancellationToken.None);
        SelectedDataTable.State = TableMigrationState.Pending;
        SelectedDataTable.RowsRead = 0;
        SelectedDataTable.RowsWritten = 0;
        SelectedDataTable.RowsRejected = 0;
        DataMigrationStatus = $"{SelectedDataTable.Table} checkpoint cleared. Target preparation applies on restart.";
    }

    [RelayCommand]
    private async Task ExportDataReportAsync()
    {
        if (_dataMigrationResult is null)
        {
            _errors.ShowRecoverable("Migration result required", "Complete or cancel a data migration before exporting its report.");
            return;
        }

        var directory = _dialogs.SelectFolder("Select a folder for the data migration report");
        if (directory is null)
        {
            return;
        }

        await _dataMigrationReportWriter.WriteAsync(
            _dataMigrationResult,
            directory,
            CancellationToken.None);
        DataMigrationStatus = $"Data migration report written to {directory}";
    }

    private async Task QueueDataMigrationAsync(bool resume)
    {
        try
        {
            var deploymentConnection = CreateDeploymentConnection();

            await _deploymentConnectionService.AssessAsync(
                deploymentConnection,
                DeploymentMode == DeploymentMode.CreateDatabaseAndDeploy,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _errors.ShowRecoverable(
                "PostgreSQL deployment connection required",
                ex.Message);

            return;
        }

        try
        {
            await EnsureCurrentIdentifierMappingSetAsync();
            var request = CreateDataMigrationRequest(
                DataExecutionMode,
                resume ? Guid.Parse(MigrationRunId) : null);
            var plan = _dataMigrationPlanner.CreatePlan(request);
            ApplyDataPlan(plan);
            ApplyRecoveredIdentifierMappings(plan);
            IsBusy = true;
            DataMigrationStatus = resume
                ? $"Queued resume for {plan.RunId}{RecoveryStatus(plan)}"
                : $"Queued migration {plan.RunId}{RecoveryStatus(plan)}";
                        var deployment = CreateDeploymentConnection();

                        var definition = new BackgroundOperationDefinition(
                $"Migrate data to {deployment.TargetDatabase}",
                            async (context, cancellationToken) =>
                {
                    try
                    {
                        var reporter = new Progress<DataMigrationProgress>(item =>
                        {
                            long totalUnits = item.EstimatedRows;

                            if (totalUnits <= 0)
                            {
                                totalUnits = item.RowsWritten;
                            }

                            context.Report(new OperationProgress(
                                item.Percentage,
                                item.Message,
                                item.RowsWritten,
                                totalUnits));

                            _dispatcher.Invoke(() => ApplyDataProgress(item));
                        });
                        var result = resume
                            ? await _dataMigrationEngine.ResumeAsync(
                                request,
                                reporter,
                                cancellationToken)
                            : await _dataMigrationEngine.ExecuteAsync(
                                request,
                                reporter,
                                cancellationToken);
                        _dispatcher.Invoke(() =>
                        {
                            ApplyDataResult(result);
                            IsBusy = false;
                        });
                        if (result.State is MigrationRunState.Failed or
                            MigrationRunState.CompletedWithFailures ||
                            result.Failures.Any(item =>
                                item.Disposition is FailureDisposition.TableStopped or
                                    FailureDisposition.MigrationStopped))
                        {
                            throw new InvalidOperationException(
                                $"Data migration ended as {result.State} with " +
                                $"{result.Failures.Count:N0} recorded failures. " +
                                "Review the retained checkpoint and streaming diagnostics.");
                        }
                    }
                    catch (Exception exception)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            IsBusy = false;
                            if (exception is DataMigrationTargetReadinessException readinessException)
                            {
                                ApplyTargetReadiness(readinessException.Readiness);
                            }
                            else
                            {
                                DataMigrationStatus =
                                    "Data migration failed. Review operation logs and the last checkpoint.";
                            }
                        });
                        throw;
                    }
                });
            _dataOperationId = await _scheduler.EnqueueAsync(definition);
        }
        catch (Exception exception)
        {
            IsBusy = false;
            HandleError("Data migration could not start", exception);
        }
    }

    private async Task EnsureCurrentIdentifierMappingSetAsync()
    {
        if (_session.Current is null || _conversionSession.Current is null)
        {
            throw new InvalidOperationException(
                "Discover and convert the inventory before planning data migration.");
        }

        var current = _conversionSession.Current;
        if (current.MappingSet.SchemaVersion == IdentifierMappingSchema.CurrentVersion &&
            current.MappingSet.IncludedColumnCount == current.MappingSet.MappedColumnCount)
        {
            return;
        }

        DataMigrationStatus =
            $"Rebuilding stale identifier mapping set v{current.MappingSet.SchemaVersion}…";
        var rebuilt = await _conversionEngine.ConvertAsync(
            _session.Current,
            CreateConversionOptions(),
            null,
            CancellationToken.None);
        _conversionSession.SetCurrent(rebuilt);
        ApplyConversionRun(rebuilt);
        var diagnosticMapping = rebuilt.IdentifierMappings.FirstOrDefault(item =>
            item.SourceKey.ColumnKey is not null &&
            item.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
            item.SourceName.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase) &&
            item.ParentObject.Contains("verify_observe1819", StringComparison.OrdinalIgnoreCase));
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var details =
                $"Rebuild; ObjectId={diagnosticMapping?.SourceKey.ObjectId}; " +
                $"ParentTableObjectId={diagnosticMapping?.SourceKey.ParentObjectId}; " +
                $"ColumnId={diagnosticMapping?.SourceKey.ColumnId}; " +
                $"Schema={diagnosticMapping?.SourceSchema ?? "nrega_SK"}; Table=verify_observe1819; " +
                $"Column=discre_obsrv; CanonicalKey={diagnosticMapping?.SourceKey.ColumnKey?.ToString() ?? string.Empty}; " +
                $"TargetIdentifier={diagnosticMapping?.TargetName ?? string.Empty}; " +
                $"MappingSetId={rebuilt.MappingSet.MappingSetId}; MappingVersion={rebuilt.MappingSet.SchemaVersion}; " +
                $"Exists={diagnosticMapping is not null}; Included={diagnosticMapping?.IncludedInScope ?? false}; " +
                $"LoadedFromCache={rebuilt.MappingSet.LoadedFromCache}";
            LogIdentifierLifecycle(details);
        }
        DataMigrationStatus =
            $"Identifier mapping set rebuilt · {rebuilt.MappingSet.MappedColumnCount:N0} columns mapped";
    }

    private string CreateDataMigrationTargetConnectionString()
    {
        var deployment = CreateDeploymentConnection();

        if (string.IsNullOrWhiteSpace(deployment.Host))
        {
            throw new InvalidOperationException(
                "PostgreSQL deployment host is required.");
        }

        if (string.IsNullOrWhiteSpace(deployment.TargetDatabase))
        {
            throw new InvalidOperationException(
                "PostgreSQL target database is required.");
        }

        if (string.IsNullOrWhiteSpace(deployment.Username))
        {
            throw new InvalidOperationException(
                "PostgreSQL username is required.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = deployment.Host,
            Port = deployment.Port,
            Database = deployment.TargetDatabase,
            Username = deployment.Username,
            Password = deployment.Password,
            SslMode = Enum.TryParse<SslMode>(
                deployment.SslMode,
                true,
                out var sslMode)
                    ? sslMode
                    : SslMode.Prefer,
            RootCertificate = deployment.RootCertificate,
            SslCertificate = deployment.ClientCertificate,
            CommandTimeout = deployment.CommandTimeoutSeconds,
            ApplicationName = "MigrationStudio.DataMigration"
        }.ConnectionString;
    }

    private DataMigrationRequest CreateDataMigrationRequest(
       DataMigrationExecutionMode executionMode,
       Guid? resumeRunId = null)
    {
        if (_session.Current is null ||
            _conversionSession.Current is null)
        {
            throw new InvalidOperationException(
                "Discover and convert the inventory before planning data migration.");
        }

        var targetConnectionString =
            CreateDataMigrationTargetConnectionString();

        var options = new DataMigrationOptions
        {
            MigrationMode = DataMigrationMode,
            ExecutionMode = executionMode,
            ParallelismMode = DataParallelismMode,
            MaximumConcurrentTables = MaximumConcurrentTables,
            MaximumConcurrentReaders = MaximumConcurrentReaders,
            MaximumConcurrentWriters = MaximumConcurrentWriters,
            BatchRowCount = BatchRowCount,
            BatchByteSize = BatchByteSize,
            CommandTimeoutSeconds = CommandTimeoutSeconds,
            LoadOrdering = LoadOrdering,
            TargetPreparation = TargetPreparation,
            FailurePolicy = MigrationFailurePolicy,
            IsDestructiveTargetPreparationConfirmed =
                DestructivePreparationConfirmed,
            Validation = new ValidationOptions
            {
                CompareRowCounts = true,
                CompareNullCounts = ValidateNullCounts,
                ChecksumMode = ChecksumMode
            }
        };

        return new DataMigrationRequest(
            _session.Current,
            _conversionSession.Current,
            CreateConnection(),
            targetConnectionString,
            options,
            resumeRunId,
            null);
    }

    private void ApplyDataPlan(DataMigrationPlan plan)
    {
        MigrationRunId = plan.RunId.ToString();
        DataTables.Clear();
        foreach (var table in plan.Tables)
        {
            DataTables.Add(new DataMigrationTableRowViewModel(table));
        }

        DataFailures.Clear();
        DataValidations.Clear();
        SequenceResets.Clear();
        TotalRowsRead = 0;
        TotalRowsWritten = 0;
        TotalRowsRejected = 0;
        StreamingCurrentStage = "Plan loaded";
        StreamingCurrentTable = string.Empty;
        StreamingCurrentBatch = 0;
        StreamingCurrentReader = string.Empty;
        StreamingCurrentWriter = string.Empty;
        StreamingLastSuccessfulStage = string.Empty;
        StreamingFailureStage = string.Empty;
        StreamingFailureComponent = string.Empty;
        StreamingFailureReason = string.Empty;
        StreamingRemediation = string.Empty;
    }

    private void ApplyRecoveredIdentifierMappings(DataMigrationPlan plan)
    {
        if (plan.RecoveredIdentifierMappings.Count == 0 ||
            _conversionSession.Current is not { } current)
        {
            return;
        }

        var mappings = current.IdentifierMappings
            .Concat(plan.RecoveredIdentifierMappings)
            .GroupBy(item => item.SourceKey)
            .Select(group => group.Last())
            .ToArray();
        var includedTableIds = _session.Current?.Tables
            .Select(item => item.ObjectId)
            .ToHashSet() ?? [];
        var mappedColumnCount = mappings
            .Where(item =>
                item.IncludedInScope &&
                item.SourceKey.ColumnKey is { } key &&
                includedTableIds.Contains(key.TableObjectId))
            .Select(item => item.SourceKey.ColumnKey!.Value)
            .Distinct()
            .Count();
        var updated = current with
        {
            IdentifierMappings = mappings,
            MappingSet = current.MappingSet with
            {
                PublishedAt = DateTimeOffset.UtcNow,
                PublishedMapCount = mappings.Length,
                MappedColumnCount = mappedColumnCount
            }
        };
        _conversionSession.SetCurrent(updated);
        foreach (var mapping in plan.RecoveredIdentifierMappings)
        {
            IdentifierMappings.Add(mapping);
            if (mapping.SourceSchema.Equals("nrega_SK", StringComparison.OrdinalIgnoreCase) &&
                mapping.SourceName.Equals("discre_obsrv", StringComparison.OrdinalIgnoreCase) &&
                mapping.ParentObject.Contains("verify_observe1819", StringComparison.OrdinalIgnoreCase))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    var details =
                        $"Replace; ObjectId={mapping.SourceKey.ObjectId}; " +
                        $"ParentTableObjectId={mapping.SourceKey.ParentObjectId}; ColumnId={mapping.SourceKey.ColumnId}; " +
                        $"Schema={mapping.SourceSchema}; Table=verify_observe1819; Column={mapping.SourceName}; " +
                        $"CanonicalKey={mapping.SourceKey.ColumnKey?.ToString() ?? string.Empty}; " +
                        $"TargetIdentifier={mapping.TargetName}; MappingSetId={updated.MappingSet.MappingSetId}; " +
                        $"MappingVersion={updated.MappingSet.SchemaVersion}; Exists=True; " +
                        $"Included={mapping.IncludedInScope}; LoadedFromCache={updated.MappingSet.LoadedFromCache}";
                    LogIdentifierLifecycle(details);
                }
            }
        }
        UpdateIdentifierMappingStatus(updated);
    }

    private static string RecoveryStatus(DataMigrationPlan plan) =>
        plan.RecoveredIdentifierMappings.Count == 0
            ? string.Empty
            : $" · {plan.RecoveredIdentifierMappings.Count:N0} missing identifier mapping " +
              (plan.RecoveredIdentifierMappings.Count == 1 ? "was" : "were") +
              " regenerated automatically. Details were added to the Identifier Mapping Report.";

    private void ApplyDataProgress(DataMigrationProgress progress)
    {
        var table = DataTables.FirstOrDefault(item => item.TableId == progress.TableId);
        table?.Apply(progress);
        TotalRowsRead = DataTables.Sum(item => item.RowsRead);
        TotalRowsWritten = DataTables.Sum(item => item.RowsWritten);
        TotalRowsRejected = DataTables.Sum(item => item.RowsRejected);
        var active = DataTables.Where(item => item.State == TableMigrationState.Running).ToArray();
        DataRowsPerSecond = active.Sum(item => item.RowsPerSecond);
        DataBytesPerSecond = active.Sum(item => item.BytesPerSecond);
        ActiveTables = progress.ActiveTables;
        ActiveReaders = progress.ActiveReaders;
        ActiveWriters = progress.ActiveWriters;
        StreamingCurrentStage = progress.StreamingStage is null
            ? progress.Stage
            : $"Stage {(int)progress.StreamingStage}: {progress.StreamingStage}";
        StreamingCurrentTable = progress.CurrentTable ?? StreamingCurrentTable;
        StreamingCurrentBatch = progress.CurrentBatch;
        StreamingCurrentReader = progress.CurrentReader ?? string.Empty;
        StreamingCurrentWriter = progress.CurrentWriter ?? string.Empty;
        StreamingLastSuccessfulStage = progress.LastSuccessfulStage?.ToString() ??
            StreamingLastSuccessfulStage;
        if (progress.FailureStage is not null)
        {
            StreamingFailureStage =
                $"Stage {(int)progress.FailureStage}: {progress.FailureStage}";
            StreamingFailureComponent = progress.FailureComponent ?? string.Empty;
            StreamingFailureReason = progress.FailureReason ?? string.Empty;
            StreamingRemediation = progress.Remediation ?? string.Empty;
        }
        DataMigrationStatus = progress.Message;
    }
    private void ApplyDataResult(DataMigrationResult result)
    {
        _dataMigrationResult = result;

        foreach (var metric in result.Tables)
        {
            DataTables
                .FirstOrDefault(item => item.TableId == metric.TableId)
                ?.Apply(metric);
        }

        DataFailures.Clear();
        foreach (var failure in result.Failures)
        {
            DataFailures.Add(failure);
        }

        DataValidations.Clear();
        foreach (var validation in result.Validations)
        {
            DataValidations.Add(validation);

            var table = DataTables.FirstOrDefault(item =>
                item.Table.Equals(
                    validation.Table,
                    StringComparison.OrdinalIgnoreCase));

            if (table is not null)
            {
                table.Validation = validation.Outcome;
            }
        }

        SequenceResets.Clear();
        foreach (var reset in result.SequenceResets)
        {
            SequenceResets.Add(reset);
        }

        TotalRowsRead = result.Tables.Sum(item => item.RowsRead);
        TotalRowsWritten = result.Tables.Sum(item => item.RowsWritten);
        TotalRowsRejected = result.Tables.Sum(item => item.RowsRejected);

        DataRowsPerSecond = 0;
        DataBytesPerSecond = 0;
        ActiveTables = 0;
        ActiveReaders = 0;
        ActiveWriters = 0;

        var failedStage = result.StreamingStages
            .Where(item => item.Outcome == StreamingStageOutcome.Failed)
            .OrderBy(item => item.StartedAt)
            .FirstOrDefault();

        if (failedStage is not null)
        {
            StreamingFailureStage =
                $"Stage {(int)failedStage.Stage}: {failedStage.Stage}";

            StreamingFailureComponent =
                failedStage.FailureComponent ?? string.Empty;

            StreamingFailureReason =
                failedStage.FailureReason ?? string.Empty;

            StreamingRemediation =
                failedStage.Remediation ?? string.Empty;

            StreamingCurrentTable =
                failedStage.SourceTable ?? string.Empty;

            DataMigrationStatus =
                $"{result.State} at {StreamingFailureStage} " +
                $"for {StreamingCurrentTable}: " +
                $"{StreamingFailureReason} " +
                $"Remediation: {StreamingRemediation}";
        }
        else
        {
            StreamingCurrentStage = result.State.ToString();

            DataMigrationStatus =
                $"{result.State} · " +
                $"{TotalRowsWritten:N0} rows written · " +
                $"checkpoint {result.CheckpointPath}";
        }
    }
    private void ApplyTargetReadiness(DataMigrationTargetReadiness readiness)
    {
        TargetExpectedTables = readiness.ExpectedTables;
        TargetExistingTables = readiness.ExistingTables;
        TargetMissingTables = readiness.MissingTables;
        TargetExpectedColumns = readiness.ExpectedColumns;
        TargetExistingColumns = readiness.ExistingColumns;
        TargetMissingColumns = readiness.MissingColumns;
        StreamingFailureStage = "Target readiness precheck";
        StreamingFailureComponent = "PostgreSQL catalog";
        StreamingFailureReason =
            $"{readiness.MissingSchemas:N0} schemas, {readiness.MissingTables:N0} tables, and " +
            $"{readiness.MissingColumns:N0} columns are missing.";
        StreamingRemediation = "Deploy or repair the target schema, then retry data migration.";
        DataMigrationStatus =
            $"Target readiness blocked migration: tables {readiness.ExistingTables:N0}/" +
            $"{readiness.ExpectedTables:N0}, columns {readiness.ExistingColumns:N0}/" +
            $"{readiness.ExpectedColumns:N0}.";
    }

    [RelayCommand]
    private void BrowseDeploymentPackage()
    {
        var directory = _dialogs.SelectFolder("Select a generated migration package");
        if (directory is not null)
        {
            DeploymentPackagePath = directory;
            DeploymentStatus = "Package selected. Run the pre-deployment assessment.";
        }
    }

    [RelayCommand]
    private async Task TestDeploymentConnectionAsync()
    {
        try
        {
            IsBusy = true;
            var capability = await _deploymentConnectionService.AssessAsync(
                CreateDeploymentConnection(),
                DeploymentMode == DeploymentMode.CreateDatabaseAndDeploy,
                CancellationToken.None);
            DeploymentServerVersion = capability.ServerVersion ?? string.Empty;
            DeploymentStatus =
                $"Connected to PostgreSQL {capability.ServerVersion} as {capability.CurrentUser}; " +
                $"create database: {capability.CanCreateDatabase}";
            DeploymentExtensions.Clear();
            foreach (var extension in capability.InstalledExtensions
                         .Select(item => $"{item.Key} {item.Value}"))
            {
                DeploymentExtensions.Add(extension);
            }
        }
        catch (Exception exception)
        {
            HandleError("PostgreSQL deployment connection failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AssessDeploymentAsync()
    {
        try
        {
            IsBusy = true;
            DeploymentStatus = "Running pre-deployment assessment…";
            await EnsureDeploymentPackageIsCurrentAsync(CancellationToken.None);
            var assessment = await _deploymentEngine.AssessAsync(
                CreateDeploymentRequest(),
                CancellationToken.None);
            ApplyDeploymentAssessment(assessment);
        }
        catch (Exception exception)
        {
            HandleError("Pre-deployment assessment failed", exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task StartDeploymentAsync() => QueueDeploymentAsync(DeploymentExecution.Start);

    [RelayCommand]
    private Task ResumeDeploymentAsync()
    {
        if (!Guid.TryParse(DeploymentId, out _))
        {
            _errors.ShowRecoverable("Deployment ID required", "Enter or retain a valid deployment journal ID.");
            return Task.CompletedTask;
        }

        return QueueDeploymentAsync(DeploymentExecution.Resume);
    }

    [RelayCommand]
    private Task RetryFailedDeploymentObjectsAsync()
    {
        if (_deploymentResult is null ||
            !_deploymentResult.Objects.Any(item => item.Status == DeploymentObjectStatus.Failed))
        {
            return Task.CompletedTask;
        }

        return QueueDeploymentAsync(DeploymentExecution.RetryFailed);
    }

    [RelayCommand]
    private void CancelDeployment()
    {
        if (_deploymentOperationId is not { } operationId)
        {
            return;
        }

        if (_scheduler.Cancel(operationId))
        {
            DeploymentStatus =
                "Cancellation requested. Waiting for the active " +
                "PostgreSQL command and journal operation to finish.";

            NotifyDeploymentCommands();
        }
    }

    [RelayCommand]
    private async Task ExportDeploymentReportAsync()
    {
        if (_deploymentResult is null)
        {
            _errors.ShowRecoverable("Deployment result required", "Run or resume a deployment before exporting.");
            return;
        }

        var directory = _dialogs.SelectFolder("Select a folder for the deployment report");
        if (directory is null)
        {
            return;
        }

        await _deploymentReportWriter.WriteAsync(
            _deploymentResult,
            directory,
            CancellationToken.None);
        DeploymentStatus = $"Deployment report written to {directory}";
    }

    [RelayCommand]
    private async Task RunValidationAsync()
    {
        if (_session.Current is null || _conversionSession.Current is null)
        {
            _errors.ShowRecoverable(
                "Inventory and conversion required",
                "Discover and convert the source database before running post-migration validation.");
            return;
        }

        try
        {
            var request = CreateValidationRequest();
            IsBusy = true;
           // ValidationStatus = "Validation queued.";
            ValidationStatus =
    "Validation queued. Foreign keys will be added and validated first.";
            /*            var definition = new BackgroundOperationDefinition(
                            $"Validate {_session.Current.Database.DatabaseName}",
                            async (context, cancellationToken) =>
                            {
                                try
                                {
                                    var reporter = new Progress<ValidationProgress>(item =>
                                    {
                                        context.Report(new OperationProgress(
                                            item.Percentage, $"{item.Stage}: {item.CurrentObject}",
                                            item.Completed, item.Total));
                                        _dispatcher.Invoke(() =>
                                        {
                                            ValidationProgress = item.Percentage;
                                            ValidationCurrentObject = item.CurrentObject;
                                            ValidationStatus = item.Stage;
                                        });
                                    });
                                    var result = await _validationEngine.ValidateAsync(
                                        request, reporter, cancellationToken);
                                    var journal = await _validationRunStore.SaveAsync(result, cancellationToken);
                                    _dispatcher.Invoke(() =>
                                    {
                                        ApplyValidationResult(result);
                                        ValidationStatus =
                                            $"{result.Readiness.OverallStatus} · persisted to {journal}";
                                        IsBusy = false;
                                    });
                                }
                                catch
                                {
                                    _dispatcher.Invoke(() =>
                                    {
                                        IsBusy = false;
                                        ValidationStatus = "Validation stopped. Review the error log and retained results.";
                                    });
                                    throw;
                                }
                            });
            */

            var definition = new BackgroundOperationDefinition(
    $"Validate {_session.Current.Database.DatabaseName}",
    async (context, cancellationToken) =>
    {
        try
        {
            /*
             * Stage 1:
             * Add foreign keys as NOT VALID and then validate them.
             */
            var deploymentReporter =
                new Progress<DeploymentProgress>(item =>
                {
                    var total = Math.Max(0, item.Total);
                    var completed = Math.Clamp(
                        item.Completed,
                        0,
                        Math.Max(total, item.Completed));

                    context.Report(new OperationProgress(
                        Math.Clamp(item.Percentage, 0, 100),
                        $"Constraints: {item.Message}",
                        completed,
                        Math.Max(total, completed)));

                    _dispatcher.Invoke(() =>
                    {
                        ValidationProgress =
                            Math.Clamp(item.Percentage, 0, 100) * 0.25;

                        ValidationCurrentObject =
                            item.CurrentObject;

                        ValidationStatus =
                            $"Foreign keys: {item.Message}";
                    });
                });

            var constraintDeployment =
                await DeployAndValidateForeignKeysAsync(
                    deploymentReporter,
                    cancellationToken).ConfigureAwait(false);

            _dispatcher.Invoke(() =>
            {
                ApplyDeploymentResult(constraintDeployment);
                ValidationProgress = 25;
                ValidationStatus =
                    "Foreign keys added and validated. " +
                    "Starting post-migration validation.";
            });

            /*
             * Stage 2:
             * Run the existing validation engine.
             */
            var validationReporter =
                new Progress<ValidationProgress>(item =>
                {
                    var total = Math.Max(0, item.Total);
                    var completed = Math.Clamp(
                        item.Completed,
                        0,
                        Math.Max(total, item.Completed));

                    var mappedPercentage =
                        25 + (item.Percentage * 0.75);

                    context.Report(new OperationProgress(
                        Math.Clamp(mappedPercentage, 0, 100),
                        $"{item.Stage}: {item.CurrentObject}",
                        completed,
                        Math.Max(total, completed)));

                    _dispatcher.Invoke(() =>
                    {
                        ValidationProgress =
                            Math.Clamp(mappedPercentage, 0, 100);

                        ValidationCurrentObject =
                            item.CurrentObject;

                        ValidationStatus =
                            item.Stage;
                    });
                });

            var result = await _validationEngine.ValidateAsync(
                request,
                validationReporter,
                cancellationToken).ConfigureAwait(false);

            var journal = await _validationRunStore.SaveAsync(
                result,
                cancellationToken).ConfigureAwait(false);

            _dispatcher.Invoke(() =>
            {
                ApplyValidationResult(result);

                ValidationStatus =
                    $"{result.Readiness.OverallStatus} · " +
                    $"persisted to {journal}";

                IsBusy = false;
            });
        }
        catch
        {
            _dispatcher.Invoke(() =>
            {
                IsBusy = false;

                ValidationStatus =
                    "Validation stopped. Review foreign-key errors, " +
                    "validation findings and retained results.";
            });

            throw;
        }
    });
            _validationOperationId = await _scheduler.EnqueueAsync(definition);
        }
        catch (Exception exception)
        {
            IsBusy = false;
            HandleError("Post-migration validation could not start", exception);
        }
    }


    private async Task<DeploymentResult> DeployAndValidateForeignKeysAsync(
    IProgress<DeploymentProgress>? progress,
    CancellationToken cancellationToken)
    {
        var request = CreateDeploymentRequest(
            scopeOverride: DeploymentScope.SelectedPhases,
            selectedPhasesOverride: ForeignKeyDeploymentPhases,

            // This strategy:
            // 1. Adds FK constraints as NOT VALID.
            // 2. Validates them during post-deployment processing.
            constraintStrategyOverride:
                ConstraintDeploymentStrategy.AddNotValidThenValidate,

            validateConstraintsOverride: true,

            // These operations are unnecessary for an FK-only deployment.
            analyzeTablesOverride: false,
            vacuumAnalyzeOverride: false,
            installExtensionsOverride: false,

            // Useful if validation is run again after the FKs already exist.
            conflictPolicyOverride:
                ExistingObjectConflictPolicy.SkipWhenEquivalent);

        var result = await _deploymentEngine.DeployAsync(
            request,
            progress,
            cancellationToken).ConfigureAwait(false);

        if (result.Status == DeploymentRunStatus.Failed ||
            result.Objects.Any(item =>
                item.Status is
                    DeploymentObjectStatus.Failed or
                    DeploymentObjectStatus.Blocked or
                    DeploymentObjectStatus.BlockedByDependency))
        {
            throw new InvalidOperationException(
                "Foreign-key deployment or validation failed. " +
                $"The deployment journal contains {result.Failures.Count:N0} failure(s).");
        }

        return result;
    }


    [RelayCommand]
    private void CancelValidation()
    {
        if (_validationOperationId is { } id && _scheduler.Cancel(id))
        {
            ValidationStatus = "Cancellation requested.";
        }
    }

    [RelayCommand]
    private async Task ExportValidationReportAsync()
    {
        if (_validationRun is null)
        {
            _errors.ShowRecoverable("Validation result required", "Run validation before exporting.");
            return;
        }
        var directory = _dialogs.SelectFolder("Select a folder for the validation report");
        if (directory is null)
        {
            return;
        }
        var files = await _validationReportWriter.WriteAsync(
            _validationRun, directory, CancellationToken.None);
        ValidationStatus = $"Validation reports written: {string.Join(", ", files)}";
    }

    private ValidationRequest CreateValidationRequest()
    {
        var source = _session.Current ??
                     throw new InvalidOperationException("A source inventory is required.");
        var conversion = _conversionSession.Current ??
                         throw new InvalidOperationException("A conversion run is required.");
        var selectedSchemas = Schemas.Where(item => item.IsSelected && !item.IsExcluded)
            .Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedTables = Objects.Where(item =>
                item.IsSelected && item.Item.ObjectType == InventoryObjectType.Table)
            .Select(item => item.Item.QualifiedSourceName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuration = new ValidationConfiguration
        {
            Level = ValidationLevel,
            Scope = new ValidationScope
            {
                Schemas = selectedSchemas,
                Tables = selectedTables
            },
            KeylessTableStrategy = KeylessValidationStrategy,
            SampleSize = ValidationSampleSize,
            ChunkSize = ValidationChunkSize,
            ValidateForeignKeyOrphans = ValidationForeignKeyOrphans,
            IncludeDistinctCounts = ValidationDistinctCounts,
            RoutineTestCases = ValidationRoutineTestCases.ToArray()
        };
        return new ValidationRequest(
            source,
            conversion,
            new ValidationConnectionOptions(
                CreateSourceValidationConnectionString(),
                CreateTargetValidationConnectionString()),
            configuration,
            Guid.TryParse(MigrationRunId, out var migrationId) ? migrationId : null,
            Guid.TryParse(DeploymentId, out var deploymentId) ? deploymentId : null);
    }

    private string CreateSourceValidationConnectionString()
    {
        var options = CreateConnection();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = options.Port is null ? options.Server : $"{options.Server},{options.Port}",
            InitialCatalog = options.Database,
            IntegratedSecurity = options.AuthenticationMode == SqlServerAuthenticationMode.Windows,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectionTimeoutSeconds,
            ApplicationName = "MigrationStudio.Validation"
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = options.Username;
            builder.Password = options.Password;
        }
        return builder.ConnectionString;
    }

    private string CreateTargetValidationConnectionString()
    {
        var deployment = CreateDeploymentConnection();

        if (string.IsNullOrWhiteSpace(deployment.Host))
        {
            throw new InvalidOperationException(
                "PostgreSQL deployment host is required.");
        }

        if (string.IsNullOrWhiteSpace(
                deployment.TargetDatabase))
        {
            throw new InvalidOperationException(
                "PostgreSQL target database is required.");
        }

        if (string.IsNullOrWhiteSpace(
                deployment.Username))
        {
            throw new InvalidOperationException(
                "PostgreSQL username is required.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = deployment.Host,
            Port = deployment.Port,
            Database = deployment.TargetDatabase,
            Username = deployment.Username,
            Password = deployment.Password,

            SslMode = Enum.TryParse<SslMode>(
                deployment.SslMode,
                true,
                out var sslMode)
                    ? sslMode
                    : SslMode.Prefer,

            RootCertificate =
                deployment.RootCertificate,

            SslCertificate =
                deployment.ClientCertificate,

            CommandTimeout =
                deployment.CommandTimeoutSeconds,

            ApplicationName =
                "MigrationStudio.Validation"
        }.ConnectionString;
    }
    private void ApplyValidationResult(ValidationRun result)
    {
        _validationRun = result;
        _validationSession.SetCurrent(result);
        ValidationRunId = result.RunId.ToString();
        ReadinessStatus = result.Readiness.OverallStatus.ToString();
        ReadinessScore = result.Readiness.WeightedScore;
        ValidationCriticalBlockers = result.Readiness.CriticalBlockers.Count;
        ValidationProgress = 100;
        ValidationCategoryScores.Clear();
        ValidationObjectComparisons.Clear();
        ValidationDataComparisons.Clear();
        ValidationSequences.Clear();
        ValidationFindings.Clear();
        foreach (var item in result.Readiness.Categories) ValidationCategoryScores.Add(item);
        foreach (var item in result.ObjectComparisons) ValidationObjectComparisons.Add(item);
        foreach (var item in result.DataComparisons) ValidationDataComparisons.Add(item);
        foreach (var item in result.SequenceResults) ValidationSequences.Add(item);
        foreach (var item in result.Findings) ValidationFindings.Add(item);
    }

    private async Task QueueDeploymentAsync(DeploymentExecution execution)
    {
        if (Interlocked.CompareExchange(ref _deploymentInFlight, 1, 0) != 0)
        {
            _errors.ShowRecoverable(
                "Deployment already running",
                "Wait for the active deployment to finish or cancel it.");
            return;
        }

        NotifyDeploymentCommands();

        try
        {
            await EnsureDeploymentPackageIsCurrentAsync(
                CancellationToken.None);

            Guid? resumeId = null;

            if (execution == DeploymentExecution.Resume)
            {
                if (!Guid.TryParse(DeploymentId, out var parsedResumeId))
                {
                    throw new InvalidOperationException(
                        "A valid deployment ID is required to resume.");
                }

                resumeId = parsedResumeId;
            }

            var request = CreateDeploymentRequest(
                resumeId: resumeId,
                scopeOverride: DeploymentScope.SelectedPhases,
                selectedPhasesOverride: InitialDeploymentPhases,
                constraintStrategyOverride:
                    ConstraintDeploymentStrategy.ValidateInLaterPhase,
                validateConstraintsOverride: false);

            var targetDescription =
                $"{request.Connection.Host}:" +
                $"{request.Connection.Port}/" +
                $"{request.Connection.TargetDatabase}";

            IsBusy = true;

            DeploymentProgress = 0;
            DeploymentCompleted = 0;
            DeploymentFailed = 0;
            DeploymentSkipped = 0;
            DeploymentCurrentObject = string.Empty;

            DeploymentStatus = execution switch
            {
                DeploymentExecution.Start =>
                    $"Schema deployment queued for {targetDescription}. " +
                    "Foreign keys will be added during validation.",

                DeploymentExecution.Resume =>
                    $"Schema deployment resume queued for {targetDescription}.",

                DeploymentExecution.RetryFailed =>
                    $"Failed schema-object retry queued for {targetDescription}.",

                _ =>
                    $"Deployment queued for {targetDescription}."
            };

            var definition = new BackgroundOperationDefinition(
                $"Deploy PostgreSQL Schema to " +
                $"{request.Connection.TargetDatabase}",
                async (context, cancellationToken) =>
                {
                    try
                    {
                        var reporter =
                            new Progress<DeploymentProgress>(item =>
                            {
                                var total = Math.Max(0, item.Total);

                                var completed = Math.Clamp(
                                    item.Completed,
                                    0,
                                    Math.Max(total, item.Completed));

                                var effectiveTotal =
                                    Math.Max(total, completed);

                                context.Report(
                                    new OperationProgress(
                                        Math.Clamp(
                                            item.Percentage,
                                            0,
                                            100),
                                        item.Message,
                                        completed,
                                        effectiveTotal));

                                _dispatcher.Invoke(() =>
                                {
                                    ApplyDeploymentProgress(item);
                                });
                            });

                        DeploymentResult result;

                        switch (execution)
                        {
                            case DeploymentExecution.Resume:
                                result =
                                    await _deploymentEngine.ResumeAsync(
                                            request,
                                            reporter,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                break;

                            case DeploymentExecution.RetryFailed:
                                {
                                    if (_deploymentResult is null)
                                    {
                                        throw new InvalidOperationException(
                                        "No previous deployment result is " +
                                        "available for retry.");
                                    }

                                    var retryObjects =
                                    _deploymentResult.Objects
                                        .Where(item =>
                                            item.SourceObjectId is not null &&
                                            InitialDeploymentPhases.Contains(
                                                item.Phase) &&
                                            item.Status is
                                                DeploymentObjectStatus.Failed or
                                                DeploymentObjectStatus.Blocked or
                                                DeploymentObjectStatus
                                                    .BlockedByDependency)
                                        .Select(item =>
                                            item.SourceObjectId!.Value)
                                        .ToHashSet();

                                    if (retryObjects.Count == 0)
                                    {
                                        throw new InvalidOperationException(
                                        "No failed or blocked schema objects " +
                                        "are available for retry.");
                                    }

                                    result =
                                    await _deploymentEngine.RetryFailedAsync(
                                            request,
                                            retryObjects,
                                            reporter,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                    break;
                                }

                            default:
                                result =
                                    await _deploymentEngine.DeployAsync(
                                            request,
                                            reporter,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                break;
                        }

                        var completedCount =
                            result.Objects.Count(item =>
                                item.Status ==
                                DeploymentObjectStatus.Succeeded);

                        var skippedCount =
                            result.Objects.Count(item =>
                                item.Status is
                                    DeploymentObjectStatus.Skipped or
                                    DeploymentObjectStatus
                                        .SkippedEquivalent);

                        var failedOrBlockedCount =
                            result.Objects.Count(item =>
                                item.Status is
                                    DeploymentObjectStatus.Failed or
                                    DeploymentObjectStatus.Blocked or
                                    DeploymentObjectStatus
                                        .BlockedByDependency);

                        /*
                         * Explicitly complete the scheduler progress.
                         * The deployment engine may not emit a final 100%
                         * progress event after the last journal entry.
                         */
                        context.Report(
                            new OperationProgress(
                                100,
                                $"Schema deployment completed as " +
                                $"{result.Status}.",
                                result.Objects.Count,
                                result.Objects.Count));

                        _dispatcher.Invoke(() =>
                        {
                            ApplyDeploymentResult(result);
                        });

                        if (result.Status is
                                DeploymentRunStatus.Failed or
                                DeploymentRunStatus.Blocked ||
                            failedOrBlockedCount > 0)
                        {
                            throw new InvalidOperationException(
                                $"Deployment ended as {result.Status}. " +
                                $"{completedCount:N0} completed, " +
                                $"{skippedCount:N0} skipped and " +
                                $"{failedOrBlockedCount:N0} failed or " +
                                $"blocked objects. Review the deployment " +
                                "journal.");
                        }
                    }
                    catch (OperationCanceledException)
                        when (cancellationToken.IsCancellationRequested)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            DeploymentStatus =
                                "Schema deployment cancelled. " +
                                "Objects already committed remain recorded " +
                                "in the deployment journal.";
                        });

                        throw;
                    }
                    catch (Exception exception)
                    {
                        _dispatcher.Invoke(() =>
                        {
                            /*
                             * Do not overwrite a successful result status
                             * unless deployment actually failed.
                             */
                            if (_deploymentResult is null ||
                                _deploymentResult.Status is
                                    DeploymentRunStatus.Failed or
                                    DeploymentRunStatus.Blocked)
                            {
                                DeploymentStatus =
                                    "Deployment stopped. Review the " +
                                    "assessment, error details and journal.";
                            }

                            deploymentFailed(_logger, exception);
                        });

                        throw;
                    }
                    finally
                    {
                        _dispatcher.Invoke(() =>
                        {
                            IsBusy = false;

                            Interlocked.Exchange(
                                ref _deploymentInFlight,
                                0);

                            /*
                             * The operation ID is cleared here and again
                             * after EnqueueAsync to handle the fast-completion
                             * race.
                             */
                            _deploymentOperationId = null;

                            NotifyDeploymentCommands();

                            OnPropertyChanged(
                                nameof(IsDeploymentRunning));

                            OnPropertyChanged(
                                nameof(HasSuccessfulSchemaDeployment));
                        });
                    }
                },
                $"postgresql-schema-deployment:" +
                $"{request.Connection.Host}:" +
                $"{request.Connection.Port}:" +
                $"{request.Connection.TargetDatabase}");

            _deploymentOperationId =
                await _scheduler.EnqueueAsync(definition);

            /*
             * The operation might finish before EnqueueAsync returns.
             * In that case, do not retain a stale operation ID.
             */
            if (Volatile.Read(ref _deploymentInFlight) == 0)
            {
                _deploymentOperationId = null;
            }

            NotifyDeploymentCommands();

            OnPropertyChanged(nameof(IsDeploymentRunning));
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _deploymentInFlight, 0);

            _deploymentOperationId = null;
            IsBusy = false;

            NotifyDeploymentCommands();

            OnPropertyChanged(nameof(IsDeploymentRunning));
            OnPropertyChanged(
                nameof(HasSuccessfulSchemaDeployment));

            HandleError(
                "Deployment could not start",
                exception);
        }
    }
    private async Task EnsureDeploymentPackageIsCurrentAsync(CancellationToken cancellationToken)
    {
        var current = _conversionSession.Current;
        if (current is null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(DeploymentPackagePath))
        {
            throw new InvalidOperationException("The current conversion has no validated deployment package.");
        }

        var manifest = await _packageReader.ReadAndVerifyAsync(
            DeploymentPackagePath,
            diagnosticMode: false,
            cancellationToken).ConfigureAwait(false);
        if (manifest.MigrationRunId != current.RunId ||
            manifest.Artifacts.Count != current.Artifacts.Count)
        {
            throw new InvalidDataException(
                "The selected package is stale or belongs to a different conversion run. " +
                "Run live PostgreSQL validation to publish a fresh package.");
        }

        var unmatched = current.Artifacts.ToList();
        foreach (var packaged in manifest.Artifacts)
        {
            var match = unmatched.FindIndex(item =>
                item.SourceObjectId == packaged.SourceObjectId &&
                item.TargetObjectId.ObjectType.Equals(
                    packaged.TargetObjectType,
                    StringComparison.OrdinalIgnoreCase) &&
                item.TargetObjectId.Schema.Equals(packaged.TargetSchema, StringComparison.Ordinal) &&
                item.TargetObjectId.Name.Equals(packaged.TargetName, StringComparison.Ordinal) &&
                item.PostgreSqlDefinition.Equals(packaged.Sql, StringComparison.Ordinal));
            if (match < 0)
            {
                throw new InvalidDataException(
                    "The selected package SQL does not match the current conversion run. " +
                    "Run live PostgreSQL validation to regenerate it.");
            }
            unmatched.RemoveAt(match);
        }
        if (unmatched.Count > 0)
        {
            throw new InvalidDataException(
                $"The selected package is missing {unmatched.Count:N0} current conversion artifacts.");
        }
    }


    private static readonly HashSet<DeploymentPhase> InitialDeploymentPhases =
[
    DeploymentPhase.PreDeployment,
    DeploymentPhase.Extensions,
    DeploymentPhase.Schemas,
    DeploymentPhase.Types,
    DeploymentPhase.Sequences,
    DeploymentPhase.Tables,
    DeploymentPhase.PreDataFunctions,
    DeploymentPhase.DefaultsAndGeneratedColumns,
    DeploymentPhase.PrimaryKeys,
    DeploymentPhase.UniqueConstraints,
    DeploymentPhase.CheckConstraints,
    DeploymentPhase.Indexes,
    DeploymentPhase.Functions,
    DeploymentPhase.Procedures,
    DeploymentPhase.Views,
    DeploymentPhase.Triggers,
    DeploymentPhase.Security,
    DeploymentPhase.Comments
];

    private static readonly IReadOnlySet<DeploymentPhase> ForeignKeyDeploymentPhases =
        new HashSet<DeploymentPhase>
        {
        DeploymentPhase.ForeignKeys
        };


    /*  private DeploymentRequest CreateDeploymentRequest(Guid? resumeId = null)
      {
          if (string.IsNullOrWhiteSpace(DeploymentPackagePath))
          {
              throw new InvalidOperationException("Select or generate a migration package first.");
          }

          var selectedPhases = DeploymentPhases.Where(item => item.IsSelected)
              .Select(item => item.Phase)
              .ToHashSet();
          var options = new DeploymentOptions
          {
              Mode = DeploymentMode,
              Scope = DeploymentScope,
              PreDeploymentPolicy = PreDeploymentPolicy,
              TransactionMode = DeploymentTransactionMode,
              ErrorPolicy = DeploymentErrorPolicy,
              ConflictPolicy = ConflictPolicy,
              ConstraintStrategy = ConstraintDeploymentStrategy.ValidateInLaterPhase,
              SelectedPhases = selectedPhases,
              InstallRequiredExtensions = InstallRequiredExtensions,
              AnalyzeTables = AnalyzeTables,
              VacuumAnalyze = VacuumAnalyze,
              ValidateConstraints = true,
              RequireLivePostgreSqlValidation = true,
              AdministratorOverrideConfirmed = AdministratorOverrideConfirmed,
              AdministratorOverrideReason = AdministratorOverrideReason,
              DatabaseCreation = new DatabaseCreationOptions
              {
                  ExistsPolicy = DatabaseExistsPolicy,
                  DestructiveActionConfirmed = DeploymentDestructiveConfirmed
              }
          };
          DataMigrationRequest? dataRequest = null;
       *//*   if (_session.Current is not null && _conversionSession.Current is not null &&
              DeploymentScope is DeploymentScope.CompletePackage or DeploymentScope.DataOnly)
          {
              dataRequest = new DataMigrationRequest(
                  _session.Current,
                  _conversionSession.Current,
                  CreateConnection(),
                  "Host=pending;Database=pending;Username=pending",
                  new DataMigrationOptions
                  {
                      MigrationMode = DataMigrationMode,
                      ExecutionMode = DataExecutionMode,
                      ParallelismMode = DataParallelismMode,
                      MaximumConcurrentTables = MaximumConcurrentTables,
                      MaximumConcurrentReaders = MaximumConcurrentReaders,
                      MaximumConcurrentWriters = MaximumConcurrentWriters,
                      BatchRowCount = BatchRowCount,
                      BatchByteSize = BatchByteSize,
                      TargetPreparation = TargetPreparation,
                      IsDestructiveTargetPreparationConfirmed = DestructivePreparationConfirmed,
                      FailurePolicy = MigrationFailurePolicy
                  });
          }
  *//*
          return new DeploymentRequest(
              DeploymentPackagePath,
              CreateDeploymentConnection(),
              options,
              dataRequest,
              resumeId);
      }
  */
    private DeploymentRequest CreateDeploymentRequest(
      Guid? resumeId = null,
      DeploymentScope? scopeOverride = null,
      IReadOnlySet<DeploymentPhase>? selectedPhasesOverride = null,
      ConstraintDeploymentStrategy? constraintStrategyOverride = null,
      bool? validateConstraintsOverride = null,
      bool? analyzeTablesOverride = null,
      bool? vacuumAnalyzeOverride = null,
      bool? installExtensionsOverride = null,
      ExistingObjectConflictPolicy? conflictPolicyOverride = null)
    {
        if (string.IsNullOrWhiteSpace(DeploymentPackagePath))
        {
            throw new InvalidOperationException(
                "Select or generate a migration package first.");
        }

        var selectedPhases =
            selectedPhasesOverride ??
            DeploymentPhases
                .Where(item => item.IsSelected)
                .Select(item => item.Phase)
                .ToHashSet();

        var options = new DeploymentOptions
        {
            Mode = DeploymentMode,

            Scope =
                scopeOverride ??
                DeploymentScope,

            PreDeploymentPolicy =
                PreDeploymentPolicy,

            TransactionMode =
                DeploymentTransactionMode,

            ErrorPolicy =
                DeploymentErrorPolicy,

            ConflictPolicy =
                conflictPolicyOverride ??
                ConflictPolicy,

            ConstraintStrategy =
                constraintStrategyOverride ??
                ConstraintDeploymentStrategy.ValidateInLaterPhase,

            SelectedPhases =
                selectedPhases,

            InstallRequiredExtensions =
                installExtensionsOverride ??
                InstallRequiredExtensions,

            AnalyzeTables =
                analyzeTablesOverride ??
                AnalyzeTables,

            VacuumAnalyze =
                vacuumAnalyzeOverride ??
                VacuumAnalyze,

            ValidateConstraints =
                validateConstraintsOverride ??
                true,

            RequireLivePostgreSqlValidation = true,

            AdministratorOverrideConfirmed =
                AdministratorOverrideConfirmed,

            AdministratorOverrideReason =
                AdministratorOverrideReason,

            DatabaseCreation = new DatabaseCreationOptions
            {
                ExistsPolicy = DatabaseExistsPolicy,

                DestructiveActionConfirmed =
                    DeploymentDestructiveConfirmed
            }
        };

        // Keep deployment and data migration independent.
        DataMigrationRequest? dataRequest = null;

        return new DeploymentRequest(
            DeploymentPackagePath,
            CreateDeploymentConnection(),
            options,
            dataRequest,
            resumeId);
    }


    private PostgreSqlConnectionOptions CreateDeploymentConnection() =>
        new()
        {
            Host = DeploymentHost.Trim(),
            Port = DeploymentPort,
            MaintenanceDatabase = MaintenanceDatabase.Trim(),
            TargetDatabase = DeploymentTargetDatabase.Trim(),
            Username = DeploymentUsername.Trim(),
            Password = DeploymentPassword,
            SslMode = DeploymentSslMode,
            RootCertificate = string.IsNullOrWhiteSpace(RootCertificate) ? null : RootCertificate,
            ClientCertificate = string.IsNullOrWhiteSpace(ClientCertificate) ? null : ClientCertificate,
            CommandTimeoutSeconds = CommandTimeoutSeconds
        };

    private void ApplyDeploymentAssessment(PreDeploymentAssessment assessment)
    {
        DeploymentFindings.Clear();
        foreach (var finding in assessment.Findings)
        {
            DeploymentFindings.Add(finding);
        }

        DeploymentConflicts.Clear();
        foreach (var conflict in assessment.Conflicts)
        {
            DeploymentConflicts.Add(conflict);
        }

        DeploymentPackageDuplicates.Clear();
        foreach (var duplicate in assessment.PackageDuplicates)
        {
            DeploymentPackageDuplicates.Add(duplicate);
        }

        DeploymentExtensions.Clear();
        foreach (var extension in assessment.Manifest?.RequiredExtensions ?? [])
        {
            DeploymentExtensions.Add(extension);
        }

        DeploymentServerVersion = assessment.Capabilities?.ServerVersion ?? string.Empty;
        DeploymentPackageId = assessment.Manifest?.PackageId.ToString() ?? string.Empty;
        DeploymentSourceDatabase = assessment.Manifest?.SourceDatabase ?? string.Empty;
        DeploymentTargetVersion = assessment.Manifest?.TargetPostgreSqlVersion ?? 0;
        DeploymentArtifactCount = assessment.Manifest?.Artifacts.Count ?? 0;
        DeploymentManualReviewCount = assessment.Manifest?.ManualReviewItems.Count ?? 0;
        PackagedArtifactCount = assessment.Manifest?.Artifacts.Count ?? 0;
        PackagedExecutableCount = assessment.Manifest?.Artifacts.Count(item => item.IsExecutable) ?? 0;
        PackagedManualReviewCount = assessment.Manifest?.Artifacts.Count(item => item.RequiresManualReview) ?? 0;
        PackagedUnsupportedCount = assessment.Manifest?.Artifacts.Count(item =>
            item.Classification == ConversionClassification.Unsupported) ?? 0;
        DeploymentPackageDuplicateCount = assessment.PackageDuplicates.Count;
        DeploymentBlockingFindingCount = DeploymentBlockingPolicy.CountBlocking(
            assessment.Findings,
            PreDeploymentPolicy,
            assessment.AdministratorOverrideApplied);
        DeploymentEquivalentObjectCount = assessment.Conflicts.Count(item => item.IsEquivalent);
        DeploymentActualConflictCount = assessment.Conflicts.Count(item =>
            item.Exists && !item.IsEquivalent);
        DeploymentTargetSchemaCount = assessment.Manifest?.Artifacts.Count(item =>
            item.IsExecutable && item.Phase == DeploymentPhase.Schemas) ?? 0;
        DeploymentTargetTableCount = assessment.Manifest?.Artifacts.Count(item =>
            item.IsExecutable && item.Phase == DeploymentPhase.Tables) ?? 0;
        DeploymentPackageIntegrityValid = assessment.PackageIntegrityValid;
        var firstBlocker = assessment.Findings.FirstOrDefault(item =>
            DeploymentBlockingPolicy.IsBlocking(
                item,
                PreDeploymentPolicy,
                assessment.AdministratorOverrideApplied));
        DeploymentStatus = assessment.CanDeploy
            ? $"Assessment passed with {assessment.Findings.Count:N0} findings."
            : firstBlocker is null
                ? $"Assessment blocked deployment with {assessment.Findings.Count:N0} findings."
                : $"Blocked by {firstBlocker.Code}: {firstBlocker.Message}";
    }


    private void ApplyDeploymentProgress(
        DeploymentProgress progress)
    {
        var total = Math.Max(0, progress.Total);

        var completed = Math.Clamp(
            progress.Completed,
            0,
            Math.Max(total, progress.Completed));

        DeploymentId =
            progress.DeploymentId.ToString();

        DeploymentCurrentObject =
            progress.CurrentObject ?? string.Empty;

        DeploymentStatus =
            $"{progress.Phase}: {progress.Message}";

        DeploymentCompleted = completed;
        DeploymentFailed = Math.Max(0, progress.Failed);
        DeploymentSkipped = Math.Max(0, progress.Skipped);

        DeploymentProgress = Math.Clamp(
            progress.Percentage,
            0,
            100);

        var phase =
            DeploymentPhases.FirstOrDefault(item =>
                item.Phase == progress.Phase);

        if (phase is not null)
        {
            phase.Completed = completed;
            phase.Failed = Math.Max(0, progress.Failed);
            phase.Skipped = Math.Max(0, progress.Skipped);

            phase.Status =
                progress.Failed > 0
                    ? DeploymentObjectStatus.Failed
                    : DeploymentObjectStatus.Running;
        }
    }

    private void ApplyDeploymentResult(
        DeploymentResult result)
    {
        _deploymentResult = result;

        DeploymentId =
            result.DeploymentId.ToString();

        DeploymentJournalEntries.Clear();

        foreach (var entry in result.Objects)
        {
            DeploymentJournalEntries.Add(entry);
        }

        DeploymentCompleted =
            result.Objects.Count(item =>
                item.Status ==
                DeploymentObjectStatus.Succeeded);

        DeploymentFailed =
            result.Objects.Count(item =>
                item.Status is
                    DeploymentObjectStatus.Failed or
                    DeploymentObjectStatus.Blocked or
                    DeploymentObjectStatus
                        .BlockedByDependency);

        DeploymentSkipped =
            result.Objects.Count(item =>
                item.Status is
                    DeploymentObjectStatus.Skipped or
                    DeploymentObjectStatus.SkippedEquivalent);

        DeploymentProgress = 100;
        DeploymentCurrentObject = string.Empty;

        DeploymentStatus =
            $"{result.Status} · " +
            $"{DeploymentCompleted:N0} completed · " +
            $"{DeploymentFailed:N0} failed or blocked · " +
            $"target {result.TargetDatabase} · " +
            $"journal {result.JournalPath}";

        /*
         * Mark phase rows with their final states.
         */
        foreach (var phaseRow in DeploymentPhases)
        {
            var phaseEntries =
                result.Objects
                    .Where(item =>
                        item.Phase == phaseRow.Phase)
                    .ToArray();

            if (phaseEntries.Length == 0)
            {
                continue;
            }

            phaseRow.Completed =
                phaseEntries.Count(item =>
                    item.Status ==
                    DeploymentObjectStatus.Succeeded);

            phaseRow.Failed =
                phaseEntries.Count(item =>
                    item.Status is
                        DeploymentObjectStatus.Failed or
                        DeploymentObjectStatus.Blocked or
                        DeploymentObjectStatus
                            .BlockedByDependency);

            phaseRow.Skipped =
                phaseEntries.Count(item =>
                    item.Status is
                        DeploymentObjectStatus.Skipped or
                        DeploymentObjectStatus
                            .SkippedEquivalent);

            phaseRow.Status =
                phaseRow.Failed > 0
                    ? DeploymentObjectStatus.Failed
                    : DeploymentObjectStatus.Succeeded;
        }

        OnPropertyChanged(nameof(IsDeploymentRunning));

        OnPropertyChanged(
            nameof(HasSuccessfulSchemaDeployment));

        NotifyDeploymentCommands();
    }


    public bool IsDeploymentRunning =>
    Volatile.Read(ref _deploymentInFlight) != 0;

    public bool HasSuccessfulSchemaDeployment
    {
        get
        {
            if (_deploymentResult is null)
            {
                return false;
            }

            if (_deploymentResult.Status is not
                    DeploymentRunStatus.Succeeded and not
                    DeploymentRunStatus.SucceededWithWarnings)
            {
                return false;
            }

            return !_deploymentResult.Objects.Any(item =>
                item.Status is
                    DeploymentObjectStatus.Failed or
                    DeploymentObjectStatus.Blocked or
                    DeploymentObjectStatus
                        .BlockedByDependency);
        }
    }

    private void NotifyDeploymentCommands()
    {
        StartDeploymentCommand.NotifyCanExecuteChanged();
        ResumeDeploymentCommand.NotifyCanExecuteChanged();

        RetryFailedDeploymentObjectsCommand
            .NotifyCanExecuteChanged();

        CancelDeploymentCommand.NotifyCanExecuteChanged();
        AssessDeploymentCommand.NotifyCanExecuteChanged();
        TestDeploymentConnectionCommand.NotifyCanExecuteChanged();
        ExportDeploymentReportCommand.NotifyCanExecuteChanged();
    }


    private enum DeploymentExecution
    {
        Start,
        Resume,
        RetryFailed
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = DebounceSearchAsync(_session.Current, value, _searchCancellation.Token);
    }

    private SqlServerConnectionOptions CreateConnection(bool requireDatabase = true) =>
        new()
        {
            Server = Server.Trim(),
            Port = Port,
            Database = requireDatabase ? SelectedDatabase ?? string.Empty : SelectedDatabase ?? "master",
            AuthenticationMode = UseWindowsAuthentication
                ? SqlServerAuthenticationMode.Windows
                : SqlServerAuthenticationMode.SqlServer,
            Username = UseWindowsAuthentication ? null : Username,
            Password = UseWindowsAuthentication ? null : Password,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectionTimeoutSeconds = ConnectionTimeoutSeconds,
            CommandTimeoutSeconds = CommandTimeoutSeconds
        };

    private InventoryDiscoveryRequest CreateRequest() =>
        new(
            CreateConnection(),
            ScopeMode,
            Schemas.Where(item => item.IsSelected).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            Objects.Where(item => item.IsSelected).Select(item => item.Item.Id).ToHashSet(),
            (_excelResult?.Matched ?? []).Select(item => item.TableObjectId).ToHashSet(),
            DependencyPolicy,
            new DiscoveryOptions { IncludeReplication = true });

    private void ApplySnapshot(InventorySnapshot snapshot)
    {
        _searchCancellation?.Cancel();
        _conversionSession.Clear();
        ConversionArtifacts.Clear();
        IdentifierMappings.Clear();
        LiveValidationFailures.Clear();
        DeploymentPackagePath = string.Empty;
        PackagedArtifactCount = 0;
        PackagedExecutableCount = 0;
        PackagedManualReviewCount = 0;
        PackagedUnsupportedCount = 0;
        LiveValidationPassedCount = 0;
        LiveValidationFailedCount = 0;
        LiveValidationBlockedCount = 0;
        LiveValidationNotRunCount = 0;
        LiveValidationManualReviewCount = 0;
        LiveValidationReusedCount = 0;
        IdentifierMappingStatus =
            "Identifier mapping invalidated because the inventory or migration scope changed.";
        ConversionStatus = "Run conversion to rebuild the identifier mapping.";
        _session.SetCurrent(snapshot);
        _objectRows.Clear();
        Schemas.Clear();
        foreach (var schema in snapshot.Schemas.OrderBy(item => item.InventoryObject.SourceName, StringComparer.OrdinalIgnoreCase))
        {
            Schemas.Add(new SchemaSelectionViewModel(
                schema.InventoryObject.SourceName,
                schema.InventoryObject.IsIncluded,
                schema.ObjectCount,
                schema.InventoryObject.SourceName.Equals("dbo", StringComparison.OrdinalIgnoreCase)
                    ? "public"
                    : schema.InventoryObject.SourceName));
        }
        Findings.Clear();
        foreach (var finding in snapshot.Findings)
        {
            Findings.Add(finding);
        }
        Dependencies.Clear();
        foreach (var dependency in snapshot.Dependencies)
        {
            Dependencies.Add(dependency);
        }
        ObjectCount = snapshot.Objects.Count;
        IncludedCount = snapshot.Objects.Count(item => item.IsIncluded);
        FindingCount = snapshot.Findings.Count;
        UnresolvedDependencyCount = snapshot.Dependencies.Count(item => !item.IsResolved);
        var stopwatch = Stopwatch.StartNew();
        RefreshObjects(snapshot);
        stopwatch.Stop();
        LastInventoryProjectionMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
    }

    partial void OnTargetPostgreSqlVersionChanged(int value) => InvalidateConversionMapping();

    partial void OnIdentifierCaseModeChanged(IdentifierCaseMode value) => InvalidateConversionMapping();

    partial void OnSchemaMappingModeChanged(SchemaMappingMode value) => InvalidateConversionMapping();

    partial void OnIdentityStrategyChanged(IdentityConversionStrategy value) => InvalidateConversionMapping();

    partial void OnSecurityStrategyChanged(SecurityConversionStrategy value) => InvalidateConversionMapping();

    partial void OnEnablePgCryptoChanged(bool value) => InvalidateConversionMapping();

    partial void OnEnablePostGisChanged(bool value) => InvalidateConversionMapping();

    private void InvalidateConversionMapping()
    {
        if (_conversionSession.Current is null)
        {
            return;
        }

        _conversionSession.Clear();
        ConversionArtifacts.Clear();
        IdentifierMappings.Clear();
        LiveValidationFailures.Clear();
        DeploymentPackagePath = string.Empty;
        PackagedArtifactCount = 0;
        PackagedExecutableCount = 0;
        PackagedManualReviewCount = 0;
        PackagedUnsupportedCount = 0;
        LiveValidationPassedCount = 0;
        LiveValidationFailedCount = 0;
        LiveValidationBlockedCount = 0;
        LiveValidationNotRunCount = 0;
        LiveValidationManualReviewCount = 0;
        LiveValidationReusedCount = 0;
        IdentifierMappingStatus =
            "Identifier mapping invalidated because conversion or naming options changed.";
        ConversionStatus = "Run conversion to rebuild the identifier mapping.";
    }

    private void RefreshObjects(InventorySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }
        var items = FilterObjects(snapshot, SearchText);
        var rows = new InventoryObjectRowViewModel[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (!_objectRows.TryGetValue(item.Id, out var row))
            {
                row = new InventoryObjectRowViewModel(item);
                _objectRows.Add(item.Id, row);
            }
            rows[index] = row;
        }
        Objects.ReplaceAll(rows);
        InventoryViewModelCount = _objectRows.Count;
        DisplayedObjectCount = rows.Length;
    }

    private async Task DebounceSearchAsync(
        InventorySnapshot? snapshot,
        string search,
        CancellationToken cancellationToken)
    {
        if (snapshot is null)
        {
            return;
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            var items = await Task.Run(
                () => FilterObjects(snapshot, search, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _dispatcher.Invoke(() =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var rows = new InventoryObjectRowViewModel[items.Length];
                for (var index = 0; index < items.Length; index++)
                {
                    var item = items[index];
                    if (!_objectRows.TryGetValue(item.Id, out var row))
                    {
                        row = new InventoryObjectRowViewModel(item);
                        _objectRows.Add(item.Id, row);
                    }
                    rows[index] = row;
                }
                Objects.ReplaceAll(rows);
                InventoryViewModelCount = _objectRows.Count;
                DisplayedObjectCount = rows.Length;
                LastInventoryFilterMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer search superseded this one.
        }
        catch (Exception exception)
        {
            LogWorkspaceError(exception, "Inventory filtering failed");
        }
    }

    private static InventoryObject[] FilterObjects(
        InventorySnapshot snapshot,
        string search,
        CancellationToken cancellationToken = default)
    {
        var normalized = search.Trim();
        var result = new List<InventoryObject>();
        foreach (var item in snapshot.Objects)
        {
            if ((result.Count & 511) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (normalized.Length == 0 ||
                item.QualifiedSourceName.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                item.ObjectType.ToString().Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(item);
            }
        }
        return result.OrderBy(item => item.ObjectType)
            .ThenBy(item => item.QualifiedSourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void Dispose()
    {
        if (_operationId is { } discoveryId)
        {
            _scheduler.Cancel(discoveryId);
        }
        if (_conversionOperationId is { } conversionId)
        {
            _scheduler.Cancel(conversionId);
        }
        if (_dataOperationId is { } dataId)
        {
            _scheduler.Cancel(dataId);
        }
        if (_deploymentOperationId is { } deploymentId)
        {
            _scheduler.Cancel(deploymentId);
        }
        if (_validationOperationId is { } validationId)
        {
            _scheduler.Cancel(validationId);
        }
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _excelCancellation?.Cancel();
        _excelCancellation?.Dispose();
        _excelCancellation = null;
        _doctorCancellation?.Cancel();
        _doctorCancellation?.Dispose();
        _doctorCancellation = null;
    }

    private void HandleError(string title, Exception exception)
    {
        LogWorkspaceError(exception, title);
        ConnectionStatus = title;
        _errors.ShowRecoverable(title, exception.Message);
    }

    private void ClearDiscoveryFailure()
    {
        IsDiscoveryFailureVisible = false;
        DiscoveryFailureStage = string.Empty;
        DiscoveryFailureQueryId = string.Empty;
        DiscoveryFailureErrorCode = string.Empty;
        DiscoveryFailureSummary = string.Empty;
        DiscoveryFailureDetails = string.Empty;
        DiscoveryFailureRemediation = string.Empty;
        DiscoveryCorrelationId = string.Empty;
        CanRetryDiscovery = false;
    }

    private void ApplyDiscoveryFailure(Exception exception)
    {
        LogWorkspaceError(exception, "SQL Server discovery failed");
        IsDiscoveryFailureVisible = true;
        if (exception is SourceDatabaseException source)
        {
            var first = source.Errors.Count == 0 ? null : source.Errors[0];
            DiscoveryFailureStage = source.Stage.ToString();
            DiscoveryFailureQueryId = source.QueryId;
            DiscoveryFailureErrorCode = first is null
                ? source.InnerException?.GetType().Name ?? source.GetType().Name
                : $"SQL {first.Number} / state {first.State} / class {first.Class}";
            DiscoveryFailureSummary = _redactor.Redact(
                first is null ? source.Message : first.Message);
            DiscoveryFailureDetails = _redactor.Redact(
                source.Errors.Count == 0
                    ? source.InnerException?.Message ?? source.Message
                    : string.Join(
                        Environment.NewLine,
                        source.Errors.Select(error =>
                            $"SQL {error.Number}; state {error.State}; class {error.Class}; " +
                            $"procedure {error.Procedure ?? "(none)"}; line {error.LineNumber}: {error.Message}")));
            DiscoveryFailureRemediation = _redactor.Redact(
                source.Remediation ??
                "Review metadata permissions and the version-specific catalog query.");
            DiscoveryCorrelationId = source.CorrelationId.ToString("N");
            CanRetryDiscovery = source.InnerException is not NotSupportedException;
        }
        else
        {
            DiscoveryFailureStage = "Discovery infrastructure";
            DiscoveryFailureQueryId = "DISCOVERY.UNHANDLED";
            DiscoveryFailureErrorCode = exception.GetType().Name;
            DiscoveryFailureSummary = _redactor.Redact(exception.Message);
            DiscoveryFailureDetails = DiscoveryFailureSummary;
            DiscoveryFailureRemediation =
                "Export the sanitized diagnostic and inspect the application log.";
            CanRetryDiscovery = true;
        }

        Status =
            $"{DiscoveryFailureStage} failed [{DiscoveryFailureQueryId}]: {DiscoveryFailureSummary}";
        RetryDiscoveryCommand.NotifyCanExecuteChanged();
        _errors.ShowRecoverable(
            "SQL Server discovery failed",
            $"{Status}{Environment.NewLine}{Environment.NewLine}" +
            $"Remediation: {DiscoveryFailureRemediation}{Environment.NewLine}" +
            $"Correlation: {DiscoveryCorrelationId}");
    }

    private ConversionOptions CreateConversionOptions() =>
        new()
        {
            TargetVersion = new PostgreSqlVersion(TargetPostgreSqlVersion),
            IdentifierCaseMode = IdentifierCaseMode,
            SchemaMappingMode = SchemaMappingMode,
            IdentityStrategy = IdentityStrategy,
            SecurityStrategy = SecurityStrategy,
            EnablePgCrypto = EnablePgCrypto,
            EnablePostGis = EnablePostGis,
            SchemaMappings = Schemas.Select(item =>
                new SchemaMappingRule(item.Name, item.TargetSchema, item.IsExcluded)).ToArray()
        };

    private void ApplyConversionRun(ConversionRun run)
    {
        foreach (var existing in ConversionArtifacts)
        {
            existing.PropertyChanged -= OnConversionArtifactPropertyChanged;
        }
        var artifactRows = run.Artifacts
            .Select(artifact => new ConversionArtifactViewModel(artifact))
            .ToArray();
        foreach (var artifactRow in artifactRows)
        {
            artifactRow.PropertyChanged += OnConversionArtifactPropertyChanged;
        }
        ConversionArtifacts.ReplaceAll(artifactRows);
        IdentifierMappings.ReplaceAll(run.IdentifierMappings);
        TypeMappings.ReplaceAll(run.TypeMappings);
        AutomaticConversionCount = run.Artifacts.Count(item => item.Classification == ConversionClassification.Automatic);
        WarningConversionCount = run.Artifacts.Count(item => item.Classification == ConversionClassification.AutomaticWithWarning);
        ManualConversionCount = run.Artifacts.Count(item => item.Classification == ConversionClassification.ManualConversion);
        UnsupportedConversionCount = run.Artifacts.Count(item => item.Classification == ConversionClassification.Unsupported);
        SelectedArtifactCount = _session.Current?.Objects.Count(item =>
            item.IsIncluded &&
            !item.IsSystemObject &&
            item.ObjectType != InventoryObjectType.Column) ?? run.Artifacts.Count;
        ConvertedArtifactCount = run.Artifacts.Count;
        UpdateIdentifierMappingStatus(run);
        SelectedConversionArtifact = ConversionArtifacts.FirstOrDefault();
    }

    private void OnConversionArtifactPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ConversionArtifactViewModel.GeneratedSql))
        {
            return;
        }

        DeploymentPackagePath = string.Empty;
        LiveValidationStatus =
            "Generated SQL changed. Revalidate the changed artifact before package generation.";
        ConversionStatus = LiveValidationStatus;
    }

    private void UpdateIdentifierMappingStatus(ConversionRun run)
    {
        var summary = run.IdentifierMappingSummary;
        IdentifierMappingStatus =
            $"Included objects: {summary.TotalIncludedObjects:N0} · " +
            $"Mapped: {summary.AutomaticallyMapped:N0} · " +
            $"Renamed: {summary.Renamed:N0} · " +
            $"Collisions resolved: {summary.CollisionsResolved:N0} · " +
            $"Auto-recovered: {summary.AutoRecovered:N0} · " +
            $"Unresolved: {summary.Unresolved:N0}";
    }

    [LoggerMessage(EventId = 2101, Level = LogLevel.Error, Message = "{ErrorTitle}")]
    private partial void LogWorkspaceError(Exception exception, string errorTitle);

    [LoggerMessage(
        EventId = 2110,
        Level = LogLevel.Error,
        Message = "Conversion {RunId} completed with {ArtifactCount} artifacts, but result presentation failed.")]
    private partial void LogConversionPresentationFailure(
        Exception exception,
        Guid runId,
        int artifactCount);

    [LoggerMessage(
        EventId = 2111,
        Level = LogLevel.Information,
        Message = "Identifier lifecycle {Details}")]
    private partial void LogIdentifierLifecycle(string details);

    [LoggerMessage(
        EventId = 2121,
        Level = LogLevel.Information,
        Message =
            "Live PostgreSQL validation starting. TotalArtifacts={TotalArtifacts}, " +
            "ExecutableArtifacts={ExecutableArtifacts}, Reusable={ReusableArtifacts}, " +
            "RequiringValidation={RequiringValidation}.")]
    private partial void LogLiveValidationStarting(
        int totalArtifacts,
        int executableArtifacts,
        int reusableArtifacts,
        int requiringValidation);

    [LoggerMessage(
        EventId = 2122,
        Level = LogLevel.Information,
        Message =
            "Live PostgreSQL validation completed. Passed={Passed}, Failed={Failed}, " +
            "Blocked={Blocked}, Reused={Reused}, NotRun={NotRun}, " +
            "ArtifactsBeforeMerge={ArtifactsBeforeMerge}, ArtifactsAfterMerge={ArtifactsAfterMerge}.")]
    private partial void LogLiveValidationCompleted(
        int passed,
        int failed,
        int blocked,
        int reused,
        int notRun,
        int artifactsBeforeMerge,
        int artifactsAfterMerge);

    [LoggerMessage(
        EventId = 2123,
        Level = LogLevel.Warning,
        Message =
            "Package export blocked. InvalidCurrentValidationCount={InvalidCount}; " +
            "Artifacts={ArtifactNames}")]
    private partial void LogPackageExportBlocked(
        int invalidCount,
        string artifactNames);
}

public sealed partial class SchemaSelectionViewModel(
    string name,
    bool isSelected,
    int objectCount,
    string targetSchema) : ObservableObject
{
    [ObservableProperty] private bool _isSelected = isSelected;
    [ObservableProperty] private string _targetSchema = targetSchema;
    [ObservableProperty] private bool _isExcluded;

    public string Name { get; } = name;

    public int ObjectCount { get; } = objectCount;
}
