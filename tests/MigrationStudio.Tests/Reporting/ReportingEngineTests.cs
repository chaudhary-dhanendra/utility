using System.IO;
using System.Text.Json;
using ClosedXML.Excel;
using ClosedXML.Graphics;
using MigrationStudio.Application.Platform;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Deployment;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Reporting;
using MigrationStudio.Domain.Validation;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Reporting;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Reporting;

public sealed class ReportingEngineTests
{
    [Fact]
    public async Task GeneratesConsistentSanitizedReportPackage()
    {
        using var workspace = new TemporaryWorkspace();
        using var history = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(),
            new SensitiveDataRedactor(),
            history);

        var result = await engine.GenerateAsync(
            ReportingFixture.CreateRequest(),
            workspace.Root,
            null,
            CancellationToken.None);

        Assert.Equal(11, result.Files.Count);
        Assert.All(result.Files, path => Assert.True(File.Exists(path)));
        Assert.Equal(
            [
                "DataReconciliation.csv",
                "DeploymentFailures.csv",
                "IdentifierMapping.csv",
                "ManualReview.csv",
                "MigrationExecutiveSummary.html",
                "MigrationExecutiveSummary.pdf",
                "MigrationReport.json",
                "MigrationReport.xlsx",
                "ObjectInventory.csv",
                "ObjectReconciliation.csv",
                "UnsupportedFeatures.csv"
            ],
            result.Files.Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray());

        var allText = string.Join(
            Environment.NewLine,
            result.Files.Where(path => Path.GetExtension(path) is ".html" or ".json" or ".csv")
                .Select(File.ReadAllText));
        Assert.DoesNotContain("super-secret", allText, StringComparison.Ordinal);
        Assert.Contains("***", allText, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(result.ReportsDirectory, "MigrationReport.json")));
        Assert.Equal(
            MigrationReportDocument.CurrentSchemaVersion,
            json.RootElement.GetProperty("reportSchemaVersion").GetInt32());
        Assert.Equal(
            ReportingFixture.ReportIdSourceDatabase,
            json.RootElement.GetProperty("summary").GetProperty("source").GetProperty("database").GetString());
        Assert.True(json.RootElement.TryGetProperty("reconciliationSummary", out var reconciliation));
        Assert.Equal(
            reconciliation.GetProperty("selectedSourceObjects").GetInt32(),
            reconciliation.GetProperty("reconciledTotal").GetInt32() +
            reconciliation.GetProperty("unreconciled").GetInt32());

        using var workbook = LoadWorkbook(Path.Combine(result.ReportsDirectory, "MigrationReport.xlsx"));
        Assert.True(workbook.Worksheets.Count >= 39);
        Assert.Contains("Executive Summary", workbook.Worksheets.Select(item => item.Name));
        Assert.Contains("Readiness Score", workbook.Worksheets.Select(item => item.Name));
        Assert.True(workbook.Worksheet("Conversion Findings").ConditionalFormats.Any());

        var pdf = await File.ReadAllBytesAsync(
            Path.Combine(result.ReportsDirectory, "MigrationExecutiveSummary.pdf"));
        Assert.True(pdf.Length > 2_000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));

        var html = await File.ReadAllTextAsync(
            Path.Combine(result.ReportsDirectory, "MigrationExecutiveSummary.html"));
        Assert.Contains("&lt;source&amp;server&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<source&server>", html, StringComparison.Ordinal);
        Assert.Contains("filterTable", html, StringComparison.Ordinal);
        Assert.Contains("Validation scorecards", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesPackageDirectlyInRequestedDirectory()
    {
        using var workspace = new TemporaryWorkspace();
        using var history = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(),
            new SensitiveDataRedactor(),
            history);
        var reportsDirectory = Path.Combine(workspace.Root, "per-user", "migration-run");
        Directory.CreateDirectory(reportsDirectory);
        var unrelatedFile = Path.Combine(reportsDirectory, "user-notes.txt");
        await File.WriteAllTextAsync(unrelatedFile, "retain me");

        var result = await engine.GenerateToDirectoryAsync(
            ReportingFixture.CreateRequest(),
            reportsDirectory,
            null,
            CancellationToken.None);

        Assert.Equal(reportsDirectory, result.ReportsDirectory);
        Assert.Equal(11, result.Files.Count);
        Assert.All(result.Files, path => Assert.Equal(reportsDirectory, Path.GetDirectoryName(path)));
        Assert.False(Directory.Exists(Path.Combine(reportsDirectory, "Reports")));
        Assert.Equal("retain me", await File.ReadAllTextAsync(unrelatedFile));
    }

    [Fact]
    public void WorkbookSanitizesNamesAndContinuesAtConfiguredLimit()
    {
        using var workspace = new TemporaryWorkspace();
        var request = ReportingFixture.CreateRequest(objectCount: 9);
        var report = MigrationReportDocumentBuilder.Build(request, Guid.NewGuid());
        var path = Path.Combine(workspace.Root, "continued.xlsx");

        new MigrationExcelReportWriter(maximumRowsPerSheet: 5)
            .Write(report, path, CancellationToken.None);

        using var workbook = LoadWorkbook(path);
        Assert.Contains("Object Inventory", workbook.Worksheets.Select(item => item.Name));
        Assert.Contains("Object Inventory 2", workbook.Worksheets.Select(item => item.Name));
        Assert.Contains("Object Inventory 3", workbook.Worksheets.Select(item => item.Name));
        Assert.All(workbook.Worksheets, sheet =>
        {
            Assert.True(sheet.Name.Length <= 31);
            Assert.DoesNotContain(sheet.Name, character => ":\\/?*[]".Contains(character));
        });
        Assert.Equal("InvalidWorksheetName", MigrationExcelReportWriter.SanitizeWorksheetName(
            "Invalid:/Worksheet?Name*[]"));
    }

    [Fact]
    public async Task EmptyOptionalRunsStillProduceAllFormats()
    {
        using var workspace = new TemporaryWorkspace();
        using var history = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var request = ReportingFixture.CreateRequest() with
        {
            Conversion = null,
            DataMigration = null,
            Deployment = null,
            Validation = null,
            ManualReviews = []
        };
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(), new SensitiveDataRedactor(), history);

        var result = await engine.GenerateAsync(
            request, workspace.Root, null, CancellationToken.None);

        Assert.Equal(11, result.Files.Count);
        using var workbook = LoadWorkbook(Path.Combine(result.ReportsDirectory, "MigrationReport.xlsx"));
        Assert.True(workbook.TryGetWorksheet("Deployment Journal", out _));
        Assert.True(workbook.TryGetWorksheet("Validation Summary", out _));
    }

    [Fact]
    public async Task ValidatedLogoIsEmbeddedInOfflineHtmlAndPdf()
    {
        using var workspace = new TemporaryWorkspace();
        var logoPath = Path.Combine(workspace.Root, "logo.png");
        await File.WriteAllBytesAsync(
            logoPath,
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        using var history = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(), new SensitiveDataRedactor(), history);
        var request = ReportingFixture.CreateRequest() with
        {
            Template = ReportingFixture.CreateRequest().Template with { LogoPath = logoPath }
        };

        var result = await engine.GenerateAsync(
            request, workspace.Root, null, CancellationToken.None);
        var html = await File.ReadAllTextAsync(
            Path.Combine(result.ReportsDirectory, "MigrationExecutiveSummary.html"));

        Assert.Contains("data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.True(new FileInfo(
            Path.Combine(result.ReportsDirectory, "MigrationExecutiveSummary.pdf")).Length > 2_000);
    }

    [Fact]
    public async Task ManualReviewWorkflowEnforcesResolutionAndReopen()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonManualReviewStore(workspace.Paths);
        var item = ReportingFixture.CreateRequest().ManualReviews.Single();

        await store.SaveAsync(item, CancellationToken.None);
        var inProgress = item with { Status = ManualReviewStatus.InProgress, Owner = "DBA" };
        await store.SaveAsync(inProgress, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveAsync(inProgress with { Status = ManualReviewStatus.Resolved }, CancellationToken.None));
        await store.SaveAsync(
            inProgress with
            {
                Status = ManualReviewStatus.Resolved,
                Resolution = "Replaced with a supported implementation.",
                ReviewedBy = "Reviewer"
            },
            CancellationToken.None);
        await store.ReopenAsync(item.Id, "Regression found.", CancellationToken.None);

        var reopened = Assert.Single(await store.LoadAsync(CancellationToken.None));
        Assert.Equal(ManualReviewStatus.Open, reopened.Status);
        Assert.Null(reopened.Resolution);
        Assert.Contains("Regression found", reopened.Comments, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHistoryPersistsPayloadWithoutPasswords()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var id = Guid.NewGuid();
        using var payload = JsonDocument.Parse("""{"connection":"Host=x;Password=super-secret","count":3}""");
        var entry = new RunHistoryEntry(
            id, RunHistoryKind.Validation, RunHistoryStatus.Succeeded,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "source", "target", "done", string.Empty);

        await store.SaveAsync(
            new RunHistoryRecord(entry, payload.RootElement.Clone()), CancellationToken.None);

        var loaded = await store.LoadAsync(id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Single(await store.ListAsync(CancellationToken.None));
        Assert.DoesNotContain("super-secret", loaded!.Payload.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("***", loaded.Payload.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoricalReportCanBeRegeneratedWithoutCurrentSessions()
    {
        using var workspace = new TemporaryWorkspace();
        using var store = new JsonRunHistoryStore(workspace.Paths, new SensitiveDataRedactor());
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(), new SensitiveDataRedactor(), store);
        var original = await engine.GenerateAsync(
            ReportingFixture.CreateRequest(), workspace.Root, null, CancellationToken.None);
        var regeneratedRoot = Path.Combine(workspace.Root, "regenerated");

        var regenerated = await engine.RegenerateAsync(
            original.ReportRunId, regeneratedRoot, null, CancellationToken.None);

        Assert.NotEqual(original.ReportRunId, regenerated.ReportRunId);
        Assert.Equal(11, regenerated.Files.Count);
        Assert.All(regenerated.Files, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task SanitizedLogExportRetainsCorrelationAndStackTrace()
    {
        using var workspace = new TemporaryWorkspace();
        var correlationId = Guid.NewGuid();
        var log = $$"""
                    {"correlationId":"{{correlationId}}","connection":"Server=db;Password=super-secret","exception":"System.InvalidOperationException\\n   at Example.Run()"}
                    """;
        await File.WriteAllTextAsync(Path.Combine(workspace.Paths.LogsDirectory, "app.jsonl"), log);
        var exporter = new SanitizedLogExporter(workspace.Paths, new SensitiveDataRedactor());

        var path = await exporter.ExportAsync(
            workspace.Root, new HashSet<Guid> { correlationId }, CancellationToken.None);
        var exported = await File.ReadAllTextAsync(path);

        Assert.Contains(correlationId.ToString(), exported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Example.Run", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", exported, StringComparison.Ordinal);
        Assert.Contains("***", exported, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratesSanitizedSamplePackageWhenExplicitlyRequested()
    {
        var output = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_SAMPLE_REPORT_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }
        var stateRoot = Path.Combine(
            Path.GetTempPath(), $"MigrationStudio-Sample-State-{Guid.NewGuid():N}");
        var paths = new TestApplicationPaths(stateRoot);
        Directory.CreateDirectory(paths.ApplicationDataDirectory);
        Directory.CreateDirectory(paths.LogsDirectory);
        using var history = new JsonRunHistoryStore(paths, new SensitiveDataRedactor());
        var engine = new MigrationReportEngine(
            new ReportTemplateValidator(), new SensitiveDataRedactor(), history);

        var result = await engine.GenerateAsync(
            ReportingFixture.CreateRequest(), output, null, CancellationToken.None);

        Assert.Equal(11, result.Files.Count);
    }

    private static XLWorkbook LoadWorkbook(string path)
    {
        using var font = File.OpenRead(@"C:\Windows\Fonts\arial.ttf");
        var engine = DefaultGraphicEngine.CreateOnlyWithFonts(font);
        return new XLWorkbook(path, new LoadOptions { GraphicEngine = engine });
    }
}

internal static class ReportingFixture
{
    public const string ReportIdSourceDatabase = "SanitizedSource";

    public static MigrationReportRequest CreateRequest(int objectCount = 1)
    {
        var objects = Enumerable.Range(1, objectCount).Select(index =>
        {
            var id = InventoryObjectId.Create(
                ReportIdSourceDatabase, InventoryObjectType.Table, "dbo", $"Table{index}", index);
            return new InventoryObject(
                id, ReportIdSourceDatabase, "dbo", $"Table{index}", $"[dbo].[Table{index}]",
                InventoryObjectType.Table, index, null, null, null, false, true,
                SelectionReason.CompleteDatabase, 0, 0, [], ConversionClassification.Automatic,
                index == 1 ? "CREATE TABLE dbo.Table1(Id int, password='super-secret')" : null,
                null, $"hash-{index}", [], DiscoveryStatus.Discovered);
        }).ToArray();
        var snapshot = TestInventory.CreateSnapshot(objects) with
        {
            Database = TestInventory.CreateSnapshot(objects).Database with
            {
                DatabaseName = ReportIdSourceDatabase
            },
            Findings =
            [
                new InventoryFinding(
                    "REPORT.TEST", FindingSeverity.Warning, "Representative warning.", objects[0].Id)
            ]
        };
        var target = new TargetObjectIdentifier("Table", "public", "table1");
        var artifact = new ConversionArtifact(
            objects[0].Id, target,
            "CREATE TABLE dbo.Table1(Id int, password='super-secret')",
            "CREATE TABLE public.table1(id integer);",
            ConversionClassification.AutomaticWithWarning, "REPORT.TEST", 0.9m,
            snapshot.Findings, [], [], [], true, ["Fixture"],
            new SqlValidationResult(true, false, null, null, null),
            DeploymentPhase.Tables, "05_Tables.sql", "artifact-hash");
        var mapping = new IdentifierMappingEntry(
            objects[0].Id, "Table", "dbo", "Table1", "[dbo].[Table1]",
            "public", "table1", "public.table1", 6, 6, false, false, null, "lowercase");
        var conversion = new ConversionRun(
            Guid.NewGuid(), DateTimeOffset.UtcNow, ReportIdSourceDatabase,
            new PostgreSqlVersion(18), new ConversionOptions(), [mapping], [], [artifact],
            snapshot.Findings, ["pgcrypto"], "test");
        var now = DateTimeOffset.UtcNow;
        var data = new DataMigrationResult(
            Guid.NewGuid(), MigrationRunState.CompletedWithFailures, now.AddMinutes(-2), now,
            [
                new TableMigrationMetrics(
                    objects[0].Id, "dbo.Table1", TableMigrationState.CompletedWithFailures,
                    10, 9, 1, 1024, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(2), 0, 1, 1,
                    1_000_000, 4.5, 512, "Sanitized fixture")
            ],
            [
                new MigrationFailure(
                    "dbo.Table1", 1, 10, "key-hash", "password", "nvarchar", "text",
                    "22000", "Sensitive value rejected and redacted.", 0,
                    FailureCategory.Conversion, FailureDisposition.RowSkipped)
            ],
            [], [], "checkpoint.json", 1, 1, 1, []);
        var deployment = new DeploymentResult(
            Guid.NewGuid(), DeploymentRunStatus.SucceededWithWarnings, now.AddMinutes(-1), now,
            "SanitizedTarget", "journal.json",
            [
                new DeploymentObjectJournal(
                    objects[0].Id, "public.table1", DeploymentPhase.Tables, "05_Tables.sql",
                    "sql-hash", DeploymentObjectStatus.Succeeded, CommitStatus.Committed,
                    now.AddSeconds(-30), now, [], [], null, true, "Created")
            ],
            [], data.RunId, []);
        var finding = new ValidationFinding(
            "DATA.ROW_COUNT", ValidationCategory.DataReconciliation, ValidationSeverity.Critical,
            ComparisonClassification.Mismatch, "Table", "dbo.Table1", "public.table1",
            "Row counts differ; values were not retained.");
        var readiness = ReadinessCalculator.Calculate(
            [finding], new ValidationConfiguration { Level = ValidationLevel.Full });
        var validation = new ValidationRun
        {
            RunId = Guid.NewGuid(),
            MigrationRunId = data.RunId,
            DeploymentRunId = deployment.DeploymentId,
            SourceSnapshotHash = "snapshot-hash",
            TargetDatabaseIdentity = "SanitizedTarget@localhost",
            Configuration = new ValidationConfiguration { Level = ValidationLevel.Full },
            StartedAt = now.AddSeconds(-20),
            CompletedAt = now,
            Findings = [finding],
            Readiness = readiness,
            DataComparisons =
            [
                new TableDataComparison(
                    "dbo.Table1", "public.table1", 10, 9, "source-hash", "target-hash",
                    true, [], [], ComparisonClassification.Mismatch, "Counts differ.")
            ]
        };
        var review = new ManualReviewItem
        {
            Id = Guid.NewGuid(),
            FindingKey = "conversion:artifact-hash",
            Source = "public.table1",
            Title = "Review representative conversion",
            Description = "Fixture manual review.",
            Status = ManualReviewStatus.Open,
            TargetSql = "ALTER ROLE app PASSWORD='super-secret';",
            IsCriticalBlocker = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        return new MigrationReportRequest
        {
            Inventory = snapshot,
            Conversion = conversion,
            DataMigration = data,
            Deployment = deployment,
            Validation = validation,
            Source = new MigrationEndpointSummary(
                "<source&server>", ReportIdSourceDatabase, "16.0", "Developer"),
            Target = new MigrationEndpointSummary(
                "postgres.local", "SanitizedTarget", "18", "PostgreSQL"),
            Template = new ReportTemplate
            {
                OrganizationName = "Example Organization",
                ProjectName = "Sanitized Migration",
                PreparedBy = "Migration Team",
                ReviewedBy = "Database Owner"
            },
            ManualReviews = [review],
            ApplicationVersion = "1.0.0-test"
        };
    }
}

internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-Reporting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Paths = new TestApplicationPaths(Path.Combine(Root, "state"));
        Directory.CreateDirectory(Paths.ApplicationDataDirectory);
        Directory.CreateDirectory(Paths.LogsDirectory);
    }

    public string Root { get; }

    public TestApplicationPaths Paths { get; }

    public void Dispose() => Directory.Delete(Root, true);
}

internal sealed class TestApplicationPaths(string root) : IApplicationPaths
{
    public string ApplicationDataDirectory { get; } = root;

    public string LogsDirectory { get; } = Path.Combine(root, "logs");

    public string PluginsDirectory { get; } = Path.Combine(root, "plugins");

    public string SettingsFilePath { get; } = Path.Combine(root, "settings.json");
}
