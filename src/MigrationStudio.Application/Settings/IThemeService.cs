namespace MigrationStudio.Application.Settings;

public interface IThemeService
{
    ThemeMode EffectiveTheme { get; }

    void Apply(ThemeMode requestedTheme);
}
