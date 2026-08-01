using CommunityToolkit.Mvvm.ComponentModel;
using MigrationStudio.Domain.Inventory;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class InventoryObjectRowViewModel(InventoryObject item) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = item.IsIncluded;

    public InventoryObject Item { get; } = item;

    public string Name => Item.QualifiedSourceName;

    public string Type => Item.ObjectType.ToString();

    public string Classification => Item.ConversionClassification.ToString();

    public string SelectionReason => Item.SelectionReason.ToString();

    public int Dependencies => Item.DependencyCount;

    public int Dependents => Item.DependentCount;
}
