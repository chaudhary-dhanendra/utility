using System.IO;
using System.Text.Json;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.Security;

namespace MigrationStudio.Tests.Infrastructure;

public sealed class DiscoveryDiagnosticSessionTests
{
    [Fact]
    public async Task Export_PseudonymizesSourceAndRedactsCredentials()
    {
        var session = new DiscoveryDiagnosticSession(new SensitiveDataRedactor());
        var correlationId = Guid.NewGuid();
        session.Publish(new DiscoveryDiagnosticReport(
            correlationId,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "private-server",
            "customer-database",
            "16",
            DiscoveryStage.DiscoveringObjects,
            DiscoveryStageState.Failed,
            [
                new DiscoveryStageDiagnostic(
                    DiscoveryStage.DiscoveringObjects,
                    DiscoveryStageState.Failed,
                    "SQLSERVER.OBJECTS.V16",
                    true,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    10,
                    0,
                    [new SqlServerError(229, 14, 1, "Password=top-secret", null, 1)],
                    "pwd=top-secret",
                    false)
            ],
            "token=top-secret",
            true));
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.discovery.json");

        try
        {
            await session.ExportAsync(path, CancellationToken.None);
            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.StartsWith("server-", root.GetProperty("Server").GetString(), StringComparison.Ordinal);
            Assert.StartsWith("database-", root.GetProperty("Database").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("private-server", json, StringComparison.Ordinal);
            Assert.DoesNotContain("customer-database", json, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
            Assert.Contains("SQLSERVER.OBJECTS.V16", json, StringComparison.Ordinal);
            Assert.Contains(correlationId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
