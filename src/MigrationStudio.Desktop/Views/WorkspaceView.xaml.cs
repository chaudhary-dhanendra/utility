using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Desktop.Views;

public partial class WorkspaceView : UserControl
{
    public WorkspaceView() => InitializeComponent();

    private void PostgreSqlTargetPasswordBox_OnPasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox)
        {
            return;
        }

        var connection =
            passwordBox.Tag as PostgreSqlConnectionViewModel ??
            (DataContext as WorkspaceViewModel)?.PostgreSqlTarget;
        if (connection is not null)
        {
            connection.Password = passwordBox.Password;
        }
    }
}
