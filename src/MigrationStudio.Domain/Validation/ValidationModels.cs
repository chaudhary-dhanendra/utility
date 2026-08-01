using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Domain.Validation;

public enum ValidationLevel
{
    InventoryOnly,
    Structural,
    DataCounts,
    DataSampling,
    DataComprehensive,
    ProgrammableObject,
    Full
}

public enum ComparisonClassification
{
    Equivalent,
    EquivalentWithExpectedTransformation,
    Warning,
    Mismatch,
    Missing,
    Extra,
    NotComparable,
    ManualReview
}

public enum ValidationSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum ValidationCategory
{
    StructuralCompleteness,
    DataReconciliation,
    Constraints,
    ProgrammableObjects,
    Security,
    UnsupportedFeatures,
    ManualReviewCompletion
}

public enum ReadinessStatus
{
    Ready,
    ReadyWithWarnings,
    NotReady,
    Incomplete
}

public enum KeylessTableValidationStrategy
{
    AllColumnCanonicalMultisetHash,
    ConfiguredKeyColumns,
    DeterministicSample,
    CountAndAggregatesOnly,
    ManualReview
}

public enum CanonicalValueKind
{
    Null,
    Boolean,
    IntegralNumber,
    ExactNumber,
    FloatingPoint,
    Date,
    Time,
    Timestamp,
    TimestampWithTimeZone,
    Text,
    Binary,
    Uuid,
    Xml,
    Json,
    Spatial
}

public sealed record ValidationScope
{
    public IReadOnlySet<string> Schemas { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<InventoryObjectType> ObjectTypes { get; init; } = new HashSet<InventoryObjectType>();

    public IReadOnlySet<string> Tables { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool Includes(string schema, InventoryObjectType type, string qualifiedName) =>
        (Schemas.Count == 0 || Schemas.Contains(schema)) &&
        (ObjectTypes.Count == 0 || ObjectTypes.Contains(type)) &&
        (Tables.Count == 0 || type != InventoryObjectType.Table || Tables.Contains(qualifiedName));
}

public sealed record CanonicalComparisonOptions
{
    public double FloatingPointAbsoluteTolerance { get; init; } = 1e-9;

    public double FloatingPointRelativeTolerance { get; init; } = 1e-12;

    public int? DecimalScale { get; init; }

    public int TimePrecision { get; init; } = 6;

    public bool TrimFixedWidthTrailingSpaces { get; init; } = true;

    public bool CaseInsensitiveStrings { get; init; }

    public bool NormalizeJsonPropertyOrder { get; init; }

    public bool NormalizeXml { get; init; }

    public bool NormalizeTimestampsToUtc { get; init; } = true;
}

public sealed record CanonicalValue(
    CanonicalValueKind Kind,
    string Representation,
    bool IsSensitive = false);

public sealed record ValidationQuery(
    string Id,
    string Name,
    string SourceSql,
    string TargetSql,
    bool IsReadOnly,
    int TimeoutSeconds = 120,
    bool ContainsSensitiveValues = false);

public sealed record RoutineValidationTestCase
{
    public required string Id { get; init; }

    public required string Routine { get; init; }

    public IReadOnlyDictionary<string, object?> InputParameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ExpectedResultColumns { get; init; } = [];

    public string? ExpectedScalarCanonicalValue { get; init; }

    public IReadOnlyDictionary<string, string> ExpectedOutputParameters { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public bool SourceExecutionAllowed { get; init; }

    public bool TargetExecutionAllowed { get; init; }

    public bool IsReadOnly { get; init; }

    public bool RollbackTransaction { get; init; } = true;

    public int TimeoutSeconds { get; init; } = 120;

    public IReadOnlySet<string> SensitiveParameters { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ValidationConfiguration
{
    public ValidationLevel Level { get; init; } = ValidationLevel.Full;

    public ValidationScope Scope { get; init; } = new();

    public CanonicalComparisonOptions Canonical { get; init; } = new();

    public KeylessTableValidationStrategy KeylessTableStrategy { get; init; } =
        KeylessTableValidationStrategy.CountAndAggregatesOnly;

    public int SampleSize { get; init; } = 1000;

    public int ChunkSize { get; init; } = 10_000;

    public bool ValidateForeignKeyOrphans { get; init; } = true;

    public bool IncludeDistinctCounts { get; init; }

    public bool IncludeColumnAggregates { get; init; } = true;

    public IReadOnlyList<ValidationQuery> CustomQueries { get; init; } = [];

    public IReadOnlyList<RoutineValidationTestCase> RoutineTestCases { get; init; } = [];

    public IReadOnlyDictionary<ValidationCategory, decimal> CategoryWeights { get; init; } =
        DefaultWeights;

    public IReadOnlyDictionary<string, ValidationSeverity> SeverityOverrides { get; init; } =
        new Dictionary<string, ValidationSeverity>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<ValidationCategory, decimal> DefaultWeights { get; } =
        new Dictionary<ValidationCategory, decimal>
        {
            [ValidationCategory.StructuralCompleteness] = 25,
            [ValidationCategory.DataReconciliation] = 25,
            [ValidationCategory.Constraints] = 15,
            [ValidationCategory.ProgrammableObjects] = 15,
            [ValidationCategory.Security] = 5,
            [ValidationCategory.UnsupportedFeatures] = 5,
            [ValidationCategory.ManualReviewCompletion] = 10
        };
}

public sealed record ValidationFinding(
    string RuleId,
    ValidationCategory Category,
    ValidationSeverity Severity,
    ComparisonClassification Classification,
    string ObjectType,
    string SourceObject,
    string? TargetObject,
    string Summary,
    string? SourceDefinition = null,
    string? TargetDefinition = null,
    bool IsSensitive = false,
    bool IsOverridden = false,
    string? OverrideReason = null);

public sealed record ObjectComparison(
    string ObjectType,
    string SourceName,
    string TargetName,
    ComparisonClassification Classification,
    ValidationSeverity Severity,
    string Detail);

public sealed record ColumnDataMetric(
    string Column,
    long NullCount,
    string? Minimum,
    string? Maximum,
    string? Sum,
    string? Average,
    long? DistinctCount);

public sealed record TableDataComparison(
    string SourceTable,
    string TargetTable,
    long SourceRowCount,
    long TargetRowCount,
    string? SourceChecksum,
    string? TargetChecksum,
    bool IsOrderedChecksum,
    IReadOnlyList<ColumnDataMetric> SourceMetrics,
    IReadOnlyList<ColumnDataMetric> TargetMetrics,
    ComparisonClassification Classification,
    string Detail);

public sealed record SequenceValidationResult(
    string SourceSequence,
    string TargetSequence,
    decimal? CurrentValue,
    decimal? MaximumColumnValue,
    decimal Increment,
    decimal Minimum,
    decimal Maximum,
    bool IsCycling,
    decimal? ExpectedNextValue,
    bool WouldGenerateDuplicate,
    ComparisonClassification Classification);

public sealed record ValidationCategoryScore(
    ValidationCategory Category,
    decimal Weight,
    int ApplicableChecks,
    int PassedChecks,
    int WarningChecks,
    int BlockerChecks,
    decimal? Score,
    ReadinessStatus Status,
    string Explanation);

public sealed record ReadinessAssessment(
    ReadinessStatus OverallStatus,
    decimal? WeightedScore,
    IReadOnlyList<ValidationCategoryScore> Categories,
    IReadOnlyList<ValidationFinding> CriticalBlockers,
    string Explanation);

public sealed record ExecutedValidationQuery(
    string Id,
    string Name,
    string SourceSqlHash,
    string TargetSqlHash,
    TimeSpan Duration,
    bool Succeeded,
    string? Error);

public sealed record ValidationRun
{
    public required Guid RunId { get; init; }

    public Guid? MigrationRunId { get; init; }

    public Guid? DeploymentRunId { get; init; }

    public required string SourceSnapshotHash { get; init; }

    public required string TargetDatabaseIdentity { get; init; }

    public required ValidationConfiguration Configuration { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public IReadOnlyList<ObjectComparison> ObjectComparisons { get; init; } = [];

    public IReadOnlyList<TableDataComparison> DataComparisons { get; init; } = [];

    public IReadOnlyList<SequenceValidationResult> SequenceResults { get; init; } = [];

    public IReadOnlyList<ValidationFinding> Findings { get; init; } = [];

    public IReadOnlyList<ExecutedValidationQuery> QueriesExecuted { get; init; } = [];

    public required ReadinessAssessment Readiness { get; init; }
}

public sealed record TargetColumnMetadata(
    string Schema,
    string Table,
    string Name,
    int Ordinal,
    string DataType,
    int? MaximumLength,
    int? NumericPrecision,
    int? NumericScale,
    bool IsNullable,
    bool IsIdentity,
    bool IsGenerated,
    string? DefaultExpression,
    string? Comment = null);

public sealed record TargetObjectMetadata(
    string Schema,
    string Name,
    string ObjectType,
    string? Definition,
    string? DefinitionHash,
    bool IsValid = true,
    bool IsEnabled = true,
    string? Owner = null,
    string? Comment = null);

public sealed record TargetConstraintMetadata(
    string Schema,
    string Table,
    string Name,
    string ConstraintType,
    IReadOnlyList<string> Columns,
    string? ReferencedSchema,
    string? ReferencedTable,
    IReadOnlyList<string> ReferencedColumns,
    bool IsValidated,
    string? Definition);

public sealed record TargetIndexMetadata(
    string Schema,
    string Table,
    string Name,
    bool IsUnique,
    bool IsValid,
    IReadOnlyList<string> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? Predicate);

public sealed record TargetSequenceMetadata(
    string Schema,
    string Name,
    decimal CurrentValue,
    decimal Increment,
    decimal Minimum,
    decimal Maximum,
    bool IsCycling);

public sealed record TargetDatabaseSnapshot
{
    public required string Identity { get; init; }

    public IReadOnlyList<TargetObjectMetadata> Objects { get; init; } = [];

    public IReadOnlyList<TargetColumnMetadata> Columns { get; init; } = [];

    public IReadOnlyList<TargetConstraintMetadata> Constraints { get; init; } = [];

    public IReadOnlyList<TargetIndexMetadata> Indexes { get; init; } = [];

    public IReadOnlyList<TargetSequenceMetadata> Sequences { get; init; } = [];

    public IReadOnlyList<string> Roles { get; init; } = [];

    public IReadOnlyList<string> RoleMemberships { get; init; } = [];

    public IReadOnlyList<string> Privileges { get; init; } = [];
}
