using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.Converters;
using MigrationStudio.Desktop.Views;

namespace MigrationStudio.Tests.Desktop;

[Collection("WPF binding tests")]
public sealed class MigrationWizardViewTests
{
    private static readonly string[] ExpectedPanels =
    [
        "ConnectPanel", "SelectPanel", "AnalyzePanel", "ConvertPanel",
        "DeployPanel", "MigratePanel", "ValidatePanel", "FinishPanel"
    ];

    [Fact]
    public void ApplicationSettings_DefaultToSimpleExperience()
    {
        var settings = new ApplicationSettings().Normalize();

        Assert.Equal(ApplicationSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(ExperienceMode.Simple, settings.ExperienceMode);
    }

    [Fact]
    public void SimpleWizard_RendersEightStepsWithoutInventoryFileControlsOrBindingErrors()
    {
        RunOnSta(() =>
        {
            var trace = PresentationTraceSources.DataBindingSource;
            var previousLevel = trace.Switch.Level;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var listener = new TextWriterTraceListener(output);
            trace.Listeners.Add(listener);
            trace.Switch.Level = SourceLevels.Error;

            try
            {
                var view = new MigrationWizardView();
                var window = new Window
                {
                    Content = view,
                    Width = 1400,
                    Height = 900,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None
                };
                window.Show();
                view.UpdateLayout();
                listener.Flush();

                foreach (var panel in ExpectedPanels)
                {
                    Assert.IsType<Grid>(view.FindName(panel));
                }

                var cancel = Assert.IsType<Button>(
                    view.FindName("CancelActiveOperationButton"));
                var cancelContext = new CancelVisibilityContext
                {
                    IsCancelVisible = true,
                    CancelActionText = "Cancel conversion"
                };
                view.DataContext = cancelContext;
                cancel.GetBindingExpression(UIElement.VisibilityProperty)?.UpdateTarget();
                cancel.GetBindingExpression(ContentControl.ContentProperty)?.UpdateTarget();
                Assert.Equal(Visibility.Visible, cancel.Visibility);
                Assert.Equal(
                    nameof(CancelVisibilityContext.CancelActionText),
                    cancel.GetBindingExpression(ContentControl.ContentProperty)?
                        .ParentBinding.Path.Path);

                cancelContext.IsCancelVisible = false;
                Dispatcher.CurrentDispatcher.Invoke(
                    DispatcherPriority.DataBind,
                    new Action(() => { }));
                view.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, cancel.Visibility);

                var buttonLabels = Descendants<Button>(view)
                    .Select(item => item.Content?.ToString() ?? string.Empty)
                    .ToArray();
                Assert.DoesNotContain("Open Inventory", buttonLabels);
                Assert.DoesNotContain("Save Inventory", buttonLabels);
                Assert.DoesNotContain("Select Package", buttonLabels);
                Assert.DoesNotContain("Export Package", buttonLabels);
                Assert.Contains("Convert and validate", buttonLabels);
                Assert.Contains(
                    Descendants<ProgressBar>(view),
                    item => item.GetBindingExpression(RangeBase.ValueProperty)?
                        .ParentBinding.Path.Path == "Workspace.LiveValidationProgress");
                var metricBindings = Descendants<TextBlock>(view)
                    .Select(item => item.GetBindingExpression(TextBlock.TextProperty)?
                        .ParentBinding.Path.Path)
                    .Where(item => item is not null)
                    .ToHashSet(StringComparer.Ordinal);
                Assert.Contains("SelectedObjectCount", metricBindings);
                Assert.Contains("ConvertedObjectCount", metricBindings);
                Assert.Contains("PackagedObjectCount", metricBindings);
                Assert.Contains("ExecutableObjectCount", metricBindings);
                Assert.Contains("ManualReviewObjectCount", metricBindings);
                Assert.Contains("UnsupportedObjectCount", metricBindings);
                Assert.DoesNotContain(
                    "binding cannot work on the read-only property",
                    output.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                window.Close();
            }
            finally
            {
                trace.Listeners.Remove(listener);
                trace.Switch.Level = previousLevel;
            }
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public sealed class CancelVisibilityContext : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isCancelVisible;

        public bool IsCancelVisible
        {
            get => _isCancelVisible;
            set
            {
                if (_isCancelVisible == value)
                {
                    return;
                }

                _isCancelVisible = value;
                PropertyChanged?.Invoke(
                    this,
                    new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCancelVisible)));
            }
        }

        public string CancelActionText { get; init; } = string.Empty;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
