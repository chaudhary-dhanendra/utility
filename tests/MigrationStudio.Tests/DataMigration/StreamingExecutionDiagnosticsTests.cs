using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Infrastructure.DataMigration;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Reporting;

namespace MigrationStudio.Tests.DataMigration;

public sealed class StreamingExecutionDiagnosticsTests
{
    [Fact]
    public void Observer_captures_sanitized_failure_and_actionable_stage()
    {
        var observer = new StreamingStageObserver(
            Guid.NewGuid(),
            null,
            new SensitiveDataRedactor(),
            NullLogger.Instance);
        var id = observer.Start(
            StreamingExecutionStage.ResolvePostgreSqlTable,
            postgreSqlQuery: "SELECT EXISTS (SELECT 1 FROM \"public\".\"missing_table\")");

        observer.Fail(
            id,
            new InvalidOperationException("Target failed; Password=do-not-export"));

        var stage = Assert.Single(observer.Snapshot());
        Assert.Equal(StreamingStageOutcome.Failed, stage.Outcome);
        Assert.Equal(StreamingExecutionStage.ResolvePostgreSqlTable, stage.Stage);
        Assert.Equal("PostgreSQL target resolver", stage.FailureComponent);
        Assert.Contains("Password=***", stage.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-export", stage.FailureReason, StringComparison.Ordinal);
        Assert.NotNull(stage.CompletedAt);
        Assert.True(stage.ElapsedMilliseconds >= 0);
    }

    [Fact]
    public async Task Report_exports_every_streaming_stage_without_credentials()
    {
        var root = Path.Combine(Path.GetTempPath(), $"MigrationStudio-Streaming-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = new DataMigrationResult(
                Guid.NewGuid(),
                MigrationRunState.Failed,
                DateTimeOffset.UtcNow.AddSeconds(-1),
                DateTimeOffset.UtcNow,
                [],
                [],
                [],
                [],
                Path.Combine(root, "checkpoint.json"),
                1,
                0,
                0,
                []) with
            {
                StreamingStages =
                [
                    new StreamingStageDiagnostic(
                        Guid.NewGuid(),
                        StreamingExecutionStage.ResolvePostgreSqlTable,
                        StreamingStageOutcome.Failed,
                        DateTimeOffset.UtcNow.AddMilliseconds(-5),
                        DateTimeOffset.UtcNow,
                        5,
                        "dbo",
                        "[dbo].[source]",
                        "public.target",
                        0,
                        0,
                        0,
                        null,
                        "Npgsql target preparation command",
                        null,
                        "SELECT EXISTS (SELECT 1 FROM \"public\".\"target\")",
                        null,
                        null,
                        "42P01",
                        "PostgreSQL target resolver",
                        "relation does not exist",
                        "Deploy the target table.",
                        "Npgsql.PostgresException",
                        null,
                        "sanitized stack")
                ]
            };

            await new DataMigrationReportWriter().WriteAsync(result, root, CancellationToken.None);

            var html = await File.ReadAllTextAsync(Path.Combine(root, "Data_Migration_Report.html"));
            Assert.Contains("ResolvePostgreSqlTable", html, StringComparison.Ordinal);
            Assert.Contains("42P01", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Password=", html, StringComparison.OrdinalIgnoreCase);
            using var workbook = System.IO.Compression.ZipFile.OpenRead(
                Path.Combine(root, "Data_Migration_Report.xlsx"));
            var workbookEntry = Assert.Single(
                workbook.Entries,
                entry => entry.FullName.Equals("xl/workbook.xml", StringComparison.Ordinal));
            using var reader = new StreamReader(workbookEntry.Open());
            var workbookXml = await reader.ReadToEndAsync();
            Assert.Contains("Streaming execution", workbookXml, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
