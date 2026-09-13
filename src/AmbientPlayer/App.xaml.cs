using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace AmbientPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // This is a personal, single-user tool that is meant to sit fullscreen
        // for hours unattended (an album, a whole listening session). SMTC
        // access is intermittently flaky - particularly around AirPlay
        // connect/disconnect - and every one of those paths is already
        // handled where it happens. This is a last-resort net: log and keep
        // going rather than tearing down the display over a transient error.
        // Wired up before base.OnStartup, which is what creates and shows
        // MainWindow via StartupUri.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] unhandled UI exception: {e.Exception}");
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Debug.WriteLine($"[App] unobserved task exception: {e.Exception}");
        e.SetObserved();
    }
}
