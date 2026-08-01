using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using ClosedXML.Excel;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Reporting;
using MigrationStudio.Domain.Validation;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.Excel;
using MigrationStudio.Reporting;
using MigrationStudio.ScaleTests;
using MigrationStudio.Validation;

namespace MigrationStudio.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class CoreScaleBenchmarks
{
    private InventorySnapshot _catalog = null!;
    private InventorySnapshot _identifierCatalog = null!;
    private InventoryObjectId[] _graphNodes = null!;
    private InventoryDependency[] _graphEdges = null!;
    private string _excelPath = null!;
    private string _snapshotPath = null!;
    private MigrationReportRequest _reportRequest = null!;
    private IReadOnlyList<IReadOnlyList<CanonicalValue>> _checksumRows = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _catalog = SyntheticInventoryFactory.Create();
        _identifierCatalog = CreateIdentifierCatalog(100_000);
        _graphNodes = Enumerable.Range(1, 50_000)
            .Select(index => new InventoryObjectId(new Guid(index, 0, 0, new byte[8]))).ToArray();
        _graphEdges = Enumerable.Range(0, 200_000).Select(index =>
            new InventoryDependency(
                _graphNodes[index % _graphNodes.Length],
                _graphNodes[(index + (index % 997) + 1) % _graphNodes.Length],
                DependencyKind.SqlExpression, "benchmark", true, false)).ToArray();
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-Benchmarks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _excelPath = Path.Combine(root, "selection.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Selection");
            sheet.Cell(1, 1).Value = "Table";
            var row = 2;
            foreach (var table in _catalog.Objects.Where(item => item.ObjectType == InventoryObjectType.Table))
            {
                sheet.Cell(row++, 1).Value = $"{table.SourceSchema}.{table.SourceName}";
            }
            workbook.SaveAs(_excelPath);
        }
        _snapshotPath = Path.Combine(root, "snapshot.msinventory");
        await new CompressedJsonInventorySnapshotStore()
            .SaveAsync(_catalog, _snapshotPath, CancellationToken.None);
        _reportRequest = new MigrationReportRequest
        {
            Inventory = _catalog,
            Source = new MigrationEndpointSummary("synthetic", "Catalog6000", "16", "Synthetic"),
            Target = new MigrationEndpointSummary("target", "scale", "18", "PostgreSQL"),
            ApplicationVersion = "1.0.0"
        };
        _checksumRows = Enumerable.Range(0, 100_000)
            .Select(index => (IReadOnlyList<CanonicalValue>)
            [
                new(CanonicalValueKind.IntegralNumber, index.ToString(System.Globalization.CultureInfo.InvariantCulture), false),
                new(CanonicalValueKind.Text, $"value-{index:D6}", false)
            ]).ToArray();
    }

    [Benchmark]
    public Dictionary<string, InventoryObject> MetadataMapping() =>
        _catalog.Objects.ToDictionary(item => item.QualifiedSourceName, StringComparer.Ordinal);

    [Benchmark]
    public object IdentifierMapping100000() =>
        new PostgreSqlIdentifierMappingService().CreateMapper(
            _identifierCatalog, new ConversionOptions());

    [Benchmark]
    public IReadOnlyList<DependencyComponent> DependencyGraph50000x200000() =>
        DependencyGraphAnalyzer.FindStronglyConnectedComponents(_graphNodes, _graphEdges);

    [Benchmark]
    public Task<MigrationStudio.Application.Discovery.ExcelTableSelectionResult> ExcelTableNameMatching6000() =>
        new ClosedXmlTableSelectionService().MatchAsync(
            new MigrationStudio.Application.Discovery.ExcelTableSelectionOptions(
                _excelPath, "Selection", "Table"),
            _catalog.Objects, CancellationToken.None);

    [Benchmark]
    public InventoryObject[] InventoryFiltering6000() =>
        _catalog.Objects.Where(item =>
                item.QualifiedSourceName.Contains("Table_005", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    [Benchmark]
    public Task SnapshotSerialization() =>
        new CompressedJsonInventorySnapshotStore().SaveAsync(
            _catalog, _snapshotPath + ".benchmark", CancellationToken.None);

    [Benchmark]
    public Task<InventorySnapshot> SnapshotDeserialization() =>
        new CompressedJsonInventorySnapshotStore().LoadAsync(
            _snapshotPath, CancellationToken.None);

    [Benchmark]
    public MigrationReportDocument ReportDataPreparation() =>
        MigrationReportDocumentBuilder.Build(_reportRequest, Guid.Empty);

    [Benchmark]
    public string CanonicalChecksum100000Rows() =>
        new CanonicalChecksumService().HashOrderedRows(_checksumRows);

    private static InventorySnapshot CreateIdentifierCatalog(int count)
    {
        var objects = Enumerable.Range(0, count).Select(index =>
        {
            var schema = $"schema_{index % 100:D3}";
            var name = index % 10 == 0
                ? $"Identifier_{index:D6}_Exceeding_PostgreSQL_Sixty_Three_Byte_Limit_For_Benchmark"
                : $"Table_{index:D6}";
            var id = InventoryObjectId.Create(
                "IdentifierBenchmark", InventoryObjectType.Table, schema, name, index);
            return new InventoryObject(
                id, "IdentifierBenchmark", schema, name, $"[{schema}].[{name}]",
                InventoryObjectType.Table, index, null, null, null, false, true,
                SelectionReason.CompleteDatabase, 0, 0, [], ConversionClassification.Automatic,
                null, null, $"hash-{index}", [], DiscoveryStatus.Discovered);
        }).ToArray();
        return new InventorySnapshot
        {
            SnapshotTimestamp = DateTimeOffset.UnixEpoch,
            ScopeMode = MigrationScopeMode.CompleteDatabase,
            Database = SyntheticInventoryFactory.Create(1, 12, 1).Database,
            Objects = objects
        };
    }
}
