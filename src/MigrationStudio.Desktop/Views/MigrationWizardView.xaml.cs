using System.Windows;
using System.Windows.Controls;
using MigrationStudio.Desktop.ViewModels;

namespace MigrationStudio.Desktop.Views;

public partial class MigrationWizardView : UserControl
{
    public MigrationWizardView() => InitializeComponent();

    private void SqlServerPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox &&
            DataContext is MigrationWizardViewModel wizard)
        {
            wizard.Workspace.Password = passwordBox.Password;
        }
    }

    private void PostgreSqlPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox &&
            DataContext is MigrationWizardViewModel wizard)
        {
            wizard.Workspace.PostgreSqlTarget.Password = passwordBox.Password;
        }
    }
}
