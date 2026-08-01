using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Infrastructure.Discovery;

public sealed class InventorySession : IInventorySession
{
    private InventorySnapshot? _current;

    public InventorySnapshot? Current => Volatile.Read(ref _current);

    public event EventHandler? Changed;

    public void SetCurrent(InventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Interlocked.Exchange(ref _current, snapshot);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Clear()
    {
        var previous = Interlocked.Exchange(ref _current, null);

        if (previous is not null)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
