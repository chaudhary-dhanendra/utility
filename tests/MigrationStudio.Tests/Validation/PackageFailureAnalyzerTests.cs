using System.IO;
using System.Text.Json;
using MigrationStudio.Validation;

namespace MigrationStudio.Tests.Validation;

public sealed class PackageFailureAnalyzerTests
{
    [Fact]
    public void ResidualScanner_DetectsRequiredSqlServerSyntaxButIgnoresCommentsAndLiterals()
    {
        const string sql = """
            -- PRINT @ignored; GETDATE()
            SELECT '@ignored PRINT GETDATE()';
            DECLARE @t TABLE(id int); SET @x = @@ROWCOUNT; SET NOCOUNT ON;
            PRINT @x; RAISERROR('x', 16, 1); BEGIN TRY SELECT TOP 1 * FROM db.dbo.t WITH (NOLOCK); END TRY;
            BEGIN CATCH EXEC(@sql); EXEC sp_executesql @sql; END CATCH;
            SELECT SCOPE_IDENTITY(), IDENT_CURRENT('t'), GETDATE(), DATEADD(day,1,GETDATE()),
                   DATEDIFF(day,GETDATE(),GETDATE()), DATEPART(day,GETDATE()), DATENAME(day,GETDATE()),
                   ISNULL(x,0), IIF(x=1,1,0), TRY_CAST(x AS int), TRY_CONVERT(int,x) FROM #temp;
            SELECT * FROM p PIVOT(max(x) FOR y IN ([a])) q FOR XML PATH;
            SELECT * FROM p UNPIVOT(x FOR y IN (a)) q FOR JSON PATH OPTION(RECOMPILE);
            MERGE INTO t USING s ON t.id=s.id WHEN NOT MATCHED BY TARGET THEN INSERT(id) VALUES(s.id);
            OUTPUT INSERTED.id;
            """;

        var findings = ResidualSqlServerSyntaxScanner.Scan(sql);
        var constructs = findings.Select(item => item.Construct).ToHashSet(StringComparer.Ordinal);

        Assert.True(constructs.Count >= 30);
        Assert.Contains("DECLARE @", constructs);
        Assert.Contains("table-variable declaration", constructs);
        Assert.Contains("SQL Server MERGE", constructs);
        Assert.Contains("TOP clause", constructs);
        Assert.DoesNotContain(findings, item => item.Offset < sql.IndexOf("DECLARE", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_SeparatesRootFailuresFromBlockedDependentsAndWritesAllReports()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Validation", "Fixtures", "package-analysis-regression.json");
        var output = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var report = PackageFailureAnalyzer.Analyze(new PackageAnalysisOptions(fixture, output, 10, 2));

            Assert.Equal(13, report.Counts.Total);
            Assert.Equal(10, report.Counts.Failed);
            Assert.Equal(2, report.Counts.DependencyBlocked);
            Assert.Equal(10, report.Counts.RootFailures);
            Assert.Equal(2, report.Counts.CascadingDependencyFailures);
            Assert.Contains(report.RootCauseGroups, group => group.BlockedDependentCount == 2);
            Assert.Contains(report.RepeatedGeneratedSqlPatterns.Keys, item => item.Contains("SET NOCOUNT", StringComparison.Ordinal));
            foreach (var name in RequiredReports)
            {
                Assert.True(File.Exists(Path.Combine(output, name)), name);
            }
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "failure-baseline.json")));
            Assert.Equal(10, json.RootElement.GetProperty("counts").GetProperty("failed").GetInt32());
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void Analyzer_ReadsDeploymentPackageManifestArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "manifest.json");
        var output = Path.Combine(root, "reports");
        try
        {
            File.WriteAllText(
                input,
                """
                {
                  "Artifacts": [{
                    "SourceObjectId": { "Value": "11111111-1111-1111-1111-111111111111" },
                    "TargetObjectType": "Table",
                    "TargetSchema": "public",
                    "TargetName": "sample",
                    "Phase": 5,
                    "Sql": "CREATE TABLE public.sample(id integer);",
                    "RequiresManualReview": false,
                    "Dependencies": [],
                    "LiveValidation": {
                      "Outcome": 1,
                      "SqlState": null,
                      "Message": null,
                      "BlockingDependencies": []
                    }
                  }]
                }
                """);

            var report = PackageFailureAnalyzer.Analyze(
                new PackageAnalysisOptions(input, output));

            Assert.Equal(1, report.Counts.Total);
            Assert.Equal(1, report.Counts.Passed);
            var artifact = Assert.Single(report.Artifacts);
            Assert.Equal("public.sample", artifact.TargetObject);
            Assert.Equal("Table", artifact.ObjectType);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static readonly string[] RequiredReports =
    [
        "failure-baseline.json",
        "failure-baseline.csv",
        "failure-baseline.md",
        "regression-delta.md",
        "conversion-architecture.md"
    ];
}
