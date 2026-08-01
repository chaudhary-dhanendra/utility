using System.Globalization;
using MigrationStudio.ScaleFixtureGenerator;

var arguments = args
    .Select((value, index) => (value, index))
    .Where(item => item.value.StartsWith("--", StringComparison.Ordinal))
    .ToDictionary(
        item => item.value[2..],
        item => item.index + 1 < args.Length && !args[item.index + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[item.index + 1]
            : "true",
        StringComparer.OrdinalIgnoreCase);

var presetName = arguments.GetValueOrDefault("preset", "Catalog6000");
var output = Path.GetFullPath(arguments.GetValueOrDefault(
    "output", Path.Combine(Environment.CurrentDirectory, "artifacts", "scale-fixtures")));
var options = ScaleFixturePresets.Get(presetName) with
{
    DatabaseName = arguments.GetValueOrDefault("database", ScaleFixturePresets.Get(presetName).DatabaseName),
    Seed = Int("seed", ScaleFixturePresets.Get(presetName).Seed),
    SchemaCount = Int("schemas", ScaleFixturePresets.Get(presetName).SchemaCount),
    TableCount = Int("tables", ScaleFixturePresets.Get(presetName).TableCount),
    ColumnsPerTable = Int("columns", ScaleFixturePresets.Get(presetName).ColumnsPerTable),
    RowsPerTable = Int("rows", ScaleFixturePresets.Get(presetName).RowsPerTable)
};

var manifest = await SqlServerScaleFixtureWriter.WriteAsync(options, output, CancellationToken.None);
Console.WriteLine($"Generated {manifest.Preset}: {manifest.Tables:N0} tables, {manifest.Columns:N0} columns, {manifest.ExpectedRows:N0} expected rows.");
Console.WriteLine($"Output: {output}");
return;

int Int(string name, int fallback) =>
    arguments.TryGetValue(name, out var value)
        ? int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)
        : fallback;
