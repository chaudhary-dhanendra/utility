using System.Windows;
using System.Diagnostics;
using System.IO;
using MigrationStudio.Application.Errors;
using MigrationStudio.Application.Platform;
using MigrationStudio.Desktop.Threading;

namespace MigrationStudio.Desktop.Errors;

public sealed class WpfErrorPresenter(
    IUiDispatcher dispatcher,
    IApplicationPaths paths) : IErrorPresenter
{
    public void ShowRecoverable(string title, string message) =>
        dispatcher.Invoke(() => Show(title, message, MessageBoxImage.Warning));

    public void ShowFatal(string title, string message) =>
        dispatcher.Invoke(() => Show(title, message, MessageBoxImage.Error));

    private void Show(string title, string message, MessageBoxImage image)
    {
        var result = MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}" +
            "Details were written to a sanitized application log. Open the log folder?",
            title,
            MessageBoxButton.YesNo,
            image);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        Directory.CreateDirectory(paths.LogsDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{paths.LogsDirectory}\"",
            UseShellExecute = true
        });
    }
}
