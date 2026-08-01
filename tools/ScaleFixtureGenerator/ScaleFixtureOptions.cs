namespace MigrationStudio.ScaleFixtureGenerator;

public sealed record ScaleFixtureOptions
{
    public string Preset { get; init; } = "Catalog6000";
    public string DatabaseName { get; init; } = "MigrationStudioScale";
    public int Seed { get; init; } = 6000;
    public int SchemaCount { get; init; } = 20;
    public int TableCount { get; init; } = 6000;
    public int ColumnsPerTable { get; init; } = 12;
    public int PrimaryKeyPercent { get; init; } = 90;
    public int ForeignKeyPercent { get; init; } = 40;
    public int IndexPercent { get; init; } = 70;
    public int ComputedColumnPercent { get; init; } = 10;
    public int LargeTextPercent { get; init; } = 5;
    public int BinaryPercent { get; init; } = 5;
    public int RowsPerTable { get; init; }
    public int DependencyDensity { get; init; } = 2;
    public int ViewCount { get; init; } = 500;
    public int FunctionCount { get; init; } = 200;
    public int ProcedureCount { get; init; } = 300;
    public int TriggerCount { get; init; } = 200;
    public int LongIdentifierPercent { get; init; } = 5;
    public int WideTablePercent { get; init; } = 2;
    public int DataBatchSize { get; init; } = 10_000;
    public int LargeValueBytes { get; init; } = 16_384;

    public void Validate()
    {
        if (SchemaCount is < 1 or > 1000 || TableCount is < 1 or > 100_000 ||
            ColumnsPerTable is < 8 or > 1024 || RowsPerTable < 0 ||
            DataBatchSize is < 100 or > 1_000_000 || DependencyDensity is < 0 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(ScaleFixtureOptions), "Fixture dimensions are outside supported safety limits.");
        }

        foreach (var percentage in new[]
                 {
                     PrimaryKeyPercent, ForeignKeyPercent, IndexPercent, ComputedColumnPercent,
                     LargeTextPercent, BinaryPercent, LongIdentifierPercent, WideTablePercent
                 })
        {
            if (percentage is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(ScaleFixtureOptions), "Percentages must be between zero and 100.");
            }
        }
    }
}

public sealed record ScaleFixtureManifest(
    int FormatVersion,
    string Preset,
    string DatabaseName,
    int Seed,
    int Schemas,
    int Tables,
    long Columns,
    long ExpectedRows,
    int Views,
    int Functions,
    int Procedures,
    int Triggers,
    int ControlledCycles,
    ScaleFixtureOptions Configuration,
    DateTimeOffset GeneratedAt);
