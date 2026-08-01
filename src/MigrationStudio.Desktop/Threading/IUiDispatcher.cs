namespace MigrationStudio.Desktop.Threading;

public interface IUiDispatcher
{
    void Invoke(Action action);

    void Post(Action action);
}
