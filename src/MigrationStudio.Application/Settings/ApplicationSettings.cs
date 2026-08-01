namespace MigrationStudio.Application.Settings;

public sealed record ApplicationSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public ThemeMode Theme { get; init; } = ThemeMode.System;

    public bool ConfirmBeforeExit { get; init; } = true;

    public int MaximumConcurrentOperations { get; init; } = 2;

    public ExperienceMode ExperienceMode { get; init; } = ExperienceMode.Simple;

    public DockLayoutSettings DockLayout { get; init; } = new();

    public ApplicationSettings Normalize() => this with
    {
        SchemaVersion = CurrentSchemaVersion,
        MaximumConcurrentOperations = Math.Clamp(MaximumConcurrentOperations, 1, 16),
        DockLayout = DockLayout.Normalize()
    };
}

public enum ExperienceMode
{
    Simple,
    Advanced
}
