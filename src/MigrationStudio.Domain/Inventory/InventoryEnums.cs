namespace MigrationStudio.Domain.Inventory;

public enum MigrationScopeMode
{
    CompleteDatabase,
    SelectedSchemas,
    ExcelSelectedTables,
    ManualObjectSelection
}

public enum ConversionClassification
{
    Automatic,
    AutomaticWithWarning,
    ManualConversion,
    Unsupported
}

public enum FindingSeverity
{
    Information,
    Warning,
    Error,
    Critical
}

public enum InventoryObjectType
{
    Database,
    Schema,
    Table,
    ExternalTable,
    Column,
    View,
    StoredProcedure,
    Function,
    Trigger,
    DatabaseTrigger,
    ServerTrigger,
    Sequence,
    UserDefinedType,
    TableType,
    Synonym,
    PrimaryKey,
    UniqueConstraint,
    CheckConstraint,
    ForeignKey,
    DefaultConstraint,
    Index,
    Statistics,
    SecurityPolicy,
    User,
    Role,
    ApplicationRole,
    Permission,
    PartitionFunction,
    PartitionScheme,
    FullTextCatalog,
    FullTextIndex,
    ServiceBrokerObject,
    Assembly,
    SqlAgentJob,
    ReplicationObject,
    ExternalDataSource,
    ExternalFileFormat,
    DatabaseScopedCredential,
    EncryptionKey,
    Certificate,
    Unknown
}

public enum TableKind
{
    Ordinary,
    MemoryOptimized,
    FileTable,
    TemporalCurrent,
    TemporalHistory,
    External,
    GraphNode,
    GraphEdge,
    Ledger,
    Stretch
}

public enum ModuleKind
{
    View,
    StoredProcedure,
    ScalarFunction,
    InlineTableValuedFunction,
    MultiStatementTableValuedFunction,
    ClrProcedure,
    ClrScalarFunction,
    ClrTableValuedFunction,
    AggregateFunction,
    DmlTrigger,
    DdlTrigger,
    ServerTrigger
}

public enum ConstraintKind
{
    PrimaryKey,
    Unique,
    Check,
    ForeignKey,
    Default
}

public enum IndexKind
{
    Heap,
    Clustered,
    NonClustered,
    ClusteredColumnstore,
    NonClusteredColumnstore,
    Xml,
    Spatial,
    Hash,
    FullText
}

public enum SelectionReason
{
    None,
    CompleteDatabase,
    SelectedSchema,
    ExcelMatch,
    ManualSelection,
    RequiredDependency,
    IncludedDependent,
    ParentObject
}

public enum DiscoveryStatus
{
    Discovered,
    DefinitionUnavailable,
    PartiallyDiscovered,
    PermissionDenied,
    UnsupportedByServerVersion,
    Unresolved
}

public enum DependencyKind
{
    ForeignKey,
    SqlExpression,
    ParentChild,
    Type,
    Sequence,
    ComputedColumn,
    Default,
    CheckConstraint,
    SecurityPolicy,
    Synonym,
    CrossDatabase,
    LinkedServer,
    External,
    ParsedFallback
}

public enum DependencyPolicy
{
    SelectedOnly,
    IncludeRequiredDependencies,
    IncludeDependenciesAndDependents
}
