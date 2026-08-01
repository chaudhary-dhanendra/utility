using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationStudio.Application.Settings;
using MigrationStudio.Application.Errors;

namespace MigrationStudio.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IThemeService _themeService;
    private readonly IErrorPresenter _errorPresenter;

    [ObservableProperty]
    private ThemeMode _selectedTheme;

    [ObservableProperty]
    private bool _confirmBeforeExit;

    [ObservableProperty]
    private int _maximumConcurrentOperations;

    [ObservableProperty]
    private ExperienceMode _selectedExperienceMode;

    [ObservableProperty]
    private string _saveStatus = string.Empty;

    public SettingsViewModel(
        ISettingsService settings,
        IThemeService themeService,
        IErrorPresenter errorPresenter)
    {
        _settings = settings;
        _themeService = themeService;
        _errorPresenter = errorPresenter;
        SelectedTheme = settings.Current.Theme;
        ConfirmBeforeExit = settings.Current.ConfirmBeforeExit;
        MaximumConcurrentOperations = settings.Current.MaximumConcurrentOperations;
        SelectedExperienceMode = settings.Current.ExperienceMode;
    }

    public string Title { get; } = "Settings";

    public IReadOnlyList<ThemeMode> Themes { get; } = Enum.GetValues<ThemeMode>();

    public IReadOnlyList<ExperienceMode> ExperienceModes { get; } = Enum.GetValues<ExperienceMode>();

    [RelayCommand]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var updated = _settings.Current with
            {
                Theme = SelectedTheme,
                ConfirmBeforeExit = ConfirmBeforeExit,
                MaximumConcurrentOperations = MaximumConcurrentOperations,
                ExperienceMode = SelectedExperienceMode
            };

            await _settings.SaveAsync(updated, cancellationToken);
            _themeService.Apply(updated.Theme);
            SaveStatus = "Settings saved.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SaveStatus = "Settings could not be saved.";
            _errorPresenter.ShowRecoverable("Settings", exception.Message);
        }
    }
}
