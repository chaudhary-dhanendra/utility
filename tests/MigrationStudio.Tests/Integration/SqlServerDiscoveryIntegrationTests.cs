using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Infrastructure.Discovery;
using MigrationStudio.Infrastructure.Security;
using MigrationStudio.Infrastructure.SqlServer;

namespace MigrationStudio.Tests.Integration;

public sealed class SqlServerDiscoveryIntegrationTests
{
    [DiscoveryDoctorIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task SqlServer2022_CorrectedCatalogQueriesCompileAndExecute()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_DISCOVERY_DOCTOR_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.CommandText =
                "SELECT CONVERT(int, SERVERPROPERTY('ProductMajorVersion'));";
            var majorVersion = Convert.ToInt32(
                await versionCommand.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
            Assert.Equal(16, majorVersion);
        }

        var queries = new[]
        {
            SqlServerCatalogQueries.Tables(16),
            SqlServerCatalogQueries.ServerTriggers,
            SqlServerCatalogQueries.Advanced(16),
            SqlServerCatalogQueries.ExternalAndPartitioning(16)
        };

        foreach (var query in queries)
        {
            await ExecuteAndDrainAsync(connection, query);
        }
    }

    [DiscoveryDoctorIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task DiscoveryDoctor_DiagnosesConfiguredDatabaseWithoutReadingUserData()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_DISCOVERY_DOCTOR_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        var builder = new SqlConnectionStringBuilder(connectionString);
        var session = new DiscoveryDiagnosticSession(new SensitiveDataRedactor());
        var discovery = new SqlServerInventoryDiscoveryService(
            NullLogger<SqlServerInventoryDiscoveryService>.Instance,
            session);
        var doctor = new SqlServerDiscoveryDoctorService(discovery, session);

        var report = await doctor.DiagnoseAsync(
            ToOptions(builder),
            new DiscoveryDoctorRequest(DiscoveryDoctorMode.FullDiagnostic),
            null,
            CancellationToken.None);

        Assert.All(
            report.Queries.Where(query => query.Descriptor.IsRequired),
            query => Assert.Equal(CatalogDiagnosticStatus.Succeeded, query.Status));
        Assert.Null(report.ProductionFailureStage);
        Assert.False(report.Cancelled);
    }

    [SqlServerIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task DiscoversRepresentativeCatalogFixture_WhenExplicitlyConfigured()
    {
        var connectionString = Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_SQLSERVER_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));

        var databaseName = $"MigrationStudio_{Guid.NewGuid():N}";
        var builder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await using var admin = new SqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await ExecuteAsync(admin, $"CREATE DATABASE [{databaseName}];");
        try
        {
            builder.InitialCatalog = databaseName;
            await using (var fixture = new SqlConnection(builder.ConnectionString))
            {
                await fixture.OpenAsync();
                await ExecuteAsync(
                    fixture,
                    """
                    CREATE SCHEMA sales;
                    CREATE TABLE sales.Customer
                    (
                        CustomerId int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                        Name nvarchar(200) NOT NULL,
                        ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                        ValidTo datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
                        PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
                    ) WITH (SYSTEM_VERSIONING = ON);
                    CREATE VIEW sales.CustomerNames AS SELECT CustomerId, Name FROM sales.Customer;
                    CREATE SEQUENCE sales.OrderNumber AS bigint START WITH 1000;
                    """);
            }

            var options = ToOptions(builder);
            var discovery = new SqlServerInventoryDiscoveryService(
                NullLogger<SqlServerInventoryDiscoveryService>.Instance);
            var snapshot = await discovery.DiscoverAsync(
                new InventoryDiscoveryRequest(
                    options,
                    MigrationScopeMode.CompleteDatabase,
                    new HashSet<string>(),
                    new HashSet<InventoryObjectId>(),
                    new HashSet<InventoryObjectId>(),
                    DependencyPolicy.IncludeRequiredDependencies,
                    new DiscoveryOptions()),
                null,
                CancellationToken.None);

            Assert.Contains(snapshot.Objects, item => item.QualifiedSourceName == "[sales].[Customer]");
            Assert.Contains(snapshot.Objects, item => item.ObjectType == InventoryObjectType.View);
            Assert.Contains(
                snapshot.Sequences,
                sequence => snapshot.Objects.Any(item =>
                    item.Id == sequence.ObjectId && item.SourceName == "OrderNumber"));
            Assert.NotEmpty(snapshot.Dependencies);
        }
        finally
        {
            builder.InitialCatalog = "master";
            await ExecuteAsync(admin, $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}];");
        }
    }

    private static SqlServerConnectionOptions ToOptions(SqlConnectionStringBuilder builder) =>
        new()
        {
            Server = builder.DataSource,
            Database = builder.InitialCatalog,
            AuthenticationMode = builder.IntegratedSecurity
                ? SqlServerAuthenticationMode.Windows
                : SqlServerAuthenticationMode.SqlServer,
            Username = builder.UserID,
            Password = builder.Password,
            Encrypt = bool.TryParse(builder.Encrypt.ToString(), out var encrypt) && encrypt,
            TrustServerCertificate = builder.TrustServerCertificate,
            ConnectionTimeoutSeconds = builder.ConnectTimeout
        };

    private static async Task ExecuteAsync(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAndDrainAsync(
        SqlConnection connection,
        string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.CommandTimeout = 120;
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            while (await reader.ReadAsync())
            {
            }
        }
        while (await reader.NextResultAsync());
    }
}

internal sealed class SqlServerIntegrationFactAttribute : FactAttribute
{
    public SqlServerIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_SQLSERVER_INTEGRATION")))
        {
            Skip = "Set MIGRATIONSTUDIO_SQLSERVER_INTEGRATION to run the SQL Server catalog fixture.";
        }
    }
}

internal sealed class DiscoveryDoctorIntegrationFactAttribute : FactAttribute
{
    public DiscoveryDoctorIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("MIGRATIONSTUDIO_DISCOVERY_DOCTOR_CONNECTION")))
        {
            Skip = "Set MIGRATIONSTUDIO_DISCOVERY_DOCTOR_CONNECTION to audit an existing database read-only.";
        }
    }
}
