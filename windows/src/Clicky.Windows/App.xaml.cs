using System.Windows;
using System.Windows.Threading;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Orchestration;

namespace Clicky.Windows;

/// <summary>
/// WPF entry point. Clicky has no main window, so startup hands straight off to
/// <see cref="ClickyApplicationStartup"/>, which builds the tray app or runs a debug mode.
/// </summary>
public partial class App : Application
{
    private ClickyApplicationStartup? applicationStartup;

    protected override async void OnStartup(StartupEventArgs startupEventArguments)
    {
        base.OnStartup(startupEventArguments);

        // A tray app has no window to show an error in; log unexpected UI-thread exceptions and keep
        // running rather than vanishing from the notification area.
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += HandleUnobservedTaskException;

        applicationStartup = new ClickyApplicationStartup(this);
        try
        {
            await applicationStartup.RunAsync(startupEventArguments.Args);
        }
        catch (Exception startupException)
        {
            ClickyApplicationLog.Write("App", $"Startup failed: {startupException}");
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs exitEventArguments)
    {
        applicationStartup?.Dispose();
        base.OnExit(exitEventArguments);
    }

    private static void HandleDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs exceptionEventArguments)
    {
        ClickyApplicationLog.Write("App", $"Unhandled UI exception: {exceptionEventArguments.Exception}");
        exceptionEventArguments.Handled = true;
    }

    private static void HandleUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs exceptionEventArguments)
    {
        ClickyApplicationLog.Write("App", $"Unobserved task exception: {exceptionEventArguments.Exception}");
        exceptionEventArguments.SetObserved();
    }
}
