using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Deployment;
using Npgsql;

namespace MigrationStudio.Deployment;

public sealed partial class DatabaseProvisioningService : IDatabaseProvisioningService
{
    public async Task<DatabaseProvisioningResult> EnsureDatabaseAsync(
        PostgreSqlConnectionOptions connection,
        DatabaseCreationOptions options,
        CancellationToken cancellationToken)
    {
        var builder = PostgreSqlDeploymentConnectionService.CreateBuilder(connection, true);
        await using var maintenanceConnection = new NpgsqlConnection(builder.ConnectionString);
        await maintenanceConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var requested = connection.TargetDatabase;
        var exists = await ExistsAsync(maintenanceConnection, requested, cancellationToken).ConfigureAwait(false);
        var effective = requested;
        var dropped = false;
        var created = false;
        var usedExisting = false;

        if (exists)
        {
            switch (options.ExistsPolicy)
            {
                case DatabaseExistsPolicy.Fail:
                    throw new InvalidOperationException($"Target database '{requested}' already exists.");
                case DatabaseExistsPolicy.UseExisting:
                    usedExisting = true;
                    break;
                case DatabaseExistsPolicy.DropAndRecreate:
                    if (!options.DestructiveActionConfirmed)
                    {
                        throw new InvalidOperationException(
                            "Dropping an existing database requires explicit confirmation.");
                    }

                    await TerminateConnectionsAsync(maintenanceConnection, requested, cancellationToken)
                        .ConfigureAwait(false);
                    await ExecuteAsync(
                        maintenanceConnection,
                        $"DROP DATABASE {Quote(requested)}",
                        cancellationToken).ConfigureAwait(false);
                    dropped = true;
                    break;
                case DatabaseExistsPolicy.CreateWithAlternateName:
                    effective = await FindAlternateNameAsync(maintenanceConnection, requested, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(options), options.ExistsPolicy, "Unknown database policy.");
            }
        }

        if (!usedExisting)
        {
            var sql = BuildCreateDatabaseSql(effective, options);
            await ExecuteAsync(maintenanceConnection, sql, cancellationToken).ConfigureAwait(false);
            created = true;
        }

        return new DatabaseProvisioningResult(
            requested,
            effective,
            created,
            dropped,
            usedExisting,
            created
                ? $"Database '{effective}' was created."
                : $"Existing database '{effective}' will be used.");
    }

    internal static string BuildCreateDatabaseSql(string database, DatabaseCreationOptions options)
    {
        if (!EncodingPattern().IsMatch(options.Encoding))
        {
            throw new InvalidOperationException("Database encoding contains unsupported characters.");
        }

        var sql = new StringBuilder("CREATE DATABASE ").Append(Quote(database))
            .Append(" ENCODING ").Append(Literal(options.Encoding));
        if (!string.IsNullOrWhiteSpace(options.Owner))
        {
            sql.Append(" OWNER ").Append(Quote(options.Owner));
        }

        if (!string.IsNullOrWhiteSpace(options.Locale))
        {
            sql.Append(" LOCALE ").Append(Literal(options.Locale));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(options.Collation))
            {
                sql.Append(" LC_COLLATE ").Append(Literal(options.Collation));
            }

            if (!string.IsNullOrWhiteSpace(options.CharacterType))
            {
                sql.Append(" LC_CTYPE ").Append(Literal(options.CharacterType));
            }
        }

        if (options.ConnectionLimit is { } limit)
        {
            if (limit < -1)
            {
                throw new InvalidOperationException("Connection limit must be -1 or non-negative.");
            }

            sql.Append(" CONNECTION LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
        }

        return sql.ToString();
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @database)",
            connection);
        command.Parameters.AddWithValue("database", database);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private static async Task<string> FindAlternateNameAsync(
        NpgsqlConnection connection,
        string requested,
        CancellationToken cancellationToken)
    {
        for (var suffix = 1; suffix <= 10_000; suffix++)
        {
            var candidate = $"{requested}_{suffix}";
            if (!await ExistsAsync(connection, candidate, cancellationToken).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No available alternate database name was found.");
    }

    private static async Task TerminateConnectionsAsync(
        NpgsqlConnection connection,
        string database,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_terminate_backend(pid)
            FROM pg_stat_activity
            WHERE datname = @database AND pid <> pg_backend_pid()
            """,
            connection);
        command.Parameters.AddWithValue("database", database);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Quote(string identifier) =>
        MigrationStudio.Application.Conversion.PostgreSqlIdentifierQuoter.Quote(identifier);

    private static string Literal(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EncodingPattern();
}
