using System.ComponentModel;
using System.IO;
using System.Windows;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Desktop;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settings;
    private bool _closeConfirmed;
    private bool _layoutSaved;

    public MainWindow(ShellViewModel viewModel, ISettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settings = settings;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_layoutSaved)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (DataContext is ShellViewModel activeShell && activeShell.HasActiveOperation)
        {
            var prompt = activeShell.ActiveOperationDescription.Equals(
                "Conversion",
                StringComparison.OrdinalIgnoreCase)
                ? "Conversion is running. Cancel and exit?"
                : $"{activeShell.ActiveOperationDescription} is running. Cancel and exit?";
            var cancelAndExit = MessageBox.Show(
                prompt,
                "Active migration operation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (cancelAndExit != MessageBoxResult.Yes)
            {
                return;
            }

            _closeConfirmed = true;
            activeShell.CancelActiveOperations();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
            while (activeShell.HasActiveOperation && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }

        if (!_closeConfirmed && _settings.Current.ConfirmBeforeExit)
        {
            var result = MessageBox.Show(
                "Close SQL Server to PostgreSQL Migration Studio?",
                "Confirm exit",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _closeConfirmed = true;
        if (DataContext is ShellViewModel shell)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await shell.SaveLayoutAsync(timeout.Token);
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                OperationCanceledException)
            {
                MessageBox.Show(
                    "The window layout could not be saved. The application will still close.",
                    "Migration Studio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        _layoutSaved = true;
        Close();
    }
}
