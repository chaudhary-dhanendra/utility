using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationStudio.Application.Navigation;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Errors;
using MigrationStudio.Application.Platform;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.Navigation;
using MigrationStudio.Desktop.Threading;
using MigrationStudio.Domain.Operations;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IDesktopNavigationService _navigation;
    private readonly IOperationMonitor _operationMonitor;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly WorkspaceViewModel _workspace;
    private readonly MigrationWizardViewModel _wizard;
    private readonly IBackgroundOperationScheduler _scheduler;
    private readonly IApplicationPaths _paths;
    private readonly IErrorPresenter _errors;
    private double _lastExplorerWidth;
    private double _lastInspectorWidth;
    private double _lastOutputHeight;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _progressPercentage;

    [ObservableProperty]
    private bool _isProgressVisible;

    [ObservableProperty]
    private GridLength _explorerWidth;

    [ObservableProperty]
    private GridLength _inspectorWidth;

    [ObservableProperty]
    private GridLength _outputHeight;

    public ShellViewModel(
        IDesktopNavigationService navigation,
        IOperationMonitor operationMonitor,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        WorkspaceViewModel workspace,
        MigrationWizardViewModel wizard,
        IBackgroundOperationScheduler scheduler,
        IApplicationPaths paths,
        IErrorPresenter errors)
    {
        _navigation = navigation;
        _operationMonitor = operationMonitor;
        _settings = settings;
        _dispatcher = dispatcher;
        _workspace = workspace;
        _wizard = wizard;
        _scheduler = scheduler;
        _paths = paths;
        _errors = errors;

        var layout = settings.Current.DockLayout;
        _lastExplorerWidth = layout.ExplorerWidth;
        _lastInspectorWidth = layout.InspectorWidth;
        _lastOutputHeight = layout.OutputHeight;
        ExplorerWidth = layout.IsExplorerVisible ? new GridLength(layout.ExplorerWidth) : new GridLength(0);
        InspectorWidth = layout.IsInspectorVisible ? new GridLength(layout.InspectorWidth) : new GridLength(0);
        OutputHeight = layout.IsOutputVisible ? new GridLength(layout.OutputHeight) : new GridLength(0);

        _navigation.Navigated += OnNavigated;
        _operationMonitor.Changed += OnOperationChanged;
        _navigation.Initialize();
    }

    public bool CanGoBack => _navigation.CanGoBack;

    public string VersionDisplay { get; } = BuildIdentity.Footer;

    public string BuildIdentification { get; } = BuildIdentity.Details;

    public IReadOnlyList<OperationSnapshot> Operations => _operationMonitor.Operations;

    public bool HasActiveOperation =>
        _wizard.IsRunning || _operationMonitor.Operations.Any(item => item.IsActive);

    public string ActiveOperationDescription =>
        _wizard.IsRunning
            ? _wizard.ActiveOperationDescription
            : _operationMonitor.Current?.Name ?? "An operation";

    public void CancelActiveOperations()
    {
        if (_wizard.IsRunning)
        {
            _wizard.RequestActiveCancellation();
        }
        foreach (var operation in _operationMonitor.Operations.Where(item => item.IsActive))
        {
            _scheduler.Cancel(operation.Id);
        }
    }

    public bool IsSimpleMode => _settings.Current.ExperienceMode == ExperienceMode.Simple;

    public bool IsAdvancedMode => !IsSimpleMode;

    [RelayCommand]
    private async Task UseSimpleModeAsync()
    {
        await SetExperienceModeAsync(ExperienceMode.Simple);
        _navigation.Navigate(NavigationRoute.Workspace);
    }

    [RelayCommand]
    private async Task UseAdvancedModeAsync()
    {
        await SetExperienceModeAsync(ExperienceMode.Advanced);
        _navigation.Navigate(NavigationRoute.AdvancedWorkspace);
    }

    private async Task SetExperienceModeAsync(ExperienceMode mode)
    {
        if (_settings.Current.ExperienceMode != mode)
        {
            await _settings.SaveAsync(
                _settings.Current with { ExperienceMode = mode },
                CancellationToken.None);
        }

        OnPropertyChanged(nameof(IsSimpleMode));
        OnPropertyChanged(nameof(IsAdvancedMode));
    }

    [RelayCommand]
    private void ShowAbout() =>
        MessageBox.Show(
            BuildIdentification,
            "About SQL Server to PostgreSQL Migration Studio",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private static class BuildIdentity
    {
        private static readonly Assembly Assembly = typeof(ShellViewModel).Assembly;
        private static readonly IReadOnlyDictionary<string, string> Metadata = Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last().Value ?? string.Empty,
                StringComparer.Ordinal);

        private static string Version =>
            FileVersionInfo.GetVersionInfo(Assembly.Location).ProductVersion ??
            Assembly.GetName().Version?.ToString(3) ??
            "unknown";

        private static string Timestamp =>
            Metadata.GetValueOrDefault("BuildTimestamp") is { Length: > 0 } value
                ? value
                : $"{File.GetLastWriteTimeUtc(Assembly.Location):yyyy-MM-ddTHH:mm:ssZ}";

        private static string Commit =>
            Metadata.GetValueOrDefault("CommitHash") is { Length: > 0 } value
                ? value
                : "unavailable";

        public static string Footer =>
            $"Version {Version} · build {Timestamp} · commit {Commit}";

        public static string Details =>
            $"SQL Server to PostgreSQL Migration Studio{Environment.NewLine}" +
            $"Application version: {Version}{Environment.NewLine}" +
            $"Build timestamp: {Timestamp}{Environment.NewLine}" +
            $"Commit hash: {Commit}";
    }

    [RelayCommand]
    private void Navigate(NavigationRoute route) => _navigation.Navigate(route);

    [RelayCommand]
    private void GoBack()
    {
        _navigation.GoBack();
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ToggleExplorer()
    {
        if (ExplorerWidth.Value > 0)
        {
            _lastExplorerWidth = ExplorerWidth.Value;
            ExplorerWidth = new GridLength(0);
        }
        else
        {
            ExplorerWidth = new GridLength(_lastExplorerWidth);
        }
    }

    [RelayCommand]
    private void ToggleInspector()
    {
        if (InspectorWidth.Value > 0)
        {
            _lastInspectorWidth = InspectorWidth.Value;
            InspectorWidth = new GridLength(0);
        }
        else
        {
            InspectorWidth = new GridLength(_lastInspectorWidth);
        }
    }

    [RelayCommand]
    private void ToggleOutput()
    {
        if (OutputHeight.Value > 0)
        {
            _lastOutputHeight = OutputHeight.Value;
            OutputHeight = new GridLength(0);
        }
        else
        {
            OutputHeight = new GridLength(_lastOutputHeight);
        }
    }

    [RelayCommand]
    private void OpenDiscoveryDoctor()
    {
        OpenDoctorPanel();
    }

    [RelayCommand]
    private async Task RunDatabaseCompatibilityAuditAsync()
    {
        OpenDoctorPanel();
        await _workspace.RunCompatibilityAuditCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task OpenCatalogQueryExplorerAsync()
    {
        OpenDoctorPanel();
        if (_workspace.DoctorQueries.Count == 0)
        {
            await _workspace.RunCompatibilityAuditCommand.ExecuteAsync(null);
        }
    }

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        OpenDoctorPanel();
        await _workspace.ExportDoctorDiagnosticsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_paths.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.LogsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            _errors.ShowRecoverable("Log folder could not be opened", exception.Message);
        }
    }

    private void OpenDoctorPanel()
    {
        _navigation.Navigate(NavigationRoute.AdvancedWorkspace);
        _workspace.ActivateDiscoveryDoctor();
    }

    public async Task SaveLayoutAsync(CancellationToken cancellationToken)
    {
        var current = _settings.Current;
        var layout = current.DockLayout with
        {
            ExplorerWidth = ExplorerWidth.Value > 0 ? ExplorerWidth.Value : _lastExplorerWidth,
            InspectorWidth = InspectorWidth.Value > 0 ? InspectorWidth.Value : _lastInspectorWidth,
            OutputHeight = OutputHeight.Value > 0 ? OutputHeight.Value : _lastOutputHeight,
            IsExplorerVisible = ExplorerWidth.Value > 0,
            IsInspectorVisible = InspectorWidth.Value > 0,
            IsOutputVisible = OutputHeight.Value > 0
        };

        await _settings.SaveAsync(current with { DockLayout = layout }, cancellationToken);
    }

    private void OnNavigated(object? sender, NavigationChangedEventArgs args)
    {
        CurrentPage = _navigation.CurrentViewModel;
        StatusText = args.Route.ToString();
        OnPropertyChanged(nameof(CanGoBack));
        GoBackCommand.NotifyCanExecuteChanged();
    }

    private void OnOperationChanged(object? sender, EventArgs args)
    {
        _dispatcher.Post(() =>
        {
            OnPropertyChanged(nameof(Operations));
            OperationSnapshot? operation = _operationMonitor.Current;
            if (operation is null)
            {
                IsProgressVisible = false;
                ProgressPercentage = 0;
                StatusText = "Ready";
                return;
            }

            IsProgressVisible = true;
            ProgressPercentage = operation.Progress.Percentage;
            StatusText = $"{operation.Name}: {operation.Progress.Message}";
        });
    }

    public void Dispose()
    {
        _navigation.Navigated -= OnNavigated;
        _operationMonitor.Changed -= OnOperationChanged;
    }
}
