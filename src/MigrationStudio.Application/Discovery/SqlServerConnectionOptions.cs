namespace MigrationStudio.Application.Discovery;

public enum SqlServerAuthenticationMode
{
    Windows,
    SqlServer
}

public sealed record SqlServerConnectionOptions
{
    public string Server { get; init; } = string.Empty;

    public int? Port { get; init; }

    public string Database { get; init; } = string.Empty;

    public SqlServerAuthenticationMode AuthenticationMode { get; init; } = SqlServerAuthenticationMode.Windows;

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool Encrypt { get; init; } = true;

    public bool TrustServerCertificate { get; init; }

    public int ConnectionTimeoutSeconds { get; init; } = 15;

    public int CommandTimeoutSeconds { get; init; } = 120;

    public SqlServerConnectionOptions Validate(bool requireDatabase = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Server);
        if (requireDatabase)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Database);
        }

        if (Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        }

        if (ConnectionTimeoutSeconds is < 1 or > 300)
        {
            throw new InvalidOperationException("Connection timeout must be between 1 and 300 seconds.");
        }

        if (CommandTimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException("Command timeout must be between 1 and 3600 seconds.");
        }

        if (AuthenticationMode == SqlServerAuthenticationMode.SqlServer)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Username);
            ArgumentException.ThrowIfNullOrWhiteSpace(Password);
        }

        return this;
    }
}
