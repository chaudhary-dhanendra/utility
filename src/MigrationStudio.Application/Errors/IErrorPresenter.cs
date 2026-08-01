namespace MigrationStudio.Application.Errors;

public interface IErrorPresenter
{
    void ShowRecoverable(string title, string message);

    void ShowFatal(string title, string message);
}
