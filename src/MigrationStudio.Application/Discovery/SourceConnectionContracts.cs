namespace MigrationStudio.Application.Discovery;

public sealed record SqlServerError(
    int Number,
    byte Class,
    byte State,
    string Message,
    string? Procedure,
    int LineNumber);

public sealed record ConnectionTestResult(
    bool Succeeded,
    string? ServerVersion,
    string? DatabaseName,
    TimeSpan Duration,
    IReadOnlyList<SqlServerError> Errors);

public interface ISqlServerConnectionService
{
    Task<ConnectionTestResult> TestAsync(
        SqlServerConnectionOptions options,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> LoadDatabasesAsync(
        SqlServerConnectionOptions options,
        CancellationToken cancellationToken);
}
