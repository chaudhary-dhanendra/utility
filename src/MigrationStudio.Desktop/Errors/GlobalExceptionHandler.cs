using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using MigrationStudio.Application.Errors;

namespace MigrationStudio.Desktop.Errors;

public sealed class GlobalExceptionHandler(
    ILogger<GlobalExceptionHandler> logger,
    IErrorPresenter presenter)
{
    private int _isHandlingFatalError;

    public void Attach(System.Windows.Application application)
    {
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    public void Detach(System.Windows.Application application)
    {
        application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var correlationId = Guid.NewGuid();
        GlobalErrorLog.UiException(logger, correlationId, args.Exception);
        if (IsUnrecoverable(args.Exception))
        {
            presenter.ShowFatal(
                "Migration Studio",
                $"Migration Studio encountered an unrecoverable error and must close. Correlation ID: {correlationId}");
            args.Handled = false;
            return;
        }
        presenter.ShowRecoverable(
            "Migration Studio",
            $"The operation could not be completed. Correlation ID: {correlationId}");
        args.Handled = true;
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        GlobalErrorLog.UnobservedException(logger, Guid.NewGuid(), args.Exception);
        args.SetObserved();
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        if (Interlocked.Exchange(ref _isHandlingFatalError, 1) != 0)
        {
            return;
        }

        var exception = args.ExceptionObject as Exception;
        var correlationId = Guid.NewGuid();
        GlobalErrorLog.FatalException(logger, correlationId, exception);
        presenter.ShowFatal(
            "Migration Studio",
            $"Migration Studio encountered a fatal error and must close. Correlation ID: {correlationId}");
    }

    private static bool IsUnrecoverable(Exception exception) =>
        exception is OutOfMemoryException or AccessViolationException or BadImageFormatException;
}

internal static partial class GlobalErrorLog
{
    [LoggerMessage(2000, LogLevel.Error, "An unhandled UI exception occurred. CorrelationId={CorrelationId}")]
    public static partial void UiException(ILogger logger, Guid correlationId, Exception exception);

    [LoggerMessage(2001, LogLevel.Error, "An unobserved background exception occurred. CorrelationId={CorrelationId}")]
    public static partial void UnobservedException(ILogger logger, Guid correlationId, Exception exception);

    [LoggerMessage(2002, LogLevel.Critical, "A fatal application exception occurred. CorrelationId={CorrelationId}")]
    public static partial void FatalException(ILogger logger, Guid correlationId, Exception? exception);
}
