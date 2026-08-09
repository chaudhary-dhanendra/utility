using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Domain.Conversion;

public readonly record struct PostgreSqlVersion(int Major)
{
    public const int MinimumSupportedMajor = 14;
    public const int MaximumSupportedMajor = 18;

    public PostgreSqlVersion Validate()
    {
        if (Major is < MinimumSupportedMajor or > MaximumSupportedMajor)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {Major} is unsupported. Select a version from {MinimumSupportedMajor} through {MaximumSupportedMajor}.");
        }

        return this;
    }

    public override string ToString() => Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum IdentifierCaseMode
{
    LowercaseUnquoted,
    PreserveQuoted,
    QuoteOnlyWhenRequired,
    QuoteEveryIdentifier
}

public enum IdentifierMappingStatus
{
    Safe,
    ReservedWordSafelyQuoted,
    AutomaticallyShortened,
    CollisionResolved,
    BlockingConflict
}

public enum IdentifierMappingSeverity
{
    Information,
    Warning,
    Error
}

public enum IdentifierMappingAction
{
    Unchanged,
    Lowercased,
    Sanitized,
    ReservedWordAdjusted,
    Truncated,
    CollisionResolved,
    UserRuleApplied,
    AutoRecovered,
    Unsupported
}

public enum SchemaMappingMode
{
    Preserve,
    MapDboToPublic,
    MapAllToOne,
    Custom
}

public enum IdentityConversionStrategy
{
    GeneratedByDefaultAsIdentity,
    GeneratedAlwaysAsIdentity,
    SequenceAndDefault,
    PlainIntegerManual
}

public enum ComputedColumnStrategy
{
    GeneratedStored,
    GeneratedStoredWithWarning,
    PopulateDuringDataMigration,
    TriggerMaintained,
    ManualConversion
}

public enum SecurityConversionStrategy
{
    ReportOnly,
    GenerateRolesWithoutPasswords,
    GenerateRoleAndGrantScripts,
    ExternalIdentityMapping
}

public enum UserDefinedTypeStrategy
{
    Domain,
    BaseType,
    CompositeType
}

public enum SynonymConversionStrategy
{
    ManualReview,
    View,
    WrapperFunction,
    ForeignTableReference,
    SchemaSearchPathMapping
}

public enum ConversionValidationStatus
{
    ConvertedAndValidated,
    ConvertedNotValidated,
    ValidationFailed,
    ManualReview,
    Unsupported
}

public enum DeploymentPhase
{
    PreDeployment = 0,
    Extensions = 1,
    Schemas = 2,
    Types = 3,
    Sequences = 4,
    Tables = 5,
    DefaultsAndGeneratedColumns = 6,
    PrimaryKeys = 7,
    UniqueConstraints = 8,
    CheckConstraints = 9,
    Data = 10,
    SequenceReset = 11,
    ForeignKeys = 12,
    Indexes = 13,
    Functions = 14,
    Procedures = 15,
    Views = 16,
    Triggers = 17,
    Security = 18,
    Comments = 19,
    PostDeployment = 20,
    Validation = 21,
    PreDataFunctions = 22,
    ManualReview = 90
}

public sealed record SchemaMappingRule(
    string SourceSchema,
    string TargetSchema,
    bool IsExcluded = false);

public sealed record TypeMappingOverride(
    string SourceType,
    string TargetType,
    string? Schema = null,
    string? Table = null,
    string? Column = null);

public sealed record ConversionOptions
{
    public PostgreSqlVersion TargetVersion { get; init; } = new(17);

    public IdentifierCaseMode IdentifierCaseMode { get; init; } = IdentifierCaseMode.QuoteOnlyWhenRequired;

    public SchemaMappingMode SchemaMappingMode { get; init; } = SchemaMappingMode.Preserve;

    public string ConsolidatedSchema { get; init; } = "public";

    public IReadOnlyList<SchemaMappingRule> SchemaMappings { get; init; } = [];

    public IReadOnlyList<TypeMappingOverride> TypeOverrides { get; init; } = [];

    public IdentityConversionStrategy IdentityStrategy { get; init; } =
        IdentityConversionStrategy.GeneratedByDefaultAsIdentity;

    public SecurityConversionStrategy SecurityStrategy { get; init; } =
        SecurityConversionStrategy.ReportOnly;

    public UserDefinedTypeStrategy UserDefinedTypeStrategy { get; init; } =
        UserDefinedTypeStrategy.Domain;

    public bool EnablePgCrypto { get; init; } = true;

    public bool UseRandomUuidForNewSequentialId { get; init; } = true;

    public bool EnablePostGis { get; init; }

    public bool MoneyAsNumeric { get; init; } = true;

    public bool DeferForeignKeysUntilAfterData { get; init; } = true;

    public bool EmitConstraintStatementsSeparately { get; init; } = true;

    public SynonymConversionStrategy SynonymStrategy { get; init; } =
        SynonymConversionStrategy.ManualReview;
}

public sealed record TargetObjectIdentifier(
    string ObjectType,
    string Schema,
    string Name)
{
    public string QualifiedName => $"{Schema}.{Name}";
}

public readonly record struct SourceIdentifierKey(
    string DatabaseName,
    string SchemaName,
    string ParentObjectName,
    string ObjectName,
    string ObjectType,
    InventoryObjectId? ParentObjectId,
    InventoryObjectId? ObjectId)
{
    public int? ColumnId { get; init; }

    public InventoryObjectId? SourceSchemaId { get; init; }

    public ColumnIdentifierKey? ColumnKey =>
        ParentObjectId is { } tableObjectId && ColumnId is { } columnId
            ? new ColumnIdentifierKey(tableObjectId, columnId)
            : null;

    public TriggerIdentifierKey? TriggerKey =>
        ObjectType.Equals("trigger", StringComparison.OrdinalIgnoreCase) &&
        ObjectId is { } triggerObjectId &&
        ParentObjectId is { } parentTableObjectId &&
        SourceSchemaId is { } sourceSchemaId
            ? new TriggerIdentifierKey(
                triggerObjectId,
                parentTableObjectId,
                sourceSchemaId,
                ObjectName)
            : null;
}

public readonly record struct ColumnIdentifierKey(
    InventoryObjectId TableObjectId,
    int ColumnId)
{
    public override string ToString() =>
        $"Column|tableObjectId={TableObjectId}|columnId={ColumnId}";
}

public readonly record struct TriggerIdentifierKey(
    InventoryObjectId TriggerObjectId,
    InventoryObjectId ParentTableObjectId,
    InventoryObjectId SourceSchemaId,
    string SourceName)
{
    public override string ToString() =>
        $"Trigger|objectId={TriggerObjectId}|parentId={ParentTableObjectId}|" +
        $"schemaId={SourceSchemaId}|name={SourceName}";
}

public static class IdentifierMappingSchema
{
    public const int CurrentVersion = 3;
}

public sealed record IdentifierMappingCoverage(
    string ObjectType,
    int IncludedCount,
    int MappedCount);

public sealed record IdentifierMappingSetMetadata(
    Guid MappingSetId,
    int SchemaVersion,
    DateTimeOffset PublishedAt,
    bool LoadedFromCache,
    int TemporaryMapCount,
    int PublishedMapCount,
    int IncludedColumnCount,
    int MappedColumnCount)
{
    public IReadOnlyList<IdentifierMappingCoverage> Coverage { get; init; } = [];

    public int AutoRecoveredCount { get; init; }

    public int UnresolvedRequiredCount { get; init; }

    public static IdentifierMappingSetMetadata Legacy { get; } =
        new(Guid.Empty, 0, DateTimeOffset.MinValue, true, 0, 0, 0, 0);
}

public sealed record IdentifierMappingEntry(
    InventoryObjectId SourceObjectId,
    string ObjectType,
    string SourceSchema,
    string SourceName,
    string SourceQualifiedName,
    string TargetSchema,
    string TargetName,
    string TargetQualifiedName,
    int OriginalUtf8ByteLength,
    int TargetUtf8ByteLength,
    bool WasShortened,
    bool HadCollision,
    string? HashSuffix,
    string MappingReason)
{
    public string ParentObject { get; init; } = string.Empty;

    public string SourceDatabase { get; init; } = string.Empty;

    public SourceIdentifierKey SourceKey { get; init; } = new(
        string.Empty,
        SourceSchema,
        string.Empty,
        SourceName,
        ObjectType,
        null,
        SourceObjectId);

    public string TargetParentObject { get; init; } = string.Empty;

    public int SourceCharacterLength { get; init; } = SourceName.Length;

    public int TargetCharacterLength { get; init; } = Unquote(TargetName).Length;

    public bool IsReservedWord { get; init; }

    public bool RequiresQuoting { get; init; }

    public bool WasQuoted { get; init; }

    public bool WasCaseNormalized { get; init; }

    public bool CollisionResolved { get; init; }

    public bool InvalidCharacterReplacement { get; init; }

    public bool AutoRecovered { get; init; }

    public bool IncludedInScope { get; init; } = true;

    public ConversionClassification ConversionClassification { get; init; } =
        ConversionClassification.Automatic;

    public IdentifierMappingAction MappingAction { get; init; } =
        IdentifierMappingAction.Unchanged;

    public string CollisionGroup { get; init; } = string.Empty;

    public string CollisionResolution { get; init; } = string.Empty;

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IdentifierMappingStatus MappingStatus { get; init; } =
        HadCollision
            ? IdentifierMappingStatus.CollisionResolved
            : WasShortened
                ? IdentifierMappingStatus.AutomaticallyShortened
                : IdentifierMappingStatus.Safe;

    public IdentifierMappingSeverity Severity { get; init; } =
        HadCollision || WasShortened
            ? IdentifierMappingSeverity.Warning
            : IdentifierMappingSeverity.Information;

    public bool ManualReviewRequired { get; init; }

    public string TransformationReason => MappingReason;

    public bool IsBlocking =>
        MappingStatus == IdentifierMappingStatus.BlockingConflict ||
        Severity == IdentifierMappingSeverity.Error ||
        ManualReviewRequired;

    private static string Unquote(string identifier) =>
        identifier.Length >= 2 && identifier[0] == '"' && identifier[^1] == '"'
            ? identifier[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal)
            : identifier;
}

public sealed record IdentifierMappingSummary(
    int TotalIncludedObjects,
    int AutomaticallyMapped,
    int Unchanged,
    int Renamed,
    int Lowercased,
    int Sanitized,
    int Truncated,
    int ReservedWordsAdjusted,
    int CollisionsResolved,
    int AutoRecovered,
    int Unsupported,
    int Unresolved)
{
    public static IdentifierMappingSummary Create(
        IReadOnlyList<IdentifierMappingEntry> mappings)
    {
        var included = mappings.Where(item => item.IncludedInScope).ToArray();
        var total = included.Select(item => item.SourceKey).Distinct().Count();
        return new(
            total,
            total - included.Where(item => item.IsBlocking)
                .Select(item => item.SourceKey)
                .Distinct()
                .Count(),
            included.Count(item => item.MappingAction == IdentifierMappingAction.Unchanged),
            included.Count(item =>
                item.MappingAction is not IdentifierMappingAction.Unchanged and
                    not IdentifierMappingAction.Unsupported),
            included.Count(item => item.MappingAction == IdentifierMappingAction.Lowercased),
            included.Count(item => item.MappingAction == IdentifierMappingAction.Sanitized),
            included.Count(item => item.WasShortened),
            included.Count(item => item.IsReservedWord),
            included.Count(item => item.CollisionResolved),
            included.Count(item => item.AutoRecovered),
            included.Count(item => item.MappingAction == IdentifierMappingAction.Unsupported),
            included.Count(item => item.IsBlocking));
    }
}

public sealed record TypeMappingResult(
    string SourceType,
    string TargetType,
    ConversionClassification Classification,
    IReadOnlyList<string> RequiredExtensions,
    IReadOnlyList<InventoryFinding> Findings,
    string RuleId);

public sealed record SqlValidationResult(
    bool IsStructurallyValid,
    bool WasLiveValidated,
    string? SqlState,
    string? Message,
    int? ErrorPosition)
{
    public Guid ValidationRunId { get; init; }

    public LiveSqlValidationOutcome Outcome { get; init; } =
        LiveSqlValidationOutcome.NotRun;

    public LiveSqlValidationConfidence Confidence { get; init; } =
        LiveSqlValidationConfidence.None;

    public string? Detail { get; init; }

    public string? Hint { get; init; }

    public string? Where { get; init; }

    public string? SchemaName { get; init; }

    public string? TableName { get; init; }

    public string? ColumnName { get; init; }

    public string? ConstraintName { get; init; }

    public string? DataTypeName { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool IsRetryable { get; init; }

    public string ValidatedSqlHash { get; init; } = string.Empty;

    public DateTimeOffset? ValidatedAt { get; init; }

    public IReadOnlyList<InventoryObjectId> BlockingDependencies { get; init; } = [];
}

public enum LiveSqlValidationOutcome
{
    NotRun,
    Passed,
    Failed,
    BlockedByDependency,
    Manual,
    Unsupported,
    Cancelled
}

public enum LiveSqlValidationConfidence
{
    None,
    RollbackTransaction,
    DisposableDatabase
}

public sealed record ConversionArtifact(
    InventoryObjectId SourceObjectId,
    TargetObjectIdentifier TargetObjectId,
    string SourceDefinition,
    string PostgreSqlDefinition,
    ConversionClassification Classification,
    string RuleId,
    decimal Confidence,
    IReadOnlyList<InventoryFinding> Findings,
    IReadOnlyList<InventoryObjectId> Dependencies,
    IReadOnlyList<TargetObjectIdentifier> ReferencedTargetObjects,
    IReadOnlyList<string> RequiredExtensions,
    bool RequiresManualReview,
    IReadOnlyList<string> UnsupportedConstructs,
    SqlValidationResult Validation,
    DeploymentPhase DeploymentPhase,
    string ScriptFileName,
    string ContentHash);

public sealed record ConversionRun(
    Guid RunId,
    DateTimeOffset GeneratedAt,
    string SourceDatabase,
    PostgreSqlVersion TargetVersion,
    ConversionOptions Options,
    IReadOnlyList<IdentifierMappingEntry> IdentifierMappings,
    IReadOnlyList<TypeMappingResult> TypeMappings,
    IReadOnlyList<ConversionArtifact> Artifacts,
    IReadOnlyList<InventoryFinding> Findings,
    IReadOnlyList<string> RequiredExtensions,
    string EngineVersion)
{
    public bool RequiresManualReview => Artifacts.Any(item => item.RequiresManualReview);

    public IdentifierMappingSummary IdentifierMappingSummary =>
        IdentifierMappingSummary.Create(IdentifierMappings);

    public IdentifierMappingSetMetadata MappingSet { get; init; } =
        IdentifierMappingSetMetadata.Legacy;

    public BlockedDependencyReconciliation? PublicationReconciliation { get; init; }
}

public sealed record ConversionResult<TTarget>(
    TTarget? Target,
    ConversionClassification Classification,
    string RuleId,
    decimal Confidence,
    IReadOnlyList<InventoryFinding> Findings,
    IReadOnlyList<string> UnsupportedConstructs,
    bool RequiresManualReview)
{
    public IReadOnlyList<string> RequiredExtensions { get; init; } = [];
}
