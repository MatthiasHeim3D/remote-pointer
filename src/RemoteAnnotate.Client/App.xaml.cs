using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RemoteAnnotate.Client.Services;
using RemoteAnnotate.Client.Views;

namespace RemoteAnnotate.Client;

public partial class App : System.Windows.Application
{
    private const string ApplicationMutexName = "RemoteAnnotate.Client.Running";
    private const string ApplicationActivationEventName = "RemoteAnnotate.Client.Activate";
    private IClientAuditLog? auditLog;
    private ApplicationInstanceGuard? instanceGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!ApplicationInstanceGuard.TryAcquire(
                ApplicationMutexName,
                ApplicationActivationEventName,
                ApplicationInstancePolicy.EnforceSingleInstance,
                out instanceGuard))
        {
            Shutdown();
            return;
        }

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
            if (!window.RequiresInitialSetup)
            {
                window.Hide();
            }
            instanceGuard?.ListenForActivation(ShowMainWindow);
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
                "Remote Annotate could not start because its configuration or local state is invalid. Check the audit log under LocalAppData\\RemoteAnnotate\\Logs.",
                "Remote Annotate startup error",
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
        instanceGuard?.Dispose();
        instanceGuard = null;
        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is RemoteAnnotate.Client.Views.MainWindow window)
            {
                window.ShowAndActivate();
            }
        });
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
            "Remote Annotate encountered an unexpected error and must close. The connection can be recovered when the application restarts.",
            "Remote Annotate error",
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
