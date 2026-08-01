using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.DataMigration;

public interface IMigrationWavePlanner
{
    MigrationWavePlan CreatePlan(InventorySnapshot inventory);
}
