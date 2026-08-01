using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Application.Conversion;

public sealed record ConversionProgress(
    ConversionStage Stage,
    int CompletedObjects,
    int TotalObjects,
    string Message)
{
    public double Percentage
    {
        get
        {
            var (start, end) = Stage switch
            {
                ConversionStage.CollectingIncludedObjects => (0d, 5d),
                ConversionStage.GeneratingIdentifierCandidates => (5d, 15d),
                ConversionStage.ResolvingCollisions => (15d, 25d),
                ConversionStage.ValidatingIdentifiers => (25d, 35d),
                ConversionStage.PublishingIdentifierMap => (35d, 40d),
                ConversionStage.ConvertingObjects => (40d, 75d),
                ConversionStage.OrderingDependencies => (75d, 82d),
                ConversionStage.BuildingDeploymentPackage => (82d, 95d),
                ConversionStage.ValidatingPackage => (95d, 98d),
                ConversionStage.CompletingReports => (98d, 100d),
                _ => (0d, 100d)
            };
            var stageProgress = TotalObjects <= 0
                ? 0d
                : Math.Clamp(CompletedObjects / (double)TotalObjects, 0d, 1d);
            return start + ((end - start) * stageProgress);
        }
    }

    public double ObjectsPerSecond { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string CurrentObjectType { get; init; } = string.Empty;

    public string CurrentObject { get; init; } = string.Empty;

    public Guid MappingSetId { get; init; }

    public DateTimeOffset LastProgressAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ConversionContext(
    InventorySnapshot Inventory,
    ConversionOptions Options,
    IIdentifierMapper Identifiers,
    ITypeMappingRegistry TypeMappings,
    ISqlExpressionTranslator Expressions,
    IReadOnlyDictionary<InventoryObjectId, InventoryObject> ObjectsById,
    IReadOnlyDictionary<InventoryObjectId, TargetObjectIdentifier> TargetsBySource)
{
    public ConversionInventoryIndex InventoryIndex { get; init; } =
        ConversionInventoryIndex.Create(Inventory);

    public IReadOnlyDictionary<string, string> TargetObjectNames { get; init; } =
        MappedPostgreSqlIdentifierRenderer.CreateObjectReferenceMap(
            ObjectsById.Values,
            TargetsBySource);
}

public interface IObjectConverter<TSource, TTarget>
{
    bool CanConvert(TSource source, ConversionContext context);

    Task<ConversionResult<TTarget>> ConvertAsync(
        TSource source,
        ConversionContext context,
        CancellationToken cancellationToken);
}

public interface IConversionEngine
{
    Task<ConversionRun> ConvertAsync(
        InventorySnapshot inventory,
        ConversionOptions options,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IConversionSession
{
    ConversionRun? Current { get; }

    event EventHandler? Changed;

    void SetCurrent(ConversionRun run);

    void Clear();
}

public interface IIdentifierMappingService
{
    IIdentifierMapper CreateMapper(InventorySnapshot inventory, ConversionOptions options);

    IIdentifierMapper CreateMapper(
        InventorySnapshot inventory,
        ConversionOptions options,
        CancellationToken cancellationToken,
        IProgress<ConversionProgress>? progress) =>
        CreateMapper(inventory, options);
}

public interface IIdentifierMapper
{
    Guid MappingSetId => Guid.Empty;

    int SchemaVersion => IdentifierMappingSchema.CurrentVersion;

    bool LoadedFromCache => false;

    string MapSchema(string sourceSchema);

    TargetObjectIdentifier MapObject(InventoryObject source);

    string MapChildIdentifier(
        InventoryObjectId ownerId,
        string objectType,
        string sourceSchema,
        string sourceName);

    string QuoteIdentifier(string identifier);

    IReadOnlyList<IdentifierMappingEntry> Mappings { get; }
}

public interface ITypeMappingRegistry
{
    TypeMappingResult Map(
        ColumnInventory column,
        InventoryObject table,
        ConversionOptions options);

    TypeMappingResult Map(
        string sourceType,
        short maximumLength,
        byte precision,
        byte scale,
        ConversionOptions options,
        string? schema = null,
        string? table = null,
        string? column = null);
}

public sealed record ExpressionTranslationContext(
    InventoryObjectId SourceObjectId,
    IReadOnlyDictionary<string, string> ColumnTypes,
    ConversionOptions Options,
    bool IsGeneratedColumn)
{
    public IReadOnlyDictionary<string, string> TargetColumnNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> TargetObjectNames { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> TargetColumnTypes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? ExpectedTargetType { get; init; }
}

public sealed record ExpressionTranslationResult(
    string Sql,
    ConversionClassification Classification,
    decimal Confidence,
    IReadOnlyList<InventoryFinding> Findings,
    IReadOnlyList<string> UnsupportedFunctions,
    IReadOnlyList<string> ReferencedColumns,
    IReadOnlyList<string> RequiredExtensions,
    bool IsImmutable);

public interface ISqlExpressionTranslator
{
    ExpressionTranslationResult Translate(
        string expression,
        ExpressionTranslationContext context);
}

public interface IGeneratedSqlValidator
{
    Task<SqlValidationResult> ValidateOfflineAsync(
        string sql,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, SqlValidationResult>> ValidateLiveAsync(
        IReadOnlyList<ConversionArtifact> artifacts,
        PostgreSqlValidationOptions options,
        CancellationToken cancellationToken);
}

public sealed record PostgreSqlValidationOptions(
    string ConnectionString,
    string ValidationSchemaPrefix = "migrationstudio_validation")
{
    public string MaintenanceDatabase { get; init; } = "postgres";

    public bool PreferDisposableDatabase { get; init; } = true;

    public bool AllowRollbackTransactionFallback { get; init; } = true;

    public int CommandTimeoutSeconds { get; init; } = 120;

    public IReadOnlyDictionary<string, SqlValidationResult> ReusableSuccessfulResults { get; init; } =
        new Dictionary<string, SqlValidationResult>(StringComparer.Ordinal);

    public IProgress<LiveSqlValidationProgress>? Progress { get; init; }
}

public sealed record LiveSqlValidationProgress(
    int CompletedArtifacts,
    int TotalArtifacts,
    string CurrentObject,
    string Message)
{
    public double Percentage => TotalArtifacts == 0
        ? 100
        : Math.Clamp(CompletedArtifacts * 100d / TotalArtifacts, 0, 100);
}

public interface IDeploymentPackageWriter
{
    Task<string> WriteAsync(
        ConversionRun run,
        string parentDirectory,
        CancellationToken cancellationToken);

    Task<string> WriteAsync(
        ConversionRun run,
        string parentDirectory,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken) =>
        WriteAsync(run, parentDirectory, cancellationToken);
}

public interface IConversionReportWriter
{
    Task WriteAsync(
        ConversionRun run,
        string reportsDirectory,
        CancellationToken cancellationToken);
}
