using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.DataMigration;

public enum MigrationWaveKind
{
    Foundation,
    ReferenceData,
    IndependentTransactional,
    DependentTransactional,
    LargeTables,
    CyclicGroups,
    ProgrammableObjects,
    Security,
    Validation
}

public sealed record MigrationWaveItem(
    InventoryObjectId ObjectId,
    string QualifiedName,
    InventoryObjectType ObjectType,
    long EstimatedRows,
    long EstimatedBytes,
    bool HasLargeObjects,
    int DependencyGroup,
    string Risk);

public sealed record MigrationWave(
    int Sequence,
    MigrationWaveKind Kind,
    string Name,
    IReadOnlyList<MigrationWaveItem> Items,
    long EstimatedRows,
    long EstimatedBytes);

public sealed record MigrationWavePlan(
    DateTimeOffset CreatedAt,
    IReadOnlyList<MigrationWave> Waves);
