using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Validation;

namespace MigrationStudio.Application.Validation;

public sealed record ValidationConnectionOptions(
    string SourceConnectionString,
    string TargetConnectionString);

public sealed record ValidationProgress(
    string Stage,
    int Completed,
    int Total,
    string CurrentObject)
{
    public double Percentage => Total == 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}

public sealed record ValidationRequest(
    InventorySnapshot SourceSnapshot,
    ConversionRun Conversion,
    ValidationConnectionOptions Connections,
    ValidationConfiguration Configuration,
    Guid? MigrationRunId = null,
    Guid? DeploymentRunId = null);

public interface ICanonicalValueSerializer
{
    CanonicalValue Serialize(
        object? value,
        CanonicalValueKind kind,
        CanonicalComparisonOptions options,
        bool fixedWidth = false,
        bool sensitive = false);

    bool AreEquivalent(CanonicalValue left, CanonicalValue right, CanonicalComparisonOptions options);
}

public interface ICanonicalChecksumService
{
    string HashRow(IReadOnlyList<CanonicalValue> values);

    string HashOrderedRows(IEnumerable<IReadOnlyList<CanonicalValue>> rows);

    string HashUnorderedRows(IEnumerable<IReadOnlyList<CanonicalValue>> rows);

    string HashChunks(IEnumerable<string> chunkHashes);
}

public interface IPostgreSqlValidationMetadataReader
{
    Task<TargetDatabaseSnapshot> ReadAsync(
        string connectionString,
        ValidationScope scope,
        CancellationToken cancellationToken);
}

public interface IValidationEngine
{
    Task<ValidationRun> ValidateAsync(
        ValidationRequest request,
        IProgress<ValidationProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IValidationSession
{
    ValidationRun? Current { get; }

    event EventHandler? Changed;

    void SetCurrent(ValidationRun run);
}

public interface IValidationRunStore
{
    Task<string> SaveAsync(ValidationRun run, CancellationToken cancellationToken);

    Task<ValidationRun> LoadAsync(string path, CancellationToken cancellationToken);
}

public interface IValidationReportWriter
{
    Task<IReadOnlyList<string>> WriteAsync(
        ValidationRun run,
        string reportsDirectory,
        CancellationToken cancellationToken);
}
