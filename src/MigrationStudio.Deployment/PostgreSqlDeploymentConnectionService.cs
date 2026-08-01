using MigrationStudio.Application.Deployment;
using MigrationStudio.Domain.Deployment;
using Npgsql;

namespace MigrationStudio.Deployment;

public sealed class PostgreSqlDeploymentConnectionService :
    IPostgreSqlDeploymentConnectionService
{
    public async Task<PostgreSqlCapabilityAssessment> AssessAsync(
        PostgreSqlConnectionOptions options,
        bool useMaintenanceDatabase,
        CancellationToken cancellationToken)
    {
        options.Validate();
        var builder = CreateBuilder(options, useMaintenanceDatabase);
        var redacted = Redact(builder);
        var warnings = new List<string>();
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var identity = new NpgsqlCommand(
            """
            SELECT current_setting('server_version'),
                   current_setting('server_version_num')::integer,
                   current_user,
                   current_database(),
                   r.rolcreatedb,
                   r.rolsuper,
                   r.rolcreaterole
            FROM pg_roles r
            WHERE r.rolname = current_user
            """,
            connection);
        await using var reader = await identity.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The connected PostgreSQL role could not be assessed.");
        }

        var serverVersion = reader.GetString(0);
        var serverMajor = reader.GetInt32(1) / 10_000;
        var currentUser = reader.GetString(2);
        var currentDatabase = reader.GetString(3);
        var canCreateDatabase = reader.GetBoolean(4);
        var superUser = reader.GetBoolean(5);
        var canCreateRole = reader.GetBoolean(6);
        await reader.CloseAsync().ConfigureAwait(false);

        var installed = await ReadDictionaryAsync(
            connection,
            "SELECT extname, extversion FROM pg_extension ORDER BY extname",
            cancellationToken).ConfigureAwait(false);
        var available = (await ReadColumnAsync(
            connection,
            "SELECT name FROM pg_available_extensions ORDER BY name",
            cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var memberships = (await ReadColumnAsync(
            connection,
            """
            SELECT parent.rolname
            FROM pg_auth_members m
            JOIN pg_roles child ON child.oid = m.member
            JOIN pg_roles parent ON parent.oid = m.roleid
            WHERE child.rolname = current_user
            ORDER BY parent.rolname
            """,
            cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canCreateSchema = await ScalarBoolAsync(
            connection,
            "SELECT has_database_privilege(current_user, current_database(), 'CREATE')",
            cancellationToken).ConfigureAwait(false);
        if (!options.Pooling)
        {
            warnings.Add("Pooling is disabled; deployment will open more physical connections.");
        }

        return new PostgreSqlCapabilityAssessment(
            true,
            serverVersion,
            serverMajor,
            currentUser,
            currentDatabase,
            canCreateDatabase || superUser,
            superUser,
            canCreateRole || superUser,
            canCreateSchema || superUser,
            installed,
            available,
            memberships,
            redacted,
            warnings);
    }

    public async Task<IReadOnlyList<string>> LoadDatabasesAsync(
        PostgreSqlConnectionOptions options,
        CancellationToken cancellationToken)
    {
        var builder = CreateBuilder(options, true);
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadColumnAsync(
            connection,
            "SELECT datname FROM pg_database WHERE datallowconn AND NOT datistemplate ORDER BY datname",
            cancellationToken).ConfigureAwait(false);
    }

    internal static NpgsqlConnectionStringBuilder CreateBuilder(
        PostgreSqlConnectionOptions options,
        bool maintenance)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = options.Host,
            Port = options.Port,
            Database = maintenance ? options.MaintenanceDatabase : options.TargetDatabase,
            Username = options.Username,
            Password = options.Password,
            SslMode = Enum.TryParse<SslMode>(options.SslMode, true, out var sslMode)
                ? sslMode
                : throw new InvalidOperationException($"Unsupported SSL mode: {options.SslMode}"),
            Timeout = options.ConnectionTimeoutSeconds,
            CommandTimeout = options.CommandTimeoutSeconds,
            KeepAlive = options.KeepAliveSeconds,
            Pooling = options.Pooling,
            ApplicationName = options.ApplicationName,
            SearchPath = options.SearchPath
        };
        if (!string.IsNullOrWhiteSpace(options.RootCertificate))
        {
            builder.RootCertificate = options.RootCertificate;
        }

        if (!string.IsNullOrWhiteSpace(options.ClientCertificate))
        {
            builder.SslCertificate = options.ClientCertificate;
        }

        if (!string.IsNullOrWhiteSpace(options.ClientCertificateKey))
        {
            builder.SslKey = options.ClientCertificateKey;
        }

        return builder;
    }

    internal static string Redact(NpgsqlConnectionStringBuilder builder)
    {
        var copy = new NpgsqlConnectionStringBuilder(builder.ConnectionString);
        if (!string.IsNullOrEmpty(copy.Password))
        {
            copy.Password = "***";
        }

        return copy.ConnectionString;
    }

    private static async Task<Dictionary<string, string>> ReadDictionaryAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<bool> ScalarBoolAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToBoolean(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
