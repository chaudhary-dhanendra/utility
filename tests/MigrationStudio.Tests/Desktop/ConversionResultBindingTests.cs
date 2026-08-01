using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MigrationStudio.Application.Operations;
using MigrationStudio.Application.Settings;
using MigrationStudio.Desktop.Converters;
using MigrationStudio.Desktop.ViewModels;
using MigrationStudio.Desktop.Views;
using MigrationStudio.Domain.Conversion;
using MigrationStudio.Domain.DataMigration;
using MigrationStudio.Domain.Inventory;
using MigrationStudio.Domain.Operations;
using MigrationStudio.Infrastructure;
using MigrationStudio.Infrastructure.Conversion;
using MigrationStudio.Infrastructure.Operations;
using MigrationStudio.Infrastructure.Security;

namespace MigrationStudio.Tests.Desktop;

[Collection("WPF binding tests")]
public sealed class ConversionResultBindingTests
{
    [Fact]
    public void ConversionResultView_BindsImmutableSourceSqlOneWayWithoutTraceErrors()
    {
        RunOnSta(() =>
        {
            var application = new System.Windows.Application();
            application.Resources.Add(
                "BooleanToVisibilityConverter",
                new BooleanToVisibilityConverter());
            application.Resources.Add(
                "InverseBooleanConverter",
                new InverseBooleanConverter());

            var trace = PresentationTraceSources.DataBindingSource;
            var previousLevel = trace.Switch.Level;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var listener = new TextWriterTraceListener(output);
            trace.Listeners.Add(listener);
            trace.Switch.Level = SourceLevels.Error;

            try
            {
                var artifact = CreateArtifact();
                var artifactViewModel = new ConversionArtifactViewModel(artifact);
                var view = new WorkspaceView();
                var sourceSql = Assert.IsType<TextBox>(
                    view.FindName("ConversionSourceSqlTextBox"));
                var expression = sourceSql.GetBindingExpression(TextBox.TextProperty);
                Assert.NotNull(expression);
                var configuredBinding = expression.ParentBinding;
                BindingOperations.SetBinding(
                    sourceSql,
                    TextBox.TextProperty,
                    new Binding(configuredBinding.Path.Path)
                    {
                        Mode = configuredBinding.Mode,
                        Source = new ConversionBindingContext(artifactViewModel)
                    });
                expression = sourceSql.GetBindingExpression(TextBox.TextProperty);
                Assert.NotNull(expression);
                expression.UpdateTarget();
                listener.Flush();

                Assert.True(sourceSql.IsReadOnly);
                Assert.Equal(BindingMode.OneWay, expression.ParentBinding.Mode);
                Assert.True(
                    string.Equals(
                        artifact.SourceDefinition,
                        sourceSql.Text,
                        StringComparison.Ordinal),
                    output.ToString());
                Assert.DoesNotContain(
                    "binding cannot work on the read-only property",
                    output.ToString(),
                    StringComparison.OrdinalIgnoreCase);
                Assert.IsType<TextBox>(view.FindName("PostgreSqlTargetHostTextBox"));
                Assert.IsType<TextBox>(view.FindName("PostgreSqlTargetPortTextBox"));
                Assert.IsType<TextBox>(view.FindName("PostgreSqlTargetDatabaseTextBox"));
                Assert.IsType<TextBox>(view.FindName("PostgreSqlTargetUsernameTextBox"));
                var connection = new PostgreSqlConnectionViewModel(
                    new SensitiveDataRedactor(),
                    NullLogger<PostgreSqlConnectionViewModel>.Instance);
                var passwordBox = Assert.IsType<PasswordBox>(
                    view.FindName("PostgreSqlTargetPasswordBox"));
                var targetBinding = passwordBox.GetBindingExpression(
                    FrameworkElement.TagProperty);
                Assert.NotNull(targetBinding);
                Assert.Equal(
                    nameof(PasswordBindingContext.PostgreSqlTarget),
                    targetBinding.ParentBinding.Path.Path);
                passwordBox.Tag = connection;
                passwordBox.Password = "wpf-forwarding-fixture";

                Assert.Equal("wpf-forwarding-fixture", connection.Password);
                Assert.IsType<Button>(
                    view.FindName("TestPostgreSqlTargetConnectionButton"));
                output.GetStringBuilder().Clear();
                AssertDataMigrationTablePlan(view, output, listener);
            }
            finally
            {
                trace.Listeners.Remove(listener);
                trace.Switch.Level = previousLevel;
                application.Shutdown();
            }
        });
    }

    [Fact]
    public void GeneratedSqlChange_RaisesPropertyChangedForCalculatedIsEdited()
    {
        var viewModel = new ConversionArtifactViewModel(CreateArtifact());
        var changed = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changed.Add(args.PropertyName);
            }
        };

        viewModel.GeneratedSql += Environment.NewLine + "-- reviewed";

        Assert.True(viewModel.IsEdited);
        Assert.Contains(nameof(ConversionArtifactViewModel.GeneratedSql), changed);
        Assert.Contains(nameof(ConversionArtifactViewModel.IsEdited), changed);
    }

    [Fact]
    public void GeneratedSqlChange_InvalidatesValidationAndContentHashWithoutMakingExecutableArtifactManual()
    {
        var artifact = CreateArtifact() with
        {
            Validation = new SqlValidationResult(true, true, null, null, null)
            {
                Outcome = LiveSqlValidationOutcome.Passed
            }
        };
        var viewModel = new ConversionArtifactViewModel(artifact);

        viewModel.GeneratedSql += Environment.NewLine + "-- corrected";
        var updated = viewModel.ToArtifact();

        Assert.NotEqual(artifact.ContentHash, updated.ContentHash);
        Assert.Equal(LiveSqlValidationOutcome.NotRun, updated.Validation.Outcome);
        Assert.False(updated.Validation.WasLiveValidated);
        Assert.Equal(artifact.Classification, updated.Classification);
        Assert.Equal(artifact.RequiresManualReview, updated.RequiresManualReview);
    }

    private static void AssertDataMigrationTablePlan(
        WorkspaceView view,
        StringWriter output,
        TextWriterTraceListener listener)
    {
        var grid = Assert.IsType<DataGrid>(
            view.FindName("DataMigrationTablePlanGrid"));
        var parent = Assert.IsType<Grid>(grid.Parent);
        parent.Children.Remove(grid);
        BindingOperations.ClearBinding(grid, DataGrid.ItemsSourceProperty);
        BindingOperations.ClearBinding(grid, DataGrid.SelectedItemProperty);
        grid.ItemsSource =
        new[]
        {
            new DataMigrationTableRowViewModel(new TableLoadPlan(
                new InventoryObjectId(Guid.NewGuid()),
                "nrega_SK",
                "verify_observe1819",
                "nrega_sk",
                "verify_observe1819",
                42,
                [],
                ["id"],
                "id",
                null,
                DataTransferStrategy.PostgreSqlBinaryCopy,
                TargetPreparationStrategy.Append,
                true,
                true,
                1,
                1,
                true,
                false,
                null,
                "binding-smoke"))
        };

        var window = new Window
        {
            Content = grid,
            Width = 1200,
            Height = 400,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None
        };
        window.Show();
        grid.UpdateLayout();
        listener.Flush();

        Assert.True(grid.IsReadOnly);
        Assert.Single(grid.Items);
        AssertColumnIsOneWay(grid, nameof(DataMigrationTableRowViewModel.IsResumable));
        AssertColumnIsOneWay(grid, nameof(DataMigrationTableRowViewModel.IsSensitive));
        Assert.Equal(string.Empty, output.ToString());
        window.Close();
    }

    [Fact]
    public async Task PresentationFailure_DoesNotFailCompletedConversionOperationOrLoseArtifacts()
    {
        var monitor = new OperationMonitor();
        var settings = new TestSettingsService();
        var session = new ConversionSession();
        var run = CreateRun();
        using var service = new BackgroundOperationService(
            Options.Create(new InfrastructureOptions { OperationQueueCapacity = 4 }),
            settings,
            monitor,
            NullLogger<BackgroundOperationService>.Instance);
        await service.StartAsync(CancellationToken.None);

        await service.EnqueueAsync(new BackgroundOperationDefinition(
            "Convert binding fixture",
            (_, _) =>
            {
                var failures = ConversionCompletionBoundary.Execute(
                    () => session.SetCurrent(run),
                    () => throw new InvalidOperationException("Simulated WPF binding failure."));
                Assert.Single(failures);
                return ValueTask.CompletedTask;
            }));

        var completed = await WaitForStateAsync(monitor, OperationState.Completed);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(OperationState.Completed, completed.State);
        var preserved = Assert.IsType<ConversionRun>(session.Current);
        Assert.Same(run, preserved);
        Assert.Single(preserved.Artifacts);
    }

    private static ConversionRun CreateRun() =>
        new ConversionRun(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            "vbgramg",
            new PostgreSqlVersion(18),
            new ConversionOptions(),
            [],
            [],
            [CreateArtifact()],
            [],
            [],
            "test")
        {
            MappingSet = new IdentifierMappingSetMetadata(
                Guid.NewGuid(),
                IdentifierMappingSchema.CurrentVersion,
                DateTimeOffset.UtcNow,
                false,
                0,
                0,
                0,
                0)
        };

    private static ConversionArtifact CreateArtifact() =>
        new(
            new InventoryObjectId(Guid.NewGuid()),
            new TargetObjectIdentifier("Table", "public", "customer"),
            "CREATE TABLE dbo.Customer (CustomerId int NOT NULL);",
            "CREATE TABLE public.customer (customer_id integer NOT NULL);",
            ConversionClassification.Automatic,
            "TEST.TABLE",
            1m,
            [],
            [],
            [],
            [],
            false,
            [],
            new SqlValidationResult(true, false, null, null, null),
            DeploymentPhase.Tables,
            "05_Tables.sql",
            "binding-fixture");

    private static async Task<OperationSnapshot> WaitForStateAsync(
        OperationMonitor monitor,
        OperationState state)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var operation = monitor.Operations.FirstOrDefault(item => item.State == state);
            if (operation is not null)
            {
                return operation;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Operation did not reach {state}.");
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

    private static void AssertColumnIsOneWay(DataGrid grid, string propertyName)
    {
        var column = Assert.IsType<DataGridCheckBoxColumn>(
            grid.Columns.Single(item =>
                item is DataGridCheckBoxColumn checkBox &&
                checkBox.Binding is Binding binding &&
                binding.Path.Path == propertyName));
        var binding = Assert.IsType<Binding>(column.Binding);
        Assert.Equal(BindingMode.OneWay, binding.Mode);
        Assert.True(column.IsReadOnly);
    }

    public sealed record ConversionBindingContext(
        ConversionArtifactViewModel SelectedConversionArtifact);

    public sealed record PasswordBindingContext(
        PostgreSqlConnectionViewModel PostgreSqlTarget);

    private sealed class TestSettingsService : ISettingsService
    {
        public ApplicationSettings Current { get; } =
            new() { MaximumConcurrentOperations = 1 };

        public event EventHandler<ApplicationSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task SaveAsync(
            ApplicationSettings settings,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

[CollectionDefinition("WPF binding tests", DisableParallelization = true)]
public sealed class WpfBindingTestGroup;
