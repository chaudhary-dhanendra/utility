namespace MigrationStudio.Domain.Inventory;

public sealed record DatabaseMetadata(
    string ProductVersion,
    string ProductLevel,
    string Edition,
    int EngineEdition,
    string DatabaseName,
    int DatabaseId,
    string? Owner,
    int CompatibilityLevel,
    string Collation,
    string ContainmentType,
    string RecoveryModel,
    bool IsReadOnly,
    string SnapshotIsolationState,
    bool IsReadCommittedSnapshotOn,
    bool IsAnsiNullDefaultOn,
    bool IsAnsiNullsOn,
    bool IsAnsiPaddingOn,
    bool IsAnsiWarningsOn,
    bool IsQuotedIdentifierOn,
    bool IsRecursiveTriggersOn,
    bool IsTrustworthyOn,
    bool IsBrokerEnabled,
    bool IsChangeTrackingEnabled,
    bool IsEncrypted,
    string QueryStoreState,
    IReadOnlyList<DatabaseScopedConfiguration> ScopedConfigurations,
    IReadOnlyList<DatabaseFileMetadata> Files,
    IReadOnlyList<FilegroupMetadata> Filegroups,
    IReadOnlyDictionary<string, string?> Options);

public sealed record DatabaseScopedConfiguration(
    string Name,
    string Value,
    string? SecondaryValue,
    bool IsValueDefault);

public sealed record DatabaseFileMetadata(
    int FileId,
    string LogicalName,
    string? PhysicalName,
    string FileType,
    string DataSpaceName,
    long SizeBytes,
    long? UsedBytes,
    bool IsPercentGrowth,
    long Growth,
    long MaxSizeBytes,
    string State);

public sealed record FilegroupMetadata(
    int DataSpaceId,
    string Name,
    bool IsDefault,
    bool IsReadOnly,
    string TypeDescription,
    int FileCount);
