using Microsoft.Extensions.Logging;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Infrastructure.Security;
using Npgsql;
using System.Text.Json;

namespace MigrationStudio.Tests.Desktop;

public sealed class PostgreSqlConnectionViewModelTests
{
    [Fact]
    public void Defaults_AreSafeAndUsefulForLocalDevelopment()
    {
        var viewModel = Create();

        Assert.Equal("localhost", viewModel.Host);
        Assert.Equal(5432, viewModel.Port);
        Assert.Equal("VBGRAMG_POSTGRES", viewModel.Database);
        Assert.Equal("postgres", viewModel.Username);
        Assert.True(viewModel.Pooling);
        Assert.False(viewModel.UseSsl);
        Assert.Equal(15, viewModel.ConnectionTimeoutSeconds);
        Assert.Equal(300, viewModel.CommandTimeoutSeconds);
    }

    [Theory]
    [InlineData("", 5432, "target", "postgres", "PostgreSQL host is required.")]
    [InlineData("localhost", 0, "target", "postgres", "PostgreSQL port must be between 1 and 65535.")]
    [InlineData("localhost", 65536, "target", "postgres", "PostgreSQL port must be between 1 and 65535.")]
    [InlineData("localhost", 5432, "", "postgres", "PostgreSQL database name is required.")]
    [InlineData("localhost", 5432, "target", "", "PostgreSQL username is required.")]
    public void Validation_ReturnsFieldSpecificMessage(
        string host,
        int port,
        string database,
        string username,
        string expected)
    {
        var viewModel = Create();
        viewModel.Host = host;
        viewModel.Port = port;
        viewModel.Database = database;
        viewModel.Username = username;

        Assert.False(viewModel.Validate());
        Assert.Equal(expected, viewModel.ValidationMessage);
    }

    [Fact]
    public void BuildConnectionString_UsesNpgsqlBuilderAndKeepsPasswordInternal()
    {
        var viewModel = Create();
        viewModel.Host = " db.example.test ";
        viewModel.Database = " target ";
        viewModel.Username = " migrator ";
        viewModel.Password = "not-for-output";
        viewModel.UseSsl = true;
        viewModel.SelectedSslMode = PostgreSqlSslMode.VerifyFull;

        var builder = new NpgsqlConnectionStringBuilder(
            viewModel.BuildConnectionString());

        Assert.Equal("db.example.test", builder.Host);
        Assert.Equal("target", builder.Database);
        Assert.Equal("migrator", builder.Username);
        Assert.Equal("not-for-output", builder.Password);
        Assert.Equal(SslMode.VerifyFull, builder.SslMode);
        Assert.DoesNotContain("not-for-output", viewModel.ConnectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPassword_ProducesFieldValidationAndIsExcludedFromSerialization()
    {
        var viewModel = Create();
        viewModel.Password = string.Empty;

        Assert.False(viewModel.Validate());
        Assert.Equal("PostgreSQL password is required.", viewModel.ValidationMessage);
        Assert.DoesNotContain(
            nameof(PostgreSqlConnectionViewModel.Password),
            JsonSerializer.Serialize(viewModel),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnection_PassesSuppliedPasswordToNpgsqlBuilder()
    {
        const string password = "probe-password-fixture";
        string? receivedPassword = null;
        var viewModel = Create((connectionString, _) =>
        {
            receivedPassword = new NpgsqlConnectionStringBuilder(connectionString).Password;
            return Task.FromResult(new PostgreSqlConnectionTestResult(
                "VBGRAMG_POSTGRES",
                "postgres",
                "17.2",
                "UTF8"));
        });
        viewModel.Password = password;

        Assert.True(await viewModel.EnsureConnectionAsync(CancellationToken.None));
        Assert.Equal(password, receivedPassword);
        Assert.DoesNotContain(password, viewModel.ConnectionStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulTest_SetsValidAndFieldChangeInvalidatesIt()
    {
        var viewModel = Create((_, _) => Task.FromResult(
            new PostgreSqlConnectionTestResult(
                "VBGRAMG_POSTGRES",
                "postgres",
                "17.2",
                "UTF8")));

        Assert.True(await viewModel.EnsureConnectionAsync(CancellationToken.None));
        Assert.True(viewModel.IsConnectionValid);
        Assert.Contains("Server version: 17.2", viewModel.ConnectionStatus, StringComparison.Ordinal);

        viewModel.Password = "changed-password";

        Assert.False(viewModel.IsConnectionValid);
        Assert.Equal("Connection settings changed. Test again.", viewModel.ConnectionStatus);
    }

    [Fact]
    public async Task AuthenticationFailure_IsSanitizedAndNeverContainsPassword()
    {
        const string password = "top-secret-value";
        var logger = new CollectingLogger<PostgreSqlConnectionViewModel>();
        var viewModel = Create(
            (_, _) => Task.FromException<PostgreSqlConnectionTestResult>(
                new PostgresException(
                    $"password={password}",
                    "ERROR",
                    "ERROR",
                    "28P01")),
            logger);
        viewModel.Password = password;

        Assert.False(await viewModel.EnsureConnectionAsync(CancellationToken.None));
        Assert.Equal(
            "Password authentication failed for user 'postgres'.",
            viewModel.ValidationMessage);
        Assert.DoesNotContain(password, viewModel.ConnectionStatus, StringComparison.Ordinal);
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(password, StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains(
                "password supplied: True",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DatabaseNotFound_UsesTargetedSanitizedMessage()
    {
        var viewModel = Create((_, _) =>
            Task.FromException<PostgreSqlConnectionTestResult>(
                new PostgresException("missing", "ERROR", "ERROR", "3D000")));

        Assert.False(await viewModel.EnsureConnectionAsync(CancellationToken.None));
        Assert.Equal(
            "Target database 'VBGRAMG_POSTGRES' does not exist.",
            viewModel.ValidationMessage);
    }

    [Fact]
    public async Task TimeoutAndCancellation_AreReportedWithoutThrowing()
    {
        var timeout = Create((_, _) =>
            Task.FromException<PostgreSqlConnectionTestResult>(
                new TimeoutException("socket timeout")));
        Assert.False(await timeout.EnsureConnectionAsync(CancellationToken.None));
        Assert.Equal(
            "The PostgreSQL connection test timed out.",
            timeout.ValidationMessage);

        var cancellation = Create(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException();
        });
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.False(await cancellation.EnsureConnectionAsync(source.Token));
        Assert.Equal(
            "PostgreSQL connection test was cancelled.",
            cancellation.ValidationMessage);
    }

    private static PostgreSqlConnectionViewModel Create(
        Func<string, CancellationToken, Task<PostgreSqlConnectionTestResult>>? probe = null,
        ILogger<PostgreSqlConnectionViewModel>? logger = null) =>
        CreateWithPassword(probe, logger);

    private static PostgreSqlConnectionViewModel CreateWithPassword(
        Func<string, CancellationToken, Task<PostgreSqlConnectionTestResult>>? probe,
        ILogger<PostgreSqlConnectionViewModel>? logger)
    {
        var viewModel = new PostgreSqlConnectionViewModel(
            new SensitiveDataRedactor(),
            logger ?? new CollectingLogger<PostgreSqlConnectionViewModel>(),
            probe ?? ((_, _) => Task.FromResult(
                new PostgreSqlConnectionTestResult(
                    "VBGRAMG_POSTGRES",
                    "postgres",
                    "17.2",
                    "UTF8"))));
        viewModel.Password = "fixture-password";
        return viewModel;
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
