using System.Windows;
using System.Windows.Threading;
using Clicky.Core.Configuration;
using Clicky.Core.Pointing;
using Clicky.Windows.Debugging;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;
using Clicky.Windows.Panel;
using Clicky.Windows.Tray;
using Microsoft.Win32;

namespace Clicky.Windows.Orchestration;

/// <summary>
/// What App.xaml.cs runs on startup. Picks a debug mode from the command line, or builds the
/// normal tray app: notification-area icon, companion panel, keyboard hook and overlays.
/// Mac counterpart: CompanionAppDelegate in leanring_buddyApp.swift.
/// </summary>
public sealed class ClickyApplicationStartup : IDisposable
{
    private const string LogCategory = "App";

    private readonly Application application;
    private ClickyApplicationServices? applicationServices;
    private TrayIconManager? trayIconManager;
    private CompanionPanelWindow? companionPanelWindow;

    public ClickyApplicationStartup(Application application)
    {
        this.application = application;
    }

    public async Task RunAsync(IReadOnlyList<string> commandLineArguments)
    {
        ClickyCommandLineOptions commandLineOptions = ClickyCommandLineOptions.Parse(commandLineArguments);
        ClickyApplicationLog.Initialize(attachToParentConsole: commandLineOptions.IsAnyDebugMode);
        Dispatcher userInterfaceDispatcher = application.Dispatcher;

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.LoadFromProcessEnvironment(AppContext.BaseDirectory);
        ClickyApplicationLog.Write(LogCategory, $"Clicky for Windows starting. Worker {workerConfiguration.WorkerBaseUrl} (from {workerConfiguration.WorkerBaseUrlSource}). Log file {ClickyApplicationLog.LogFilePath}.");

        if (commandLineOptions.IsCaptureTestMode)
        {
            application.Shutdown(await CaptureTestRunner.RunAsync(commandLineOptions));
            return;
        }

        if (commandLineOptions.IsOverlayTestMode)
        {
            application.Shutdown(await OverlayTestRunner.RunAsync(commandLineOptions));
            return;
        }

        applicationServices = new ClickyApplicationServices(workerConfiguration, commandLineOptions.CaptureBackendPreference, userInterfaceDispatcher);
        applicationServices.OverlayWindowManager.ExcludeOverlaysFromScreenCapture = !commandLineOptions.ShouldShowOverlaysInCaptures;

        if (commandLineOptions.IsTokenTestMode)
        {
            application.Shutdown(await WorkerTurnTestRunner.RunTokenTestAsync(applicationServices));
            return;
        }

        if (commandLineOptions.AskTranscript is not null)
        {
            application.Shutdown(await WorkerTurnTestRunner.RunAskAsync(applicationServices, commandLineOptions.AskTranscript));
            return;
        }

        StartTrayApplication(commandLineOptions);
    }

    private void StartTrayApplication(ClickyCommandLineOptions commandLineOptions)
    {
        ClickyApplicationServices services = applicationServices!;
        CreateTrayIconAndPanel(services);

        services.CompanionManager.Start();

        // Monitors plugged in, removed or rescaled: rebuild the per-monitor overlays.
        SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;

        if (commandLineOptions.ShouldOpenPanelAtStartup)
        {
            companionPanelWindow!.ShowNearNotificationArea(GetPrimaryMonitorNotificationAreaAnchor());
        }

        ClickyApplicationLog.Write(LogCategory, "Tray app running.");
    }

    private void CreateTrayIconAndPanel(ClickyApplicationServices services)
    {
        companionPanelWindow = new CompanionPanelWindow(services.CompanionManager.PanelState, services.CompanionManager);
        trayIconManager = new TrayIconManager();
        trayIconManager.TrayIconClicked += anchorPointInPhysicalPixels => companionPanelWindow.TogglePanel(anchorPointInPhysicalPixels);

        services.CompanionManager.PanelDismissRequested += () => companionPanelWindow.HidePanel();
        services.CompanionManager.QuitRequested += () =>
        {
            ClickyApplicationLog.Write(LogCategory, "Quit requested from the panel.");
            application.Shutdown(0);
        };
    }

    private void HandleDisplaySettingsChanged(object? sender, EventArgs eventArguments)
    {
        application.Dispatcher.BeginInvoke(() => applicationServices?.CompanionManager.HandleDisplayConfigurationChanged());
    }

    /// <summary>Bottom-right corner of the primary monitor's work area, where the notification area usually is.</summary>
    private static PhysicalPixelPoint GetPrimaryMonitorNotificationAreaAnchor()
    {
        IReadOnlyList<DisplayMonitorInfo> connectedMonitors = DisplayMonitorEnumerator.EnumerateMonitors();
        DisplayMonitorInfo primaryMonitor = connectedMonitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? connectedMonitors[0];
        PhysicalPixelRectangle primaryWorkArea = primaryMonitor.WorkAreaInPhysicalPixels;
        return new PhysicalPixelPoint(primaryWorkArea.Right - 40, primaryMonitor.BoundsInPhysicalPixels.Bottom - 20);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
        trayIconManager?.Dispose();
        companionPanelWindow?.HidePanel();
        applicationServices?.Dispose();
        ClickyApplicationLog.Write(LogCategory, "Clicky for Windows exited.");
    }
}
