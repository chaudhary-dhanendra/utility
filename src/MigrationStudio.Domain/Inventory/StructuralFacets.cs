namespace MigrationStudio.Domain.Inventory;

public sealed record TableInventory(
    InventoryObjectId ObjectId,
    TableKind Kind,
    bool IsMemoryOptimized,
    string? Durability,
    bool IsFileTable,
    int TemporalType,
    InventoryObjectId? HistoryTableId,
    bool IsExternal,
    bool IsNode,
    bool IsEdge,
    bool IsLedger,
    bool IsRemoteDataArchiveEnabled,
    bool LockEscalationDisabled,
    bool LockOnBulkLoad,
    long RowCountEstimate,
    long ReservedBytes,
    long UsedBytes,
    IReadOnlyList<PartitionInventory> Partitions);

public sealed record ColumnInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId ParentObjectId,
    int ColumnId,
    int OrdinalPosition,
    string Name,
    string SystemTypeName,
    string UserTypeName,
    string TypeSchema,
    short MaximumLength,
    byte Precision,
    byte Scale,
    string? Collation,
    bool IsNullable,
    bool IsIdentity,
    decimal? IdentitySeed,
    decimal? IdentityIncrement,
    decimal? IdentityLastValue,
    bool IsIdentityNotForReplication,
    bool IsComputed,
    string? ComputedDefinition,
    bool IsComputedPersisted,
    bool? IsComputedDeterministic,
    bool IsSparse,
    bool IsColumnSet,
    bool IsRowGuidColumn,
    bool IsFileStream,
    int GeneratedAlwaysType,
    bool IsHidden,
    bool IsMasked,
    string? MaskingFunction,
    string? EncryptionType,
    string? EncryptionAlgorithm,
    string? ColumnEncryptionKey,
    string? XmlSchemaCollection,
    string? DefaultConstraintName,
    string? DefaultDefinition,
    string? RuleName,
    IReadOnlyList<ExtendedProperty> ExtendedProperties);

public sealed record ConstraintInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId TableObjectId,
    ConstraintKind Kind,
    string Name,
    IReadOnlyList<ConstraintColumn> Columns,
    InventoryObjectId? ReferencedTableObjectId,
    IReadOnlyList<ConstraintColumn> ReferencedColumns,
    string? Definition,
    string? DeleteAction,
    string? UpdateAction,
    bool IsDisabled,
    bool IsNotTrusted,
    bool IsNotForReplication,
    bool IsClustered,
    string? DataSpaceName,
    int FillFactor,
    string? FilterDefinition);

public sealed record ConstraintColumn(
    int Ordinal,
    string Name,
    bool IsDescending);

public sealed record IndexInventory(
    InventoryObjectId ObjectId,
    InventoryObjectId TableObjectId,
    int IndexId,
    string Name,
    IndexKind Kind,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsUniqueConstraint,
    bool IsDisabled,
    bool IsFiltered,
    string? FilterDefinition,
    int FillFactor,
    string? DataSpaceName,
    IReadOnlyList<IndexColumn> Columns,
    IReadOnlyList<PartitionInventory> Partitions,
    ConversionClassification Classification);

public sealed record IndexColumn(
    int KeyOrdinal,
    string Name,
    bool IsDescending,
    bool IsIncluded);

public sealed record PartitionInventory(
    int PartitionNumber,
    long RowCount,
    string Compression,
    string? DataSpaceName,
    string? PartitionScheme,
    string? PartitionColumn);

public sealed record ModuleInventory(
    InventoryObjectId ObjectId,
    ModuleKind Kind,
    bool UsesAnsiNulls,
    bool UsesQuotedIdentifier,
    bool IsSchemaBound,
    bool IsRecompiled,
    bool IsEncrypted,
    bool IsNativeCompiled,
    string? ExecuteAsPrincipal,
    bool ContainsDynamicSql,
    bool UsesTemporaryTables,
    bool ContainsTransactionControl,
    bool ContainsErrorHandling,
    IReadOnlyList<ModuleParameterInventory> Parameters,
    IReadOnlyList<ColumnInventory> ResultColumns);

public sealed record ModuleParameterInventory(
    int ParameterId,
    string Name,
    string TypeSchema,
    string TypeName,
    short MaximumLength,
    byte Precision,
    byte Scale,
    bool IsOutput,
    bool HasDefaultValue,
    string? DefaultValue,
    bool IsReadOnly,
    bool IsTableType);

public sealed record SequenceInventory(
    InventoryObjectId ObjectId,
    string TypeSchema,
    string TypeName,
    decimal StartValue,
    decimal Increment,
    decimal MinimumValue,
    decimal MaximumValue,
    bool IsCycling,
    int CacheSize,
    decimal? CurrentValue,
    bool IsExhausted);

public sealed record UserDefinedTypeInventory(
    InventoryObjectId ObjectId,
    string Kind,
    string? BaseTypeSchema,
    string? BaseTypeName,
    bool IsNullable,
    bool IsAssemblyType,
    InventoryObjectId? AssemblyObjectId,
    IReadOnlyList<ColumnInventory> TableTypeColumns);

public sealed record SynonymInventory(
    InventoryObjectId ObjectId,
    string BaseObjectName,
    string? ServerName,
    string? DatabaseName,
    string? SchemaName,
    string? ObjectName,
    bool IsLinkedServerReference,
    bool IsCrossDatabaseReference);
