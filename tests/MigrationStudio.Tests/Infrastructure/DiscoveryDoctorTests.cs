using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Infrastructure.SqlServer;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class DiscoveryDoctorTests
{
    [Fact]
    public void Catalog_UsesExactProductionMetadataQueriesAndPolicies()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);
        var catalog = service.GetCatalog(16);

        Assert.Equal(18, catalog.Count);
        Assert.All(catalog, item => Assert.True(item.IsMetadataOnly));
        Assert.Contains(catalog, item =>
            item.QueryId == "SQLSERVER.OBJECTS.V16" &&
            item.IsRequired &&
            item.Stage == DiscoveryStage.DiscoveringObjects &&
            item.QueryText.Contains("FROM sys.objects", StringComparison.Ordinal));
        Assert.Contains(catalog, item =>
            item.QueryId == "SQLSERVER.SQL_AGENT.V1" &&
            !item.IsRequired &&
            item.QueryText.Contains("msdb.dbo.sysjobs", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_SelectsNonEmptyQuickAndFullDiagnosticsForSql2022Compatibility100()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);

        var quick = service.SelectCatalog(
            16,
            new DiscoveryDoctorRequest(DiscoveryDoctorMode.QuickPreflight));
        var full = service.SelectCatalog(
            16,
            new DiscoveryDoctorRequest(DiscoveryDoctorMode.FullDiagnostic));

        Assert.Equal(10, quick.Count);
        Assert.Equal(18, full.Count);
        Assert.Contains(quick, item => item.QueryId == "SQLSERVER.OBJECTS.V16");
        Assert.Contains(quick, item => item.QueryId == "SQLSERVER.TABLES.V16");
        Assert.Contains(quick, item => item.QueryId == "SQLSERVER.COLUMNS.V16");
        Assert.Contains(full, item =>
            item.QueryId == "SQLSERVER.FULL_TEXT.V1" &&
            item.RequiredCapability == "Full Text installed");
        Assert.All(full, item => Assert.False(string.IsNullOrWhiteSpace(item.QueryText)));
    }

    [Fact]
    public void SelectedMode_RejectsAnEmptySelection()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            service.SelectCatalog(
                16,
                new DiscoveryDoctorRequest(
                    DiscoveryDoctorMode.SelectedQueries,
                    new HashSet<string>())));

        Assert.Contains("at least one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InfrastructureDi_ResolvesDiscoveryDoctorAndRegisteredQueries()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMigrationStudioInfrastructure(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var doctor = provider.GetRequiredService<IDiscoveryDoctorService>();

        Assert.NotEmpty(doctor.GetCatalog(16));
    }

    [Fact]
    public void ProductionMapperFailure_UpdatesQueryAndProductionFailurePhase()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);
        var descriptor = service.GetCatalog(16).Single(item =>
            item.QueryId == "SQLSERVER.OBJECTS.V16");
        var query = new CatalogQueryDiagnostic(
            descriptor,
            CatalogDiagnosticStatus.Succeeded,
            1,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            5,
            1,
            12,
            0,
            [],
            null,
            null,
            [],
            "Raw SQL passed.",
            string.Empty,
            true);
        var results = new List<CatalogQueryDiagnostic> { query };
        var exception = new SourceDatabaseException(
            "Object mapping failed.",
            [],
            new InvalidOperationException("Invalid ordinal access."),
            DiscoveryStage.DiscoveringObjects,
            descriptor.QueryId,
            Guid.NewGuid(),
            false,
            "Correct the mapper.");

        SqlServerDiscoveryDoctorService.ApplyProductionFailure(results, exception, null);

        Assert.Equal(CatalogDiagnosticStatus.Failed, results[0].Status);
        Assert.Equal(CatalogFailurePhase.MetadataMapping, results[0].FailurePhase);
        Assert.Equal("InvalidOperationException", results[0].ExceptionType);
        Assert.Contains("Invalid ordinal", results[0].Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedFullText_IsRecordedAsSkippedUnsupported()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);
        var descriptor = service.GetCatalog(16).Single(item =>
            item.QueryId == "SQLSERVER.FULL_TEXT.V1");

        var result = SqlServerDiscoveryDoctorService.CreateSkippedUnsupported(descriptor);

        Assert.Equal(CatalogDiagnosticStatus.Skipped, result.Status);
        Assert.Equal(CatalogFailurePhase.QuerySelection, result.FailurePhase);
        Assert.Contains("SkippedUnsupported", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_RejectsVersionsBelowSupportedFloor()
    {
        var service = new SqlServerDiscoveryDoctorService(null!, null!);

        Assert.Empty(service.GetCatalog(12));
    }

    [Fact]
    public async Task DoctorExport_OmitsSqlAndSanitizesSourceAndErrors()
    {
        var session = new DiscoveryDiagnosticSession(new SensitiveDataRedactor());
        var descriptor = new CatalogQueryDescriptor(
            "SQLSERVER.OBJECTS.V16",
            DiscoveryStage.DiscoveringObjects,
            true,
            13,
            "Objects",
            "SELECT SECRET_QUERY_TEXT FROM sys.objects",
            true);
        session.PublishDoctor(new DiscoveryDoctorReport(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "private-server",
            "vbgramg",
            new DatabaseCompatibilityAudit(
                "16.0.1000.6",
                16,
                "RTM",
                "Enterprise",
                3,
                160,
                [],
                []),
            [
                new CatalogQueryDiagnostic(
                    descriptor,
                    CatalogDiagnosticStatus.Failed,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    5,
                    0,
                    0,
                    0,
                    [new SqlServerError(229, 14, 1, "Password=top-secret", null, 1)],
                    "SqlException",
                    CatalogFailurePhase.QueryExecution,
                    [],
                    "pwd=top-secret",
                    "Grant VIEW DEFINITION.",
                    false)
            ],
            18,
            10,
            10,
            DiscoveryStage.DiscoveringObjects,
            descriptor.QueryId,
            "token=top-secret",
            false));
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.doctor.json");

        try
        {
            await session.ExportDoctorAsync(path, CancellationToken.None);
            var json = await File.ReadAllTextAsync(path);

            Assert.DoesNotContain("private-server", json, StringComparison.Ordinal);
            Assert.DoesNotContain("vbgramg", json, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET_QUERY_TEXT", json, StringComparison.Ordinal);
            Assert.Contains("SQLSERVER.OBJECTS.V16", json, StringComparison.Ordinal);
            Assert.Contains("\"RegisteredQueryCount\": 18", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Queries\": []", json, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
