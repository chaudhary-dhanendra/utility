namespace MigrationStudio.ScaleFixtureGenerator;

public static class ScaleFixturePresets
{
    public static ScaleFixtureOptions Get(string name) =>
        name.ToUpperInvariant() switch
        {
            "CATALOG6000" => new ScaleFixtureOptions(),
            "CATALOG6000WITHDEPENDENCIES" => new ScaleFixtureOptions
            {
                Preset = "Catalog6000WithDependencies",
                ForeignKeyPercent = 75,
                DependencyDensity = 5,
                ViewCount = 1200,
                FunctionCount = 500,
                ProcedureCount = 800,
                TriggerCount = 600
            },
            "DATA10GBAPPROXIMATE" => new ScaleFixtureOptions
            {
                Preset = "Data10GBApproximate",
                SchemaCount = 8,
                TableCount = 1000,
                ColumnsPerTable = 16,
                RowsPerTable = 250_000,
                LargeTextPercent = 20,
                BinaryPercent = 20,
                LargeValueBytes = 32_768
            },
            "WIDETABLESTRESS" => new ScaleFixtureOptions
            {
                Preset = "WideTableStress",
                SchemaCount = 4,
                TableCount = 250,
                ColumnsPerTable = 300,
                WideTablePercent = 100,
                RowsPerTable = 20_000,
                LargeTextPercent = 15,
                BinaryPercent = 15
            },
            "REPORTSTRESS" => new ScaleFixtureOptions
            {
                Preset = "ReportStress",
                TableCount = 6000,
                ColumnsPerTable = 18,
                IndexPercent = 90,
                ForeignKeyPercent = 70,
                ViewCount = 1500,
                FunctionCount = 700,
                ProcedureCount = 1000,
                TriggerCount = 800
            },
            "CANCELLATIONSTRESS" => new ScaleFixtureOptions
            {
                Preset = "CancellationStress",
                TableCount = 6000,
                ColumnsPerTable = 24,
                RowsPerTable = 1_000_000,
                LargeTextPercent = 30,
                BinaryPercent = 30,
                DataBatchSize = 1000
            },
            _ => throw new ArgumentException(
                $"Unknown preset '{name}'. Valid presets: Catalog6000, Catalog6000WithDependencies, Data10GBApproximate, WideTableStress, ReportStress, CancellationStress.")
        };
}
