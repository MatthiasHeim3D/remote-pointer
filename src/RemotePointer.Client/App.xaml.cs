using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RemotePointer.Client.Services;
using RemotePointer.Client.Views;

namespace RemotePointer.Client;

public partial class App : System.Windows.Application
{
    private IClientAuditLog? auditLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        auditLog = new JsonFileClientAuditLog();
        auditLog.Write(ClientAuditEvent.ApplicationStarted);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            var window = new MainWindow(auditLog);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            auditLog.Write(
                exception is InvalidOperationException or JsonException
                    ? ClientAuditEvent.ConfigurationRejected
                    : ClientAuditEvent.UnhandledException,
                ClientAuditLevel.Error,
                exception: exception);
            MessageBox.Show(
                "Remote Pointer could not start because its configuration or local state is invalid. Check the audit log under LocalAppData\\RemotePointer\\Logs.",
                "Remote Pointer startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        auditLog?.Write(ClientAuditEvent.ApplicationStopped);
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        _ = sender;
        auditLog?.Write(
            ClientAuditEvent.UnhandledException,
            ClientAuditLevel.Error,
            exception: e.Exception);
        e.Handled = true;
        MessageBox.Show(
            "Remote Pointer encountered an unexpected error and must close. The connection can be recovered when the application restarts.",
            "Remote Pointer error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(-2);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        _ = sender;
        auditLog?.Write(
            ClientAuditEvent.UnhandledException,
            ClientAuditLevel.Error,
            exception: e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _ = sender;
        auditLog?.Write(
            ClientAuditEvent.UnhandledException,
            ClientAuditLevel.Error,
            exception: e.Exception);
        e.SetObserved();
    }
}
