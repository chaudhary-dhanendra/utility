using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationStudio.Application.Conversion;
using MigrationStudio.Application.Operations;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Desktop.ViewModels;

public enum MigrationWizardStep
{
    Connect,
    Select,
    Analyze,
    Convert,
    Deploy,
    Migrate,
    Validate,
    Finish
}

public enum WizardStepState
{
    NotStarted,
    Running,
    Blocked,
    Completed,
    CompletedWithWarnings,
    Cancelled,
    Failed,
    Stale
}

public sealed partial class WizardStepViewModel(
    MigrationWizardStep step,
    string title,
    string purpose) : ObservableObject
{
    public MigrationWizardStep Step { get; } = step;
    public string Title { get; } = title;
    public string Purpose { get; } = purpose;

    [ObservableProperty] private WizardStepState _state;
    [ObservableProperty] private string _message = "Not started";
}

public sealed partial class MigrationWizardViewModel : ObservableObject, IDisposable
{
    private readonly IOperationMonitor _operations;
    private readonly IConversionSession _conversionSession;
    private bool _internalChange;

    [ObservableProperty] private MigrationWizardStep _currentStep;
    [ObservableProperty]
    private string _workflowMessage =
        "Connect SQL Server and PostgreSQL to begin.";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _activePackage = string.Empty;
    [ObservableProperty] private DateTimeOffset? _startedAt;
    [ObservableProperty] private DateTimeOffset? _finishedAt;

    public MigrationWizardViewModel(
        WorkspaceViewModel workspace,
        IOperationMonitor operations,
        IConversionSession conversionSession)
    {
        Workspace = workspace;
        _operations = operations;
        _conversionSession = conversionSession;

        Steps =
        [
            new(MigrationWizardStep.Connect, "Connect", "Connect the SQL Server source and PostgreSQL target."),
            new(MigrationWizardStep.Select, "Select", "Choose the database objects and data to migrate."),
            new(MigrationWizardStep.Analyze, "Analyze", "Assess compatibility, dependencies, and migration risks."),
            new(MigrationWizardStep.Convert, "Convert", "Create PostgreSQL objects and a verified migration package."),
            new(MigrationWizardStep.Deploy, "Deploy", "Prepare PostgreSQL and deploy the converted schema."),
            new(MigrationWizardStep.Migrate, "Migrate", "Transfer table data with automatic checkpoints."),
            new(MigrationWizardStep.Validate, "Validate", "Compare source and target and identify discrepancies."),
            new(MigrationWizardStep.Finish, "Finish", "Review the migration outcome and reports.")
        ];

        Workspace.PropertyChanged += OnWorkspacePropertyChanged;
        Workspace.PostgreSqlTarget.PropertyChanged += OnTargetPropertyChanged;
        RefreshConnectionStep();
        NotifyNavigation();
    }

    public WorkspaceViewModel Workspace { get; }

    public IReadOnlyList<WizardStepViewModel> Steps { get; }

    public IReadOnlyList<MigrationScopeMode> SelectionModes { get; } =
    [
        MigrationScopeMode.CompleteDatabase,
        MigrationScopeMode.SelectedSchemas,
        MigrationScopeMode.ExcelSelectedTables
    ];

    public bool IsConnectStep => CurrentStep == MigrationWizardStep.Connect;
    public bool IsSelectStep => CurrentStep == MigrationWizardStep.Select;
    public bool IsAnalyzeStep => CurrentStep == MigrationWizardStep.Analyze;
    public bool IsConvertStep => CurrentStep == MigrationWizardStep.Convert;
    public bool IsDeployStep => CurrentStep == MigrationWizardStep.Deploy;
    public bool IsMigrateStep => CurrentStep == MigrationWizardStep.Migrate;
    public bool IsValidateStep => CurrentStep == MigrationWizardStep.Validate;
    public bool IsFinishStep => CurrentStep == MigrationWizardStep.Finish;

    public bool SourceConnected =>
        Workspace.ConnectionStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) ||
        Workspace.ConnectionStatus.Contains("accessible databases loaded", StringComparison.OrdinalIgnoreCase);

    public bool TargetConnected => Workspace.PostgreSqlTarget.IsConnectionValid;
    public bool CanGoBack => !IsRunning && CurrentStep > MigrationWizardStep.Connect;
    public bool CanGoNext => !IsRunning && IsStepComplete(CurrentStep) &&
                             CurrentStep < MigrationWizardStep.Finish;
    public bool IsCancelVisible => IsRunning &&
        CurrentStep is MigrationWizardStep.Analyze or MigrationWizardStep.Convert or
            MigrationWizardStep.Deploy or MigrationWizardStep.Migrate or
            MigrationWizardStep.Validate;
    public string CancelActionText => CurrentStep switch
    {
        MigrationWizardStep.Analyze => "Cancel analysis",
        MigrationWizardStep.Convert => "Cancel conversion",
        MigrationWizardStep.Deploy => "Cancel deployment",
        MigrationWizardStep.Migrate => "Cancel migration",
        MigrationWizardStep.Validate => "Cancel validation",
        _ => "Cancel"
    };
    public string ActiveOperationDescription =>
        CurrentStep == MigrationWizardStep.Convert ? "Conversion" : CurrentStep.ToString();

    public int SelectedSchemaCount => Workspace.Schemas.Count(item => item.IsSelected && !item.IsExcluded);
    public int SelectedTableCount => Workspace.Objects.Count(item =>
        item.IsSelected && item.Item.ObjectType == InventoryObjectType.Table);
    public long EstimatedRows => Workspace.DataTables.Sum(item => item.EstimatedRows);

    public int TableCount => Workspace.Objects.Count(item => item.Item.ObjectType == InventoryObjectType.Table);
    public int ViewCount => Workspace.Objects.Count(item => item.Item.ObjectType == InventoryObjectType.View);
    public int ProcedureCount => Workspace.Objects.Count(item =>
        item.Item.ObjectType == InventoryObjectType.StoredProcedure);
    public int FunctionCount => Workspace.Objects.Count(item => item.Item.ObjectType == InventoryObjectType.Function);
    public int TriggerCount => Workspace.Objects.Count(item =>
        item.Item.ObjectType is InventoryObjectType.Trigger or
            InventoryObjectType.DatabaseTrigger or InventoryObjectType.ServerTrigger);
    public int WarningCount => Workspace.Findings.Count(item =>
        item.Severity is FindingSeverity.Warning or FindingSeverity.Error);
    public int BlockerCount => Workspace.Findings.Count(item =>
        item.Severity is FindingSeverity.Critical);

    public int SelectedObjectCount => Workspace.SelectedArtifactCount;
    public int ConvertedObjectCount => Workspace.ConvertedArtifactCount;
    public int PackagedObjectCount => Workspace.PackagedArtifactCount;
    public int ExecutableObjectCount => Workspace.PackagedExecutableCount;
    public int ManualReviewObjectCount => Workspace.PackagedManualReviewCount;
    public int UnsupportedObjectCount => Workspace.PackagedUnsupportedCount;
    public int AutomaticallyRenamedCount => Workspace.IdentifierMappings.Count(item =>
        !string.Equals(item.SourceName, item.TargetName, StringComparison.Ordinal));
    public int UnresolvedMappingCount => _conversionSession.Current?.IdentifierMappingSummary.Unresolved ?? 0;

    [RelayCommand]
    private async Task TestSourceAsync()
    {
        await Workspace.TestConnectionCommand.ExecuteAsync(null);
        if (SourceConnected)
        {
            await Workspace.LoadDatabasesCommand.ExecuteAsync(null);
        }
        RefreshConnectionStep();
    }

    [RelayCommand]
    private async Task TestTargetAsync()
    {
        await Workspace.PostgreSqlTarget.TestPostgreSqlConnectionCommand.ExecuteAsync(null);
        RefreshConnectionStep();
    }

    [RelayCommand]
    private void ConfirmSelection()
    {
        if (Workspace.ScopeMode == MigrationScopeMode.ExcelSelectedTables &&
            string.IsNullOrWhiteSpace(Workspace.ExcelPath))
        {
            SetState(MigrationWizardStep.Select, WizardStepState.Blocked,
                "Choose an Excel workbook and match its table selection.");
            return;
        }

        SetState(MigrationWizardStep.Select, WizardStepState.Completed,
            Workspace.ScopeMode == MigrationScopeMode.CompleteDatabase
                ? "Entire database selected."
                : "Selection is ready for analysis.");
        MoveTo(MigrationWizardStep.Analyze);
    }

    [RelayCommand]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        await RunStepAsync(MigrationWizardStep.Analyze, "Analyzing source database…", async () =>
        {
            await ExecuteBackgroundCommandAsync(
                () => Workspace.StartDiscoveryCommand.ExecuteAsync(null),
                operation => operation.Name.StartsWith("Discover ", StringComparison.Ordinal),
                cancellationToken);
            await Workspace.RunCompatibilityAuditCommand.ExecuteAsync(null);
            if (Workspace.ObjectCount == 0)
            {
                throw new InvalidOperationException("Analysis completed without discovering migratable objects.");
            }

            SetState(MigrationWizardStep.Analyze,
                BlockerCount == 0 ? WizardStepState.Completed : WizardStepState.Blocked,
                BlockerCount == 0
                    ? $"Ready to convert · {Workspace.ObjectCount:N0} objects analyzed."
                    : $"Analysis found {BlockerCount:N0} blockers. Review the analysis before conversion.");
        });
    }

    [RelayCommand]
    private async Task ConvertAsync(CancellationToken cancellationToken)
    {
        await RunStepAsync(MigrationWizardStep.Convert, "Converting and building migration package…", async () =>
        {
            await ExecuteBackgroundCommandAsync(
                () => Workspace.StartConversionCommand.ExecuteAsync(null),
                operation => operation.Name.StartsWith("Convert ", StringComparison.Ordinal),
                cancellationToken);

            var run = _conversionSession.Current ??
                throw new InvalidOperationException("Conversion completed without publishing its result.");
            if (run.IdentifierMappingSummary.Unresolved != 0)
            {
                throw new InvalidOperationException(
                    $"{run.IdentifierMappingSummary.Unresolved:N0} required identifier mappings remain unresolved.");
            }

            SynchronizeDeploymentConnection();
            Workspace.PostgreSqlValidationConnectionString =
                Workspace.PostgreSqlTarget.BuildConnectionString();
            await Workspace.ValidateOnPostgreSqlCommand.ExecuteAsync(null);
            cancellationToken.ThrowIfCancellationRequested();

            var package = Workspace.DeploymentPackagePath;
            if (string.IsNullOrWhiteSpace(package))
            {
                throw new InvalidOperationException(
                    $"Live PostgreSQL validation did not publish a deployable package. {Workspace.LiveValidationStatus}");
            }
            var validatedRun = _conversionSession.Current ??
                throw new InvalidOperationException(
                    "Live validation completed without retaining the conversion run.");
            ConversionArtifactReconciler.EnsureSameSourceObjects(
                run.Artifacts,
                validatedRun.Artifacts,
                "wizard live validation");
            if (Workspace.PackagedArtifactCount != validatedRun.Artifacts.Count)
            {
                throw new InvalidDataException(
                    "Validated package reconciliation failed: " +
                    $"converted={validatedRun.Artifacts.Count:N0}, packaged={Workspace.PackagedArtifactCount:N0}.");
            }
            ActivePackage = package;
            SetState(MigrationWizardStep.Convert, WizardStepState.Completed,
                $"Conversion and live PostgreSQL validation complete · {run.Artifacts.Count:N0} objects · validated package verified.");
        });
    }

    [RelayCommand]
    private async Task AssessTargetAsync()
    {
        SynchronizeDeploymentConnection();
        await Workspace.TestDeploymentConnectionCommand.ExecuteAsync(null);
        await Workspace.AssessDeploymentCommand.ExecuteAsync(null);
        SetState(MigrationWizardStep.Deploy,
            Workspace.DeploymentPackageIntegrityValid ? WizardStepState.NotStarted : WizardStepState.Blocked,
            Workspace.DeploymentStatus);
    }

    [RelayCommand]
    private async Task DeployAsync(CancellationToken cancellationToken)
    {
        await RunStepAsync(
            MigrationWizardStep.Deploy,
            "Deploying PostgreSQL schema…",
            async () =>
            {
                SynchronizeDeploymentConnection();

                await Workspace.AssessDeploymentCommand.ExecuteAsync(null);

                if (!Workspace.DeploymentPackageIntegrityValid)
                {
                    throw new InvalidOperationException(
                        $"Target readiness assessment blocked deployment. " +
                        Workspace.DeploymentStatus);
                }

                /*
                 * Do not discover the deployment operation by its display name.
                 * The display name changed in the past and left the wizard stuck
                 * in Running even though the scheduler and workspace had completed.
                 *
                 * WorkspaceViewModel is the source of truth for deployment state.
                 */
                await Workspace.StartDeploymentCommand.ExecuteAsync(null);

                await WaitForDeploymentCompletionAsync(cancellationToken);

                if (!Workspace.HasSuccessfulSchemaDeployment)
                {
                    throw new InvalidOperationException(
                        $"Deployment did not complete successfully. " +
                        Workspace.DeploymentStatus);
                }

                SetState(
                    MigrationWizardStep.Deploy,
                    Workspace.DeploymentSkipped > 0
                        ? WizardStepState.CompletedWithWarnings
                        : WizardStepState.Completed,
                    Workspace.DeploymentStatus);
            });
    }

    [RelayCommand]
    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await RunStepAsync(MigrationWizardStep.Migrate, "Validating target and migrating data…", async () =>
        {
            if (!IsStepComplete(MigrationWizardStep.Deploy))
            {
                throw new InvalidOperationException("Deploy the target schema before migrating data.");
            }

            await Workspace.PreviewDataPlanCommand.ExecuteAsync(null);
            if (Workspace.DataMigrationStatus.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Workspace.DataMigrationStatus);
            }

            await ExecuteBackgroundCommandAsync(
                () => Workspace.StartDataMigrationCommand.ExecuteAsync(null),
                operation => operation.Name.StartsWith("Migrate data", StringComparison.Ordinal),
                cancellationToken);
            if (Workspace.StreamingFailureStage.Length > 0 || Workspace.DataFailures.Count > 0)
            {
                throw new InvalidOperationException(Workspace.DataMigrationStatus);
            }

            SetState(MigrationWizardStep.Migrate, WizardStepState.Completed,
                $"{Workspace.TotalRowsWritten:N0} rows migrated.");
        });
    }

    [RelayCommand]
    private void PauseMigration() => Workspace.PauseDataMigrationCommand.Execute(null);

    [RelayCommand]
    private void ResumeMigration() => Workspace.ContinueDataMigrationCommand.Execute(null);

    [RelayCommand]
    private void CancelMigration() => Workspace.CancelDataMigrationCommand.Execute(null);

    [RelayCommand]
    private void CancelCurrentOperation() => RequestActiveCancellation();

    public void RequestActiveCancellation()
    {
        switch (CurrentStep)
        {
            case MigrationWizardStep.Analyze:
                AnalyzeCommand.Cancel();
                if (Workspace.CancelDiscoveryCommand.CanExecute(null))
                {
                    Workspace.CancelDiscoveryCommand.Execute(null);
                }
                if (Workspace.CancelDoctorCommand.CanExecute(null))
                {
                    Workspace.CancelDoctorCommand.Execute(null);
                }
                break;
            case MigrationWizardStep.Convert:
                if (Workspace.CancelConversionCommand.CanExecute(null))
                {
                    Workspace.CancelConversionCommand.Execute(null);
                }
                else
                {
                    // Conversion has already published and package/report generation is
                    // running under the wizard token rather than the operation scheduler.
                    ConvertCommand.Cancel();
                    Workspace.ValidateOnPostgreSqlCommand.Cancel();
                }
                break;
            case MigrationWizardStep.Deploy:
                DeployCommand.Cancel();
                Workspace.CancelDeploymentCommand.Execute(null);
                break;
            case MigrationWizardStep.Migrate:
                MigrateCommand.Cancel();
                Workspace.CancelDataMigrationCommand.Execute(null);
                break;
            case MigrationWizardStep.Validate:
                ValidateCommand.Cancel();
                Workspace.CancelValidationCommand.Execute(null);
                break;
        }
        WorkflowMessage = $"Cancelling {ActiveOperationDescription.ToLowerInvariant()}…";
    }

    [RelayCommand]
    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await RunStepAsync(MigrationWizardStep.Validate, "Validating migration…", async () =>
        {
            await ExecuteBackgroundCommandAsync(
                () => Workspace.RunValidationCommand.ExecuteAsync(null),
                operation => operation.Name.StartsWith("Validate ", StringComparison.Ordinal),
                cancellationToken);
            if (string.Equals(Workspace.ReadinessStatus, "Failed", StringComparison.OrdinalIgnoreCase) ||
                Workspace.ValidationCriticalBlockers > 0)
            {
                throw new InvalidOperationException(Workspace.ValidationStatus);
            }

            SetState(MigrationWizardStep.Validate,
                Workspace.ValidationFindings.Count > 0
                    ? WizardStepState.CompletedWithWarnings
                    : WizardStepState.Completed,
                Workspace.ValidationStatus);
        });
    }

    [RelayCommand]
    private void CompleteWorkflow()
    {
        FinishedAt = DateTimeOffset.Now;
        SetState(MigrationWizardStep.Finish, WizardStepState.Completed, "Migration workflow complete.");
        WorkflowMessage = "Migration complete. Reports and engineering details remain available in Advanced Mode.";
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => MoveTo(CurrentStep - 1);

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void Next() => MoveTo(CurrentStep + 1);

    [RelayCommand]
    private void GoToStep(MigrationWizardStep step)
    {
        if (step <= CurrentStep || IsStepComplete(step - 1))
        {
            MoveTo(step);
        }
    }

    private async Task RunStepAsync(
        MigrationWizardStep step,
        string runningMessage,
        Func<Task> action)
    {
        if (IsRunning)
        {
            return;
        }

        StartedAt ??= DateTimeOffset.Now;
        IsRunning = true;
        SetState(step, WizardStepState.Running, runningMessage);
        WorkflowMessage = runningMessage;
        try
        {
            await action();
            WorkflowMessage = Steps[(int)step].Message;
            NotifySummaries();
        }
        catch (OperationCanceledException)
        {
            SetState(step, WizardStepState.Cancelled, "Operation cancelled safely. Retry is available.");
            WorkflowMessage = "Operation cancelled safely. Completed work and checkpoints were retained.";
        }
        catch (Exception exception)
        {
            SetState(step, WizardStepState.Failed, exception.Message);
            WorkflowMessage = exception.Message;
        }
        finally
        {
            IsRunning = false;
            NotifyNavigation();
        }
    }

    private async Task WaitForDeploymentCompletionAsync(
        CancellationToken cancellationToken)
    {
        /*
         * StartDeploymentCommand queues work, so command completion does not mean
         * deployment completion. Wait on WorkspaceViewModel's authoritative state.
         * This avoids coupling the wizard to operation display names.
         */
        while (Workspace.IsDeploymentRunning)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
        }

        /*
         * A very fast operation can finish before the first loop condition is read.
         * In that case the completed result is already available here.
         */
        if (Workspace.HasSuccessfulSchemaDeployment)
        {
            return;
        }

        /*
         * Enqueueing and publishing the running flag occur on different callbacks.
         * Give the workspace a short opportunity to publish either Running or a
         * completed result before treating the deployment as not started.
         */
        var startDeadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!Workspace.IsDeploymentRunning &&
               !Workspace.HasSuccessfulSchemaDeployment &&
               Workspace.DeploymentFailed == 0 &&
               DateTimeOffset.UtcNow < startDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, cancellationToken);
        }

        while (Workspace.IsDeploymentRunning)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
        }

        if (Workspace.HasSuccessfulSchemaDeployment)
        {
            return;
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(Workspace.DeploymentStatus)
                ? "Schema deployment did not publish a successful result."
                : Workspace.DeploymentStatus);
    }

    private async Task ExecuteBackgroundCommandAsync(
        Func<Task> start,
        Func<OperationSnapshot, bool> matches,
        CancellationToken cancellationToken)
    {
        var existing = _operations.Operations.Select(item => item.Id).ToHashSet();
        await start();

        OperationSnapshot? operation = null;
        while (operation is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            operation = _operations.Operations.FirstOrDefault(item =>
                !existing.Contains(item.Id) && matches(item));
            if (operation is null)
            {
                await Task.Delay(50, cancellationToken);
            }
        }

        while (operation.IsActive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(100, cancellationToken);
            operation = _operations.Operations.First(item => item.Id == operation.Id);
        }

        if (operation.State != OperationState.Completed)
        {
            if (operation.State == OperationState.Cancelled)
            {
                throw new OperationCanceledException(
                    $"{operation.Name} was cancelled.");
            }
            var detail = operation.Failure?.Remediation;
            throw new InvalidOperationException(
                string.Join(" ", new[] { operation.ErrorMessage, detail }
                    .Where(item => !string.IsNullOrWhiteSpace(item))));
        }
    }

    private void SynchronizeDeploymentConnection()
    {
        var target = Workspace.PostgreSqlTarget;
        Workspace.DeploymentHost = target.Host;
        Workspace.DeploymentPort = target.Port;
        Workspace.DeploymentTargetDatabase = target.Database;
        Workspace.DeploymentUsername = target.Username;
        Workspace.DeploymentPassword = target.Password;
        Workspace.DeploymentSslMode = target.UseSsl ? target.SelectedSslMode.ToString() : "Disable";
        if (string.IsNullOrWhiteSpace(Workspace.MaintenanceDatabase))
        {
            Workspace.MaintenanceDatabase = "postgres";
        }
    }

    private void RefreshConnectionStep()
    {
        var ready = SourceConnected && TargetConnected &&
                    !string.IsNullOrWhiteSpace(Workspace.SelectedDatabase);
        SetState(MigrationWizardStep.Connect,
            ready ? WizardStepState.Completed : WizardStepState.NotStarted,
            ready
                ? "Source and target connections are ready."
                : "Test both connections and select a source database.");
        NotifyNavigation();
    }

    private void InvalidateAfter(MigrationWizardStep step, string reason)
    {
        if (_internalChange)
        {
            return;
        }

        for (var index = (int)step + 1; index < Steps.Count; index++)
        {
            if (Steps[index].State is WizardStepState.Completed or
                WizardStepState.CompletedWithWarnings or WizardStepState.Running)
            {
                Steps[index].State = WizardStepState.Stale;
                Steps[index].Message = reason;
            }
        }

        if (step < MigrationWizardStep.Convert)
        {
            ActivePackage = string.Empty;
            Workspace.DeploymentPackagePath = string.Empty;
        }

        WorkflowMessage = reason;
        NotifyNavigation();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WorkspaceViewModel.Server) or
            nameof(WorkspaceViewModel.Port) or nameof(WorkspaceViewModel.UseWindowsAuthentication) or
            nameof(WorkspaceViewModel.Username) or nameof(WorkspaceViewModel.Password) or
            nameof(WorkspaceViewModel.SelectedDatabase))
        {
            RefreshConnectionStep();
            InvalidateAfter(MigrationWizardStep.Connect,
                "Source connection changed. Selection and later steps must be rerun.");
        }
        else if (args.PropertyName is nameof(WorkspaceViewModel.ScopeMode) or
                 nameof(WorkspaceViewModel.ExcelPath))
        {
            InvalidateAfter(MigrationWizardStep.Select,
                "Source selection changed. Analysis and later steps must be rerun.");
        }

        NotifySummaries();
    }

    private void OnTargetPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshConnectionStep();
        if (args.PropertyName is not nameof(PostgreSqlConnectionViewModel.ConnectionStatus) and
            not nameof(PostgreSqlConnectionViewModel.ValidationMessage) and
            not nameof(PostgreSqlConnectionViewModel.IsTesting))
        {
            InvalidateAfter(MigrationWizardStep.Connect,
                "Target connection changed. Deployment and later steps must be rerun.");
        }
    }

    private bool IsStepComplete(MigrationWizardStep step) =>
        Steps[(int)step].State is WizardStepState.Completed or WizardStepState.CompletedWithWarnings;

    private void SetState(MigrationWizardStep step, WizardStepState state, string message)
    {
        _internalChange = true;
        try
        {
            Steps[(int)step].State = state;
            Steps[(int)step].Message = message;
        }
        finally
        {
            _internalChange = false;
        }
        NotifyNavigation();
    }

    private void MoveTo(MigrationWizardStep step)
    {
        CurrentStep = step;
        WorkflowMessage = Steps[(int)step].Message;
    }

    partial void OnCurrentStepChanged(MigrationWizardStep value)
    {
        OnPropertyChanged(nameof(IsConnectStep));
        OnPropertyChanged(nameof(IsSelectStep));
        OnPropertyChanged(nameof(IsAnalyzeStep));
        OnPropertyChanged(nameof(IsConvertStep));
        OnPropertyChanged(nameof(IsDeployStep));
        OnPropertyChanged(nameof(IsMigrateStep));
        OnPropertyChanged(nameof(IsValidateStep));
        OnPropertyChanged(nameof(IsFinishStep));
        OnPropertyChanged(nameof(IsCancelVisible));
        OnPropertyChanged(nameof(CancelActionText));
        OnPropertyChanged(nameof(ActiveOperationDescription));
        NotifyNavigation();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCancelVisible));
        NotifyNavigation();
    }

    private void NotifyNavigation()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
    }

    private void NotifySummaries()
    {
        OnPropertyChanged(nameof(SourceConnected));
        OnPropertyChanged(nameof(TargetConnected));
        OnPropertyChanged(nameof(SelectedSchemaCount));
        OnPropertyChanged(nameof(SelectedTableCount));
        OnPropertyChanged(nameof(EstimatedRows));
        OnPropertyChanged(nameof(TableCount));
        OnPropertyChanged(nameof(ViewCount));
        OnPropertyChanged(nameof(ProcedureCount));
        OnPropertyChanged(nameof(FunctionCount));
        OnPropertyChanged(nameof(TriggerCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(BlockerCount));
        OnPropertyChanged(nameof(SelectedObjectCount));
        OnPropertyChanged(nameof(ConvertedObjectCount));
        OnPropertyChanged(nameof(PackagedObjectCount));
        OnPropertyChanged(nameof(ExecutableObjectCount));
        OnPropertyChanged(nameof(ManualReviewObjectCount));
        OnPropertyChanged(nameof(UnsupportedObjectCount));
        OnPropertyChanged(nameof(AutomaticallyRenamedCount));
        OnPropertyChanged(nameof(UnresolvedMappingCount));
    }

    public void Dispose()
    {
        Workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        Workspace.PostgreSqlTarget.PropertyChanged -= OnTargetPropertyChanged;
    }
}
