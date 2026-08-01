using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using ClosedXML.Graphics;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Win32;
using MigrationStudio.Application.Platform;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Reporting;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.Excel;
using MigrationStudio.Reporting;
using MigrationStudio.ScaleTests;

var outputDirectory = Path.GetFullPath(
    Argument("output") ?? Path.Combine(Environment.CurrentDirectory, "artifacts", "benchmarks"));
var workDirectory = Path.Combine(outputDirectory, "work");
Directory.CreateDirectory(workDirectory);
var measurements = new List<ScaleMeasurement>();
InventorySnapshot? snapshot = null;

measurements.Add(await MeasurementRunner.RunAsync(
    "Synthetic 6,000-table catalog construction",
    "Release gate",
    _ =>
    {
        snapshot = SyntheticInventoryFactory.Create();
        Require(snapshot.Tables.Count == 6000, "Expected 6,000 tables.");
        Require(snapshot.Columns.Count == 180_000, "Expected 180,000 columns.");
        Require(snapshot.Columns.Count > 150_000, "Scale inventory must exceed 150,000 columns.");
        Require(snapshot.Constraints.Count == 18_000, "Expected 18,000 constraints.");
        return Task.FromResult(Result(
            "Constructed the full catalog without table data.",
            ("Tables", snapshot.Tables.Count), ("Columns", snapshot.Columns.Count),
            ("Constraints", snapshot.Constraints.Count), ("Indexes", snapshot.Indexes.Count),
            ("Dependencies", snapshot.Dependencies.Count)));
    },
    CancellationToken.None));

if (snapshot is null)
{
    throw new InvalidOperationException("The synthetic inventory could not be created.");
}

var snapshotPath = Path.Combine(workDirectory, "Catalog6000.msinventory");
var snapshotStore = new CompressedJsonInventorySnapshotStore();
measurements.Add(await MeasurementRunner.RunAsync(
    "Inventory snapshot save",
    "Release gate",
    async cancellationToken =>
    {
        await snapshotStore.SaveAsync(snapshot, snapshotPath, cancellationToken);
        return Result("Streaming compressed snapshot save completed.",
            ("FileBytes", new FileInfo(snapshotPath).Length));
    },
    CancellationToken.None));

var dependencyOffsets = new[] { 1, 17, 101, 997 };
measurements.Add(await MeasurementRunner.RunAsync(
    "Inventory snapshot reload",
    "Release gate",
    async cancellationToken =>
    {
        var loaded = await snapshotStore.LoadAsync(snapshotPath, cancellationToken);
        Require(loaded.Tables.Count == 6000 && loaded.Columns.Count == 180_000,
            "Reloaded inventory counts differ.");
        snapshot = loaded;
        return Result("Snapshot reloaded and object counts reconciled.",
            ("Tables", loaded.Tables.Count), ("Columns", loaded.Columns.Count));
    },
    CancellationToken.None));

measurements.Add(await MeasurementRunner.RunAsync(
    "50,000-object / 200,000-edge dependency graph",
    "Release gate",
    cancellationToken =>
    {
        var nodes = Enumerable.Range(1, 50_000)
            .Select(index => new InventoryObjectId(new Guid(index, 0, 0, new byte[8]))).ToArray();
        var edges = new List<InventoryDependency>(200_000);
        for (var index = 0; index < nodes.Length; index++)
        {
            foreach (var offset in dependencyOffsets)
            {
                edges.Add(new InventoryDependency(
                    nodes[index], nodes[(index + offset) % nodes.Length],
                    DependencyKind.SqlExpression, "synthetic", true, false));
            }
        }
        var components = DependencyGraphAnalyzer.FindStronglyConnectedComponents(
            nodes, edges, cancellationToken);
        var reversed = DependencyGraphAnalyzer.FindStronglyConnectedComponents(
            nodes.Reverse(), edges.AsEnumerable().Reverse(), cancellationToken);
        Require(components.Count == reversed.Count, "Component count is not deterministic.");
        Require(components.Any(item => item.IsCycle), "Controlled cycles were not detected.");
        var firstHash = ComponentHash(components);
        var secondHash = ComponentHash(reversed);
        Require(firstHash == secondHash, "Component ordering is not deterministic.");
        return Task.FromResult(Result("Graph built, cycles detected, and reversed-input determinism verified.",
            ("Objects", nodes.Length), ("Edges", edges.Count), ("Components", components.Count),
            ("DeterministicHash", firstHash)));
    },
    CancellationToken.None));

measurements.Add(await CancellationMeasurementAsync(
    "Dependency graph cancellation",
    async cancellationToken =>
    {
        var nodes = Enumerable.Range(1, 50_000)
            .Select(index => new InventoryObjectId(new Guid(index, 0, 0, new byte[8]))).ToArray();
        var edges = Enumerable.Range(0, 400_000).Select(index =>
            new InventoryDependency(nodes[index % nodes.Length], nodes[(index + 1) % nodes.Length],
                DependencyKind.SqlExpression, "cancel", true, false)).ToArray();
        await Task.Run(
            () => DependencyGraphAnalyzer.FindStronglyConnectedComponents(nodes, edges, cancellationToken),
            CancellationToken.None);
    }));

var excelPath = Path.Combine(workDirectory, "ExcelSelection6000.xlsx");
measurements.Add(await MeasurementRunner.RunAsync(
    "Excel import with 6,000 table rows and issues",
    "Release gate",
    async cancellationToken =>
    {
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Selection");
            sheet.Cell(1, 1).Value = "Table";
            var row = 2;
            foreach (var table in snapshot.Objects.Where(item => item.ObjectType == InventoryObjectType.Table))
            {
                sheet.Cell(row++, 1).Value = $"{table.SourceSchema}.{table.SourceName}";
            }
            for (var index = 0; index < 100; index++)
            {
                sheet.Cell(row++, 1).Value = snapshot.Objects.First(item => item.ObjectType == InventoryObjectType.Table).QualifiedSourceName;
            }
            for (var index = 0; index < 50; index++) row++;
            sheet.Cell(row++, 1).Value = "SharedTable";
            sheet.Cell(row++, 1).Value = "invalid..name";
            sheet.Cell(row, 1).Value = "missing_schema.missing_table";
            workbook.SaveAs(excelPath);
        }
        var progressCount = 0;
        var service = new ClosedXmlTableSelectionService();
        var result = await service.MatchAsync(
            new MigrationStudio.Application.Discovery.ExcelTableSelectionOptions(
                excelPath, "Selection", "Table"),
            snapshot.Objects,
            new Progress<MigrationStudio.Application.Discovery.ExcelSelectionProgress>(_ => progressCount++),
            cancellationToken);
        Require(result.Matched.Count == 6000, $"Expected 6,000 matches, got {result.Matched.Count}.");
        Require(result.DuplicatesRemoved >= 100, "Duplicate rows were not classified.");
        Require(result.Ambiguous.Count == 1, "Ambiguous unqualified name was not classified.");
        Require(result.Unmatched.Count >= 2, "Invalid and missing names were not reported.");
        return Result("ClosedXML import used only the selected worksheet and indexed matching.",
            ("Matched", result.Matched.Count), ("Duplicates", result.DuplicatesRemoved),
            ("Blanks", result.BlankRowsRemoved), ("Ambiguous", result.Ambiguous.Count),
            ("Unmatched", result.Unmatched.Count), ("ProgressEvents", progressCount));
    },
    CancellationToken.None));

measurements.Add(await CancellationMeasurementAsync(
    "Excel import cancellation",
    async cancellationToken =>
    {
        await new ClosedXmlTableSelectionService().MatchAsync(
            new MigrationStudio.Application.Discovery.ExcelTableSelectionOptions(
                excelPath, "Selection", "Table"),
            snapshot.Objects, null, cancellationToken);
    }));

measurements.Add(await MeasurementRunner.RunAsync(
    "6,000-table UI projection and filter kernel",
    "Local deterministic",
    _ =>
    {
        var viewModels = snapshot.Objects.Where(item => item.ObjectType == InventoryObjectType.Table)
            .Select(item => new InventoryObjectRowViewModel(item)).ToArray();
        var unique = viewModels.Select(item => item.Item.Id).Distinct().Count();
        var stopwatch = Stopwatch.StartNew();
        var filtered = viewModels.Where(item =>
                item.Name.Contains("Table_005", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        stopwatch.Stop();
        Require(unique == 6000, "Duplicate view models were created.");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "Filtering exceeded five seconds.");
        return Task.FromResult(Result(
            "One view model per table; in-memory filter completed. This does not substitute for rendered WPF automation.",
            ("ViewModels", viewModels.Length), ("UniqueObjects", unique),
            ("Matches", filtered.Length), ("FilterMilliseconds", stopwatch.Elapsed.TotalMilliseconds)));
    },
    CancellationToken.None));

var checkpointPaths = new ScaleApplicationPaths(Path.Combine(workDirectory, "state"));
using var checkpointStore = new JsonMigrationCheckpointStore(checkpointPaths);
var checkpointRunId = Guid.NewGuid();
measurements.Add(await MeasurementRunner.RunAsync(
    "Checkpoint persistence and resume load",
    "Release gate",
    async cancellationToken =>
    {
        var tableStates = snapshot.Tables.Select((table, index) => new TableCheckpoint(
            table.ObjectId, $"source.table_{index:D6}", $"target.table_{index:D6}",
            DataTransferStrategy.PostgreSqlBinaryCopy, index / 10, index.ToString(CultureInfo.InvariantCulture),
            index * 1000L, index * 1000L, 0, null, DateTimeOffset.UtcNow,
            index % 3 == 0 ? DateTimeOffset.UtcNow : null,
            index % 3 == 0 ? TableMigrationState.Completed : TableMigrationState.Running, true)).ToArray();
        var checkpoint = new MigrationCheckpoint(
            MigrationCheckpoint.CurrentFormatVersion, checkpointRunId, "source", "source-hash",
            "target", "config-hash", "1.0.0", DateTimeOffset.UtcNow, tableStates);
        var path = await checkpointStore.SaveAsync(checkpoint, cancellationToken);
        var loaded = await checkpointStore.LoadAsync(checkpointRunId, cancellationToken)
            ?? throw new InvalidDataException("Checkpoint reload returned no data.");
        Require(loaded.Tables.Count == 6000, "Checkpoint reload count differs.");
        return Result("Atomic checkpoint save and resume reload completed.",
            ("Tables", loaded.Tables.Count), ("Completed", loaded.Tables.Count(item => item.State == TableMigrationState.Completed)),
            ("FileBytes", new FileInfo(path).Length));
    },
    CancellationToken.None));

measurements.Add(await MeasurementRunner.RunAsync(
    "Three-million-row bounded streaming checksum workload",
    "Local deterministic",
    cancellationToken =>
    {
        const int rows = 3_000_000;
        var binary = new byte[256];
        RandomNumberGenerator.Fill(binary);
        var text = Encoding.UTF8.GetBytes("Synthetic Unicode Ω payload");
        var ordinal = new byte[8];
        using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0;
        for (var row = 0; row < rows; row++)
        {
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            BitConverter.TryWriteBytes(ordinal.AsSpan(), row);
            checksum.AppendData(ordinal);
            checksum.AppendData(text);
            if (row % 1000 == 0) checksum.AppendData(binary);
            bytes += ordinal.Length + text.Length + (row % 1000 == 0 ? binary.Length : 0);
        }
        var hash = Convert.ToHexString(checksum.GetHashAndReset()).ToLowerInvariant();
        return Task.FromResult(Result(
            "Executed a bounded-memory transform/checksum workload; this is not a database migration throughput result.",
            ("Rows", rows), ("Bytes", bytes), ("Checksum", hash)));
    },
    CancellationToken.None));

measurements.Add(await CancellationMeasurementAsync(
    "Streaming data-path cancellation",
    async cancellationToken =>
    {
        await Task.Run(() =>
        {
            var buffer = new byte[4096];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer);
            }
        }, CancellationToken.None);
    }));

var reportDirectory = Path.Combine(workDirectory, "report");
Directory.CreateDirectory(reportDirectory);
var identifierMapper = new PostgreSqlIdentifierMappingService().CreateMapper(
    snapshot,
    new ConversionOptions());
var sourceObjects = snapshot.Objects.ToDictionary(item => item.Id);
foreach (var column in snapshot.Columns)
{
    var owner = sourceObjects[column.ParentObjectId];
    identifierMapper.MapChildIdentifier(owner.Id, "column", owner.SourceSchema, column.Name);
}
foreach (var constraint in snapshot.Constraints)
{
    var owner = sourceObjects[constraint.TableObjectId];
    identifierMapper.MapChildIdentifier(owner.Id, "constraint", owner.SourceSchema, constraint.Name);
}
foreach (var index in snapshot.Indexes)
{
    var owner = sourceObjects[index.TableObjectId];
    identifierMapper.MapChildIdentifier(owner.Id, "index", owner.SourceSchema, index.Name);
}
var identifierRun = new ConversionRun(
    Guid.NewGuid(),
    DateTimeOffset.UtcNow,
    snapshot.Database.DatabaseName,
    new PostgreSqlVersion(18),
    new ConversionOptions(),
    identifierMapper.Mappings,
    [],
    [],
    [],
    [],
    "scale-identifier-audit");
Require(identifierRun.IdentifierMappings.Count > 191_000,
    "Identifier scale gate must exceed 191,000 mapped source objects.");
measurements.Add(await MeasurementRunner.RunAsync(
    "Identifier mapping report generation",
    "Release gate",
    async cancellationToken =>
    {
        await new ConversionReportWriter().WriteAsync(
            identifierRun,
            reportDirectory,
            cancellationToken);
        var workbookPath = Path.Combine(reportDirectory, "Identifier_Mapping.xlsx");
        Require(ReadFirstInlineCell(workbookPath) == "Object type",
            "Streaming identifier workbook header is missing.");
        Require(identifierRun.IdentifierMappings.All(item => item.TargetUtf8ByteLength <= 63),
            "An emitted identifier exceeds 63 UTF-8 bytes.");
        Require(identifierRun.IdentifierMappings.Any(item =>
                item.MappingStatus == IdentifierMappingStatus.AutomaticallyShortened),
            "The long-name fixture did not exercise shortening.");
        var summary = identifierRun.IdentifierMappingSummary;
        Require(summary.Unresolved == 0, "Identifier scale fixture contains unresolved mappings.");
        return Result(
            "Generated the dedicated Excel/CSV/HTML identifier reports with textual status and color presentation.",
            ("Mappings", identifierRun.IdentifierMappings.Count),
            ("Included", summary.TotalIncludedObjects),
            ("Unchanged", summary.Unchanged),
            ("Renamed", summary.Renamed),
            ("Shortened", identifierRun.IdentifierMappings.Count(item => item.WasShortened)),
            ("Collisions", identifierRun.IdentifierMappings.Count(item => item.HadCollision)),
            ("AutoRecovered", summary.AutoRecovered),
            ("Unresolved", summary.Unresolved),
            ("WorkbookBytes", new FileInfo(workbookPath).Length));
    },
    CancellationToken.None));
var reportRequest = new MigrationReportRequest
{
    Inventory = snapshot,
    Source = new MigrationEndpointSummary("synthetic", snapshot.Database.DatabaseName, "16", "Synthetic"),
    Target = new MigrationEndpointSummary("not-connected", "scale_target", "18", "PostgreSQL"),
    Template = new ReportTemplate
    {
        OrganizationName = "Scale qualification",
        ProjectName = "Catalog6000",
        PreparedBy = "MigrationStudio.ScaleTests"
    },
    ApplicationVersion = "1.0.0"
};
var report = MigrationReportDocumentBuilder.Build(reportRequest, Guid.NewGuid());
measurements.Add(await MeasurementRunner.RunAsync(
    "6,000-table / 180,000-column report generation",
    "Release gate",
    async cancellationToken =>
    {
        var excel = Path.Combine(reportDirectory, "ScaleReport.xlsx");
        var html = Path.Combine(reportDirectory, "ScaleReport.html");
        var pdf = Path.Combine(reportDirectory, "ScaleSummary.pdf");
        await Task.Run(
            () => new MigrationExcelReportWriter(maximumRowsPerSheet: 50_000)
                .Write(report, excel, cancellationToken), cancellationToken);
        await MigrationHtmlReportWriter.WriteAsync(report, html, cancellationToken);
        await Task.Run(() => MigrationPdfReportWriter.Write(report, pdf, cancellationToken), cancellationToken);
        var htmlText = await File.ReadAllTextAsync(html, cancellationToken);
        Require(htmlText.Contains("application/json", StringComparison.Ordinal), "HTML paging data was not emitted.");
        Require(htmlText.Contains("size:100", StringComparison.Ordinal), "HTML page size is not bounded.");
        using var workbook = LoadWorkbook(excel);
        Require(workbook.Worksheets.Any(item => item.Name == "Columns 2"),
            "Large detail rows did not split at the configured threshold.");
        Require(workbook.Worksheets.Where(item => item.Name.StartsWith("Columns", StringComparison.Ordinal))
            .All(item => item.SheetView.SplitRow == 1), "Continuation sheets do not freeze headers.");
        return Result("Excel split deterministically; HTML initial DOM is paged; PDF remained summary-focused.",
            ("ExcelBytes", new FileInfo(excel).Length), ("HtmlBytes", new FileInfo(html).Length),
            ("PdfBytes", new FileInfo(pdf).Length), ("Worksheets", workbook.Worksheets.Count));
    },
    CancellationToken.None));

measurements.Add(await CancellationMeasurementAsync(
    "Report generation cancellation",
    async cancellationToken =>
    {
        var canceledPath = Path.Combine(reportDirectory, "CancelledReport.xlsx");
        await Task.Run(
            () => new MigrationExcelReportWriter().Write(report, canceledPath, cancellationToken),
            CancellationToken.None);
    }));

var sqlServerConnection = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_SQLSERVER_INTEGRATION");
var postgresConnection = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
if (string.IsNullOrWhiteSpace(sqlServerConnection))
{
    measurements.Add(MeasurementRunner.Skipped(
        "Live SQL Server 6,000-table discovery and failure injection",
        "MIGRATIONSTUDIO_SQLSERVER_INTEGRATION is not configured; server version, execution plans, dropped connections, permissions, and command cancellation were not measured."));
}
if (string.IsNullOrWhiteSpace(postgresConnection))
{
    measurements.Add(MeasurementRunner.Skipped(
        "Live PostgreSQL binary/text COPY, deployment, validation, and resume",
        "MIGRATIONSTUDIO_POSTGRES_INTEGRATION is not configured; multi-million-row live throughput and server/network failure recovery were not measured."));
}
measurements.Add(new ScaleMeasurement
{
    Name = "Rendered WPF interaction automation",
    Category = "Release gate",
    Status = ScaleTestStatus.NotReproducible,
    StartedAt = DateTimeOffset.UtcNow,
    DurationMilliseconds = 0,
    PeakManagedMemoryBytes = GC.GetTotalMemory(false),
    PeakWorkingSetBytes = Environment.WorkingSet,
    AllocatedBytes = 0,
    Gen0Collections = 0,
    Gen1Collections = 0,
    Gen2Collections = 0,
    Detail = "Projection and filtering were measured, but repeatable keyboard/render/expansion timing requires a dedicated interactive UI automation workstation."
});

var machine = Machine();
var infrastructure = new Dictionary<string, string>(StringComparer.Ordinal)
{
    ["SQL Server"] = string.IsNullOrWhiteSpace(sqlServerConnection) ? "Unavailable; live tests skipped" : "Configured",
    ["PostgreSQL"] = string.IsNullOrWhiteSpace(postgresConnection) ? "Unavailable; live tests skipped" : "Configured",
    ["Fixture"] = "In-memory synthetic Catalog6000; generated SQL fixture utility is separate",
    ["Disk detection"] = machine.Disk
};
var scaleReport = new ScaleTestReport
{
    GeneratedAt = DateTimeOffset.UtcNow,
    Machine = machine,
    Fixture = "6,000 tables; 180,000 columns; 18,000 constraints; 6,000 indexes; 12,000 inventory dependencies",
    Measurements = measurements,
    Infrastructure = infrastructure
};
await ScaleArtifactWriter.WriteAsync(scaleReport, outputDirectory, CancellationToken.None);
foreach (var item in measurements)
{
    Console.WriteLine(
        $"{item.Status,-15} {item.Name,-65} {item.DurationMilliseconds,10:N0} ms  managed {item.PeakManagedMemoryBytes / 1024d / 1024d,8:N1} MiB");
}
Console.WriteLine($"Artifacts: {outputDirectory}");
Environment.ExitCode = measurements.Any(item => item.Status == ScaleTestStatus.Failed) ? 1 : 0;

string? Argument(string name)
{
    var position = Array.FindIndex(args, value =>
        value.Equals($"--{name}", StringComparison.OrdinalIgnoreCase));
    return position >= 0 && position + 1 < args.Length ? args[position + 1] : null;
}

static (string Detail, IReadOnlyDictionary<string, string> Values) Result(
    string detail,
    params (string Name, object Value)[] values) =>
    (detail, values.ToDictionary(
        item => item.Name,
        item => Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty,
        StringComparer.Ordinal));

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static string ComponentHash(IReadOnlyList<DependencyComponent> components)
{
    var canonical = string.Join(
        '\n',
        components.Select(component =>
            $"{component.IsCycle}:{string.Join(',', component.Members.Select(item => item.ToString()))}"));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

static async Task<ScaleMeasurement> CancellationMeasurementAsync(
    string name,
    Func<CancellationToken, Task> operation)
{
    return await MeasurementRunner.RunAsync(
        name,
        "Release gate",
        async _ =>
        {
            using var cancellation = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            var running = operation(cancellation.Token);
            await Task.Delay(20, CancellationToken.None);
            cancellation.Cancel();
            try
            {
                await running.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                throw new InvalidOperationException("Operation completed without observing cancellation.");
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                Require(stopwatch.Elapsed <= TimeSpan.FromSeconds(5), "Cancellation acknowledgement exceeded five seconds.");
                return Result("Cancellation was acknowledged and the operation task completed.",
                    ("LatencyMilliseconds", stopwatch.Elapsed.TotalMilliseconds));
            }
        },
        CancellationToken.None);
}

static ScaleMachine Machine()
{
    var cpu = Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
        "ProcessorNameString", null)?.ToString()?.Trim() ?? "Not detected";
    var memory = GetPhysicalMemory();
    var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\");
    return new ScaleMachine(
        Environment.MachineName, cpu, Environment.ProcessorCount, memory,
        $"{drive.DriveType}; SSD/HDD media type not detectable without privileged storage APIs",
        RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
        RuntimeInformation.ProcessArchitecture.ToString());
}

static XLWorkbook LoadWorkbook(string path)
{
    using var font = File.OpenRead(@"C:\Windows\Fonts\arial.ttf");
    var engine = DefaultGraphicEngine.CreateOnlyWithFonts(font);
    return new XLWorkbook(path, new LoadOptions { GraphicEngine = engine });
}

static string ReadFirstInlineCell(string path)
{
    using var document = SpreadsheetDocument.Open(path, false);
    var worksheet = document.WorkbookPart?.WorksheetParts.SingleOrDefault() ??
        throw new InvalidOperationException("Identifier workbook has no worksheet.");
    using var reader = System.Xml.XmlReader.Create(
        worksheet.GetStream(),
        new System.Xml.XmlReaderSettings { IgnoreWhitespace = true });
    while (reader.Read())
    {
        if (reader.LocalName == "t" && reader.NodeType == System.Xml.XmlNodeType.Element)
        {
            return reader.ReadElementContentAsString();
        }
    }
    return string.Empty;
}

static long GetPhysicalMemory()
{
    var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
    return GlobalMemoryStatusEx(ref status) ? checked((long)status.TotalPhysical) : 0;
}

[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhysical;
    public ulong AvailablePhysical;
    public ulong TotalPageFile;
    public ulong AvailablePageFile;
    public ulong TotalVirtual;
    public ulong AvailableVirtual;
    public ulong AvailableExtendedVirtual;
}

sealed class ScaleApplicationPaths : IApplicationPaths
{
    public ScaleApplicationPaths(string root)
    {
        ApplicationDataDirectory = root;
        LogsDirectory = Path.Combine(root, "Logs");
        PluginsDirectory = Path.Combine(root, "Plugins");
        SettingsFilePath = Path.Combine(root, "settings.json");
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
    }

    public string ApplicationDataDirectory { get; }
    public string LogsDirectory { get; }
    public string PluginsDirectory { get; }
    public string SettingsFilePath { get; }
}
