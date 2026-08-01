using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using MigrationStudio.Application.Discovery;

namespace MigrationStudio.Infrastructure.SqlServer;

public sealed class SqlServerConnectionService : ISqlServerConnectionService
{
    public async Task<ConnectionTestResult> TestAsync(
        SqlServerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await using var connection = SqlServerConnectionFactory.Create(options);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                    DB_NAME();
                """;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = options.CommandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            return new ConnectionTestResult(
                true,
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                stopwatch.Elapsed,
                []);
        }
        catch (SqlException exception)
        {
            return new ConnectionTestResult(
                false,
                null,
                null,
                stopwatch.Elapsed,
                SqlServerConnectionFactory.MapErrors(exception));
        }
    }

    public async Task<IReadOnlyList<string>> LoadDatabasesAsync(
        SqlServerConnectionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            await using var connection = SqlServerConnectionFactory.Create(options, useMaster: true);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT [name]
                FROM sys.databases
                WHERE [state] = 0
                  AND HAS_DBACCESS([name]) = 1
                ORDER BY [name];
                """;
            command.CommandTimeout = options.CommandTimeoutSeconds;
            var databases = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                databases.Add(reader.GetString(0));
            }

            return databases;
        }
        catch (SqlException exception)
        {
            throw new SourceDatabaseException(
                "SQL Server databases could not be loaded.",
                SqlServerConnectionFactory.MapErrors(exception),
                exception);
        }
    }
}
