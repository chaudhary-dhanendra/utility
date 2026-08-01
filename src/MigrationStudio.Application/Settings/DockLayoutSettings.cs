namespace MigrationStudio.Application.Settings;

public sealed record DockLayoutSettings
{
    public double ExplorerWidth { get; init; } = 280;

    public double InspectorWidth { get; init; } = 320;

    public double OutputHeight { get; init; } = 180;

    public bool IsExplorerVisible { get; init; } = true;

    public bool IsInspectorVisible { get; init; } = true;

    public bool IsOutputVisible { get; init; } = true;

    public DockLayoutSettings Normalize() => this with
    {
        ExplorerWidth = Math.Clamp(ExplorerWidth, 180, 700),
        InspectorWidth = Math.Clamp(InspectorWidth, 220, 700),
        OutputHeight = Math.Clamp(OutputHeight, 100, 500)
    };
}
