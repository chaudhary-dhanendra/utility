namespace MigrationStudio.Application.Discovery;

public sealed class SourceDatabaseException : Exception
{
    public SourceDatabaseException(
        string message,
        IReadOnlyList<SqlServerError> errors,
        Exception innerException,
        DiscoveryStage stage = DiscoveryStage.Failed,
        string queryId = "SQLSERVER.UNKNOWN",
        Guid? correlationId = null,
        bool isRetryable = false,
        string? remediation = null)
        : base(message, innerException)
    {
        Errors = errors;
        Stage = stage;
        QueryId = queryId;
        CorrelationId = correlationId ?? Guid.NewGuid();
        IsRetryable = isRetryable;
        Remediation = remediation;
    }

    public IReadOnlyList<SqlServerError> Errors { get; }

    public DiscoveryStage Stage { get; }

    public string QueryId { get; }

    public Guid CorrelationId { get; }

    public bool IsRetryable { get; }

    public string? Remediation { get; }
}
