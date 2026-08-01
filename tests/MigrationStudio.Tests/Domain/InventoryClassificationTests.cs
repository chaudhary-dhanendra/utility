using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Domain;

public sealed class InventoryClassificationTests
{
    [Theory]
    [InlineData(InventoryObjectType.ServiceBrokerObject, ConversionClassification.Unsupported)]
    [InlineData(InventoryObjectType.Synonym, ConversionClassification.ManualConversion)]
    [InlineData(InventoryObjectType.View, ConversionClassification.AutomaticWithWarning)]
    [InlineData(InventoryObjectType.Table, ConversionClassification.Automatic)]
    public void ForObject_ClassifiesKnownObjectTypes(
        InventoryObjectType objectType,
        ConversionClassification expected) =>
        Assert.Equal(expected, InventoryClassification.ForObject(objectType));

    [Fact]
    public void ForObject_FlagsSpecializedTableAndIndexFeatures()
    {
        Assert.Equal(
            ConversionClassification.ManualConversion,
            InventoryClassification.ForObject(InventoryObjectType.Table, TableKind.Ledger));
        Assert.Equal(
            ConversionClassification.ManualConversion,
            InventoryClassification.ForObject(InventoryObjectType.Index, indexKind: IndexKind.Spatial));
    }
}
