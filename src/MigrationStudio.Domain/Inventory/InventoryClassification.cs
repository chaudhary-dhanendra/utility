namespace MigrationStudio.Domain.Inventory;

public static class InventoryClassification
{
    public static ConversionClassification ForObject(
        InventoryObjectType objectType,
        TableKind? tableKind = null,
        IndexKind? indexKind = null,
        ModuleKind? moduleKind = null)
    {
        if (objectType is InventoryObjectType.ServiceBrokerObject or
            InventoryObjectType.ReplicationObject or
            InventoryObjectType.SqlAgentJob or
            InventoryObjectType.ServerTrigger)
        {
            return ConversionClassification.Unsupported;
        }

        if (objectType is InventoryObjectType.Synonym or
            InventoryObjectType.Assembly or
            InventoryObjectType.EncryptionKey or
            InventoryObjectType.Certificate or
            InventoryObjectType.ExternalDataSource or
            InventoryObjectType.ExternalFileFormat or
            InventoryObjectType.SecurityPolicy)
        {
            return ConversionClassification.ManualConversion;
        }

        if (tableKind is TableKind.MemoryOptimized or TableKind.FileTable or TableKind.External or
            TableKind.GraphNode or TableKind.GraphEdge or TableKind.Ledger or TableKind.Stretch)
        {
            return ConversionClassification.ManualConversion;
        }

        if (indexKind is IndexKind.Xml or IndexKind.Spatial or IndexKind.Hash or IndexKind.FullText)
        {
            return ConversionClassification.ManualConversion;
        }

        if (indexKind is IndexKind.ClusteredColumnstore or IndexKind.NonClusteredColumnstore ||
            moduleKind is ModuleKind.ClrProcedure or ModuleKind.ClrScalarFunction or
                ModuleKind.ClrTableValuedFunction or ModuleKind.AggregateFunction)
        {
            return ConversionClassification.ManualConversion;
        }

        return objectType is InventoryObjectType.View or
            InventoryObjectType.StoredProcedure or
            InventoryObjectType.Function or
            InventoryObjectType.Trigger or
            InventoryObjectType.DatabaseTrigger
            ? ConversionClassification.AutomaticWithWarning
            : ConversionClassification.Automatic;
    }
}
