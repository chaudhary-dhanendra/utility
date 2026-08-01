using Microsoft.Extensions.Logging.Abstractions;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Infrastructure.Security;
using Npgsql;

namespace MigrationStudio.Tests.Integration;

public sealed class PostgreSqlConnectionIntegrationTests
{
    [PostgreSqlIntegrationFact]
    [Trait("Category", "Integration")]
    public async Task TestConnection_ReachesPostgreSqlWithSuppliedPassword()
    {
        var configured = Environment.GetEnvironmentVariable(
            "MIGRATIONSTUDIO_POSTGRES_INTEGRATION");
        Assert.False(string.IsNullOrWhiteSpace(configured));
        var source = new NpgsqlConnectionStringBuilder(configured);
        Assert.False(string.IsNullOrEmpty(source.Password));

        var viewModel = new PostgreSqlConnectionViewModel(
            new SensitiveDataRedactor(),
            NullLogger<PostgreSqlConnectionViewModel>.Instance)
        {
            Host = source.Host ?? string.Empty,
            Port = source.Port,
            Database = source.Database ?? string.Empty,
            Username = source.Username ?? string.Empty,
            Password = source.Password ?? string.Empty,
            Pooling = source.Pooling,
            ConnectionTimeoutSeconds = source.Timeout,
            CommandTimeoutSeconds = source.CommandTimeout
        };

        var succeeded = await viewModel.EnsureConnectionAsync(
            CancellationToken.None);

        Assert.True(succeeded, viewModel.ConnectionStatus);
        Assert.Equal("Connection successful", viewModel.ConnectionStatus.Split(
            Environment.NewLine,
            StringSplitOptions.None)[0]);
    }
}
