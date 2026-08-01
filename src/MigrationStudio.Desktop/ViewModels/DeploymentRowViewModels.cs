using CommunityToolkit.Mvvm.ComponentModel;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.Deployment;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class DeploymentPhaseRowViewModel : ObservableObject
{
    public DeploymentPhaseRowViewModel(DeploymentPhase phase, bool isSelected)
    {
        Phase = phase;
        IsSelected = isSelected;
    }

    public DeploymentPhase Phase { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private DeploymentObjectStatus _status;
    [ObservableProperty] private int _completed;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private int _skipped;
}
