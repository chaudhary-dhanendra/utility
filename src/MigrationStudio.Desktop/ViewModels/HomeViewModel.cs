using CommunityToolkit.Mvvm.ComponentModel;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class HomeViewModel : ObservableObject
{
    public string Title { get; } = "Welcome";

    public string Description { get; } =
        "Open the migration workspace to discover, convert, deploy, transfer, validate, and report a SQL Server to PostgreSQL migration.";
}
