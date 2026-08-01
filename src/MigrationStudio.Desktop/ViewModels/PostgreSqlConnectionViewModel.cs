using System.Diagnostics;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Security;
using Npgsql;

namespace MigrationStudio.Desktop.ViewModels;

public enum PostgreSqlSslMode
{
    Disable,
    Allow,
    Prefer,
    Require,
    VerifyCA,
    VerifyFull
}

public sealed partial class PostgreSqlConnectionViewModel : ObservableObject
{
    private readonly ISensitiveDataRedactor _redactor;
    private readonly ILogger<PostgreSqlConnectionViewModel> _logger;
    private readonly Func<string, CancellationToken, Task<PostgreSqlConnectionTestResult>> _probe;
    private int _testInFlight;

    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private int _port = 5432;
    [ObservableProperty] private string _database = "VBGRAMG_POSTGRES";
    [ObservableProperty] private string _username = "postgres";
    [ObservableProperty]
    [property: JsonIgnore]
    private string _password = string.Empty;
    [ObservableProperty] private bool _useSsl;
    [ObservableProperty] private PostgreSqlSslMode _selectedSslMode = PostgreSqlSslMode.Prefer;
    [ObservableProperty] private bool _trustServerCertificate;
    [ObservableProperty] private int _connectionTimeoutSeconds = 15;
    [ObservableProperty] private int _commandTimeoutSeconds = 300;
    [ObservableProperty] private bool _pooling = true;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private bool _isConnectionValid;
    [ObservableProperty] private string _connectionStatus = "Connection not tested.";
    [ObservableProperty] private string _validationMessage = string.Empty;

    public PostgreSqlConnectionViewModel(
        ISensitiveDataRedactor redactor,
        ILogger<PostgreSqlConnectionViewModel> logger)
        : this(redactor, logger, ProbeAsync)
    {
    }

    internal PostgreSqlConnectionViewModel(
        ISensitiveDataRedactor redactor,
        ILogger<PostgreSqlConnectionViewModel> logger,
        Func<string, CancellationToken, Task<PostgreSqlConnectionTestResult>> probe)
    {
        _redactor = redactor;
        _logger = logger;
        _probe = probe;
    }

    public IReadOnlyList<PostgreSqlSslMode> SslModes { get; } =
        Enum.GetValues<PostgreSqlSslMode>();

    public bool Validate()
    {
        ValidationMessage = HostValidationMessage();
        return ValidationMessage.Length == 0;
    }

    public string BuildConnectionString()
    {
        if (!Validate())
        {
            throw new InvalidOperationException(ValidationMessage);
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host.Trim(),
            Port = Port,
            Database = Database.Trim(),
            Username = Username.Trim(),
            Password = Password,
            Pooling = Pooling,
            Timeout = ConnectionTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            SslMode = ResolveSslMode(),
            ApplicationName = "MigrationStudio.DataMigration"
        };
        return builder.ConnectionString;
    }

    public async Task<bool> EnsureConnectionAsync(CancellationToken cancellationToken)
    {
        if (IsConnectionValid && Validate())
        {
            return true;
        }

        await TestConnectionCoreAsync(cancellationToken);
        return IsConnectionValid;
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private Task TestPostgreSqlConnectionAsync(CancellationToken cancellationToken) =>
        TestConnectionCoreAsync(cancellationToken);

    private bool CanTestConnection() => !IsTesting;

    private async Task TestConnectionCoreAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _testInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            IsTesting = true;
            IsConnectionValid = false;
            ConnectionStatus = "Testing connection...";
            var passwordSupplied = !string.IsNullOrEmpty(Password);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                PostgreSqlConnectionLog.PasswordPresence(
                    _logger,
                    passwordSupplied,
                    passwordSupplied);
            }

            if (!Validate())
            {
                ConnectionStatus = ValidationMessage;
                return;
            }

            var logHost = Host.Trim();
            var logDatabase = Database.Trim();
            var logUsername = Username.Trim();
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await _probe(BuildConnectionString(), cancellationToken);
                IsConnectionValid = true;
                ValidationMessage = string.Empty;
                ConnectionStatus =
                    $"Connection successful{Environment.NewLine}" +
                    $"Server version: {result.ServerVersion}{Environment.NewLine}" +
                    $"Database: {result.Database}{Environment.NewLine}" +
                    $"User: {result.User}{Environment.NewLine}" +
                    $"Encoding: {result.Encoding}";
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    PostgreSqlConnectionLog.Succeeded(
                        _logger,
                        logHost,
                        Port,
                        logDatabase,
                        logUsername,
                        result.ServerVersion,
                        stopwatch.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ValidationMessage = "PostgreSQL connection test was cancelled.";
                ConnectionStatus = ValidationMessage;
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    PostgreSqlConnectionLog.Failed(
                        _logger,
                        logHost,
                        Port,
                        logDatabase,
                        logUsername,
                        "Cancelled");
                }
            }
            catch (Exception exception)
            {
                var sanitized = SanitizeFailure(exception);
                ValidationMessage = sanitized;
                ConnectionStatus = $"Connection failed{Environment.NewLine}{sanitized}";
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    PostgreSqlConnectionLog.Failed(
                        _logger,
                        logHost,
                        Port,
                        logDatabase,
                        logUsername,
                        exception.GetType().Name);
                }
            }
        }
        finally
        {
            IsTesting = false;
            Interlocked.Exchange(ref _testInFlight, 0);
        }
    }

    private string HostValidationMessage()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            return "PostgreSQL host is required.";
        }

        if (Port is < 1 or > 65535)
        {
            return "PostgreSQL port must be between 1 and 65535.";
        }

        if (string.IsNullOrWhiteSpace(Database))
        {
            return "PostgreSQL database name is required.";
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            return "PostgreSQL username is required.";
        }

        if (string.IsNullOrEmpty(Password))
        {
            return "PostgreSQL password is required.";
        }

        if (ConnectionTimeoutSeconds <= 0)
        {
            return "PostgreSQL connection timeout must be positive.";
        }

        if (CommandTimeoutSeconds <= 0)
        {
            return "PostgreSQL command timeout must be positive.";
        }

        return string.Empty;
    }

    private SslMode ResolveSslMode()
    {
        if (!UseSsl)
        {
            return SslMode.Disable;
        }

        if (TrustServerCertificate)
        {
            return SslMode.Require;
        }

        return SelectedSslMode switch
        {
            PostgreSqlSslMode.Disable => SslMode.Disable,
            PostgreSqlSslMode.Allow => SslMode.Allow,
            PostgreSqlSslMode.Prefer => SslMode.Prefer,
            PostgreSqlSslMode.Require => SslMode.Require,
            PostgreSqlSslMode.VerifyCA => SslMode.VerifyCA,
            PostgreSqlSslMode.VerifyFull => SslMode.VerifyFull,
            _ => throw new InvalidOperationException("Unsupported PostgreSQL SSL mode.")
        };
    }

    private string SanitizeFailure(Exception exception)
    {
        if (exception is PostgresException { SqlState: "3D000" })
        {
            return $"Target database '{Database.Trim()}' does not exist.";
        }

        if (exception is PostgresException { SqlState: "28P01" })
        {
            return $"Password authentication failed for user '{Username.Trim()}'.";
        }

        if (exception is TimeoutException ||
            exception.InnerException is TimeoutException)
        {
            return "The PostgreSQL connection test timed out.";
        }

        var message = _redactor.Redact(exception.Message);
        if (!string.IsNullOrEmpty(Password))
        {
            message = message.Replace(Password, "***", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(message)
            ? "The PostgreSQL connection could not be established."
            : message;
    }

    private void InvalidateConnection()
    {
        if (IsTesting)
        {
            return;
        }

        IsConnectionValid = false;
        ValidationMessage = string.Empty;
        ConnectionStatus = "Connection settings changed. Test again.";
    }

    partial void OnHostChanged(string value) => InvalidateConnection();
    partial void OnPortChanged(int value) => InvalidateConnection();
    partial void OnDatabaseChanged(string value) => InvalidateConnection();
    partial void OnUsernameChanged(string value) => InvalidateConnection();
    partial void OnPasswordChanged(string value) => InvalidateConnection();
    partial void OnUseSslChanged(bool value) => InvalidateConnection();
    partial void OnSelectedSslModeChanged(PostgreSqlSslMode value) => InvalidateConnection();
    partial void OnTrustServerCertificateChanged(bool value) => InvalidateConnection();
    partial void OnConnectionTimeoutSecondsChanged(int value) => InvalidateConnection();
    partial void OnCommandTimeoutSecondsChanged(int value) => InvalidateConnection();
    partial void OnPoolingChanged(bool value) => InvalidateConnection();

    partial void OnIsTestingChanged(bool value) =>
        TestPostgreSqlConnectionCommand.NotifyCanExecuteChanged();

    private static async Task<PostgreSqlConnectionTestResult> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            SELECT current_database(),
                   current_user,
                   current_setting('server_version'),
                   current_setting('server_encoding');
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                "PostgreSQL returned no connection identity metadata.");
        }

        return new PostgreSqlConnectionTestResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
    }
}

internal sealed record PostgreSqlConnectionTestResult(
    string Database,
    string User,
    string ServerVersion,
    string Encoding);

internal static partial class PostgreSqlConnectionLog
{
    [LoggerMessage(
        EventId = 2119,
        Level = LogLevel.Information,
        Message = "PostgreSQL password supplied: {PasswordSupplied}; password length greater than zero: {PasswordLengthGreaterThanZero}.")]
    public static partial void PasswordPresence(
        ILogger logger,
        bool passwordSupplied,
        bool passwordLengthGreaterThanZero);

    [LoggerMessage(
        EventId = 2120,
        Level = LogLevel.Information,
        Message = "PostgreSQL connection test succeeded for {Host}:{Port}/{Database} as {Username}; server {ServerVersion}; duration {DurationMilliseconds} ms.")]
    public static partial void Succeeded(
        ILogger logger,
        string host,
        int port,
        string database,
        string username,
        string serverVersion,
        long durationMilliseconds);

    [LoggerMessage(
        EventId = 2121,
        Level = LogLevel.Warning,
        Message = "PostgreSQL connection test failed for {Host}:{Port}/{Database} as {Username}; category {FailureCategory}.")]
    public static partial void Failed(
        ILogger logger,
        string host,
        int port,
        string database,
        string username,
        string failureCategory);
}
