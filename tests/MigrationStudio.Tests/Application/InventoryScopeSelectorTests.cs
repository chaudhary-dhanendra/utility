using MigrationStudio.Application.Discovery;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Tests.Application;

public sealed class InventoryScopeSelectorTests
{
    [Theory]
    [InlineData("dbo")]
    [InlineData("nrega_SK")]
    [InlineData("nrega")]
    [InlineData("custom_application")]
    public void UserScopePolicy_IncludesObjectsInUserSchemas(string schema)
    {
        var policy = new SqlServerUserObjectScopePolicy();

        Assert.True(policy.IsUserMigrationObject(
            Object(InventoryObjectType.Table, schema, "application_table", 1)));
    }

    [Theory]
    [InlineData("sys")]
    [InlineData("INFORMATION_SCHEMA")]
    [InlineData("guest")]
    [InlineData("db_owner")]
    [InlineData("db_datareader")]
    public void UserScopePolicy_ExcludesSystemSchemasAndRoleNamespaces(string schema)
    {
        var policy = new SqlServerUserObjectScopePolicy();

        Assert.False(policy.IsUserMigrationObject(
            Object(InventoryObjectType.Table, schema, "internal_object", 1)));
    }

    [Theory]
    [InlineData(InventoryObjectType.StoredProcedure)]
    [InlineData(InventoryObjectType.View)]
    [InlineData(InventoryObjectType.Function)]
    [InlineData(InventoryObjectType.Trigger)]
    public void UserScopePolicy_IncludesUserProgrammableObjects(InventoryObjectType objectType)
    {
        var policy = new SqlServerUserObjectScopePolicy();

        Assert.True(policy.IsUserMigrationObject(
            Object(objectType, "nrega_SK", "application_object", 1)));
    }

    [Fact]
    public void UserScopePolicy_ExcludesMicrosoftShippedObjects()
    {
        var policy = new SqlServerUserObjectScopePolicy();
        var shipped = Object(InventoryObjectType.StoredProcedure, "dbo", "sp_internal", 1) with
        {
            IsSystemObject = true
        };

        Assert.False(policy.IsUserMigrationObject(shipped));
    }

    [Fact]
    public void Apply_IncludesRequiredDependenciesAndParents()
    {
        var table = Object(InventoryObjectType.Table, "sales", "orders", 1);
        var view = Object(InventoryObjectType.View, "sales", "order_view", 2);
        var snapshot = TestInventory.CreateSnapshot([table, view]) with
        {
            Dependencies = [new InventoryDependency(view.Id, table.Id, DependencyKind.SqlExpression, table.QualifiedSourceName, true, false)]
        };
        var request = new InventoryDiscoveryRequest(
            new SqlServerConnectionOptions { Server = "fixture", Database = "fixture" },
            MigrationScopeMode.ManualObjectSelection,
            new HashSet<string>(),
            new HashSet<InventoryObjectId> { view.Id },
            new HashSet<InventoryObjectId>(),
            DependencyPolicy.IncludeRequiredDependencies,
            new DiscoveryOptions());

        var result = InventoryScopeSelector.Apply(snapshot, request);

        Assert.True(result.Objects.Single(item => item.Id == view.Id).IsIncluded);
        Assert.Equal(
            SelectionReason.RequiredDependency,
            result.Objects.Single(item => item.Id == table.Id).SelectionReason);
    }

    private static InventoryObject Object(
        InventoryObjectType type,
        string schema,
        string name,
        int sqlId)
    {
        var id = InventoryObjectId.Create("fixture", type, schema, name, sqlId);
        return new InventoryObject(
            id, "fixture", schema, name, $"[{schema}].[{name}]", type, sqlId, null, null, null, false,
            false, SelectionReason.None, 0, 0, [], InventoryClassification.ForObject(type), null, null,
            "hash", [], DiscoveryStatus.Discovered);
    }
}
