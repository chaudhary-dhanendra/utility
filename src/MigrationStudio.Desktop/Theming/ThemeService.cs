using System.Windows;
using Microsoft.Win32;
using MigrationStudio.Application.Settings;

namespace MigrationStudio.Desktop.Theming;

public sealed class ThemeService : IThemeService
{
    private const string ThemeMarker = "Themes/";

    public ThemeMode EffectiveTheme { get; private set; } = ThemeMode.Light;

    public void Apply(ThemeMode requestedTheme)
    {
        EffectiveTheme = requestedTheme == ThemeMode.System ? ReadSystemTheme() : requestedTheme;
        var source = new Uri(
            EffectiveTheme == ThemeMode.Dark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
            UriKind.Relative);

        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(
            dictionary => dictionary.Source?.OriginalString.Contains(ThemeMarker, StringComparison.OrdinalIgnoreCase) == true);

        if (existing is not null)
        {
            dictionaries.Remove(existing);
        }

        dictionaries.Add(new ResourceDictionary { Source = source });
    }

    private static ThemeMode ReadSystemTheme()
    {
        const string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        var useLightTheme = key?.GetValue("AppsUseLightTheme") as int?;
        return useLightTheme == 0 ? ThemeMode.Dark : ThemeMode.Light;
    }
}
