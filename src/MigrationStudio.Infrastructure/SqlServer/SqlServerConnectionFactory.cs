using Microsoft.Data.SqlClient;
using MigrationStudio.Application.Discovery;

namespace MigrationStudio.Infrastructure.SqlServer;

internal static class SqlServerConnectionFactory
{
    public static SqlConnection Create(SqlServerConnectionOptions options, bool useMaster = false)
    {
        options.Validate(requireDatabase: !useMaster);
        var dataSource = options.Port is { } port
            ? $"{options.Server.Trim()},{port}"
            : options.Server.Trim();
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            InitialCatalog = useMaster ? "master" : options.Database.Trim(),
            IntegratedSecurity = options.AuthenticationMode == SqlServerAuthenticationMode.Windows,
            Encrypt = options.Encrypt,
            TrustServerCertificate = options.TrustServerCertificate,
            ConnectTimeout = options.ConnectionTimeoutSeconds,
            ApplicationName = "SQL Server to PostgreSQL Migration Studio",
            MultipleActiveResultSets = false,
            PersistSecurityInfo = false
        };

        if (options.AuthenticationMode == SqlServerAuthenticationMode.SqlServer)
        {
            builder.UserID = options.Username;
            builder.Password = options.Password;
        }

        return new SqlConnection(builder.ConnectionString);
    }

    public static IReadOnlyList<SqlServerError> MapErrors(SqlException exception) =>
        exception.Errors.Cast<SqlError>()
            .Select(error => new SqlServerError(
                error.Number,
                error.Class,
                error.State,
                error.Message,
                string.IsNullOrWhiteSpace(error.Procedure) ? null : error.Procedure,
                error.LineNumber))
            .ToArray();
}
