namespace MigrationStudio.Desktop.Dialogs;

public interface IFileDialogService
{
    string? Open(string filter);

    string? Save(string filter, string defaultExtension, string suggestedName);

    string? SelectFolder(string title);
}
