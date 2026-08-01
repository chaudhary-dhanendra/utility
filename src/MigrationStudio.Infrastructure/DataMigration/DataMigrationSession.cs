using MigrationStudio.Application.DataMigration;
using MigrationStudio.Domain.DataMigration;

namespace MigrationStudio.Infrastructure.DataMigration;

public sealed class DataMigrationSession : IDataMigrationSession
{
    private readonly object _sync = new();

    public DataMigrationPlan? CurrentPlan { get; private set; }

    public DataMigrationResult? CurrentResult { get; private set; }

    public event EventHandler? Changed;

    public void SetPlan(DataMigrationPlan plan)
    {
        lock (_sync)
        {
            CurrentPlan = plan;
            CurrentResult = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetResult(DataMigrationResult result)
    {
        lock (_sync)
        {
            CurrentResult = result;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
