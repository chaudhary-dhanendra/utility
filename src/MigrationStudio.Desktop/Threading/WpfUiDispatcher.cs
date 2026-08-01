using System.Windows.Threading;

namespace MigrationStudio.Desktop.Threading;

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
