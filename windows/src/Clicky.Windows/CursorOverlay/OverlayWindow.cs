using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Clicky.Core.Pointing;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// A full-monitor, transparent, click-through, always-on-top window hosting the blue cursor for
/// one monitor. Windows port of the OverlayWindow NSWindow in OverlayWindow.swift, which used
/// level .screenSaver, ignoresMouseEvents true and canBecomeKey false. Win32 has no single switch
/// for each of those, so this class combines WPF properties, extended window styles and a message
/// hook; each piece is commented with the Mac behavior it reproduces.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const string OverlayLogCategory = "Overlay";

    private readonly DisplayMonitorInfo overlayMonitor;
    private readonly bool shouldExcludeFromScreenCapture;
    private IntPtr overlayWindowHandle;
    private string captureExclusionResultDescription = "window not created yet";

    public OverlayWindow(
        DisplayMonitorInfo overlayMonitor,
        CompanionOverlayState overlayState,
        Stopwatch overlayAnimationClock,
        bool shouldExcludeFromScreenCapture,
        bool playsWelcomeAnimation,
        Color? captureTestMarkerColor)
    {
        this.overlayMonitor = overlayMonitor;
        this.shouldExcludeFromScreenCapture = shouldExcludeFromScreenCapture;

        // Mac: styleMask .borderless, isOpaque false, backgroundColor .clear, hasShadow false.
        // AllowsTransparency makes WPF create a per-pixel-alpha layered window (WS_EX_LAYERED).
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;

        // Mac: the overlay never shows up as a window the user can switch to.
        ShowInTaskbar = false;

        // Mac: canBecomeKey and canBecomeMain return false. Showing the overlay must never take
        // focus away from the app the user is working in.
        ShowActivated = false;
        Focusable = false;

        // Mac: level .screenSaver. HWND_TOPMOST is the highest band a normal app window can use.
        Topmost = true;

        Title = "Clicky cursor overlay";

        // A first guess in DIPs. WPF converts these with whatever DPI it assumes before the window
        // exists, which can be wrong on mixed-DPI setups, so SourceInitialized replaces them with
        // the exact physical-pixel bounds.
        WindowStartupLocation = WindowStartupLocation.Manual;
        PhysicalPixelRectangle monitorBounds = overlayMonitor.BoundsInPhysicalPixels;
        Left = monitorBounds.Left / overlayMonitor.DpiScale;
        Top = monitorBounds.Top / overlayMonitor.DpiScale;
        Width = monitorBounds.Width / overlayMonitor.DpiScale;
        Height = monitorBounds.Height / overlayMonitor.DpiScale;

        CursorOverlayView = new BlueCursorOverlayView(overlayMonitor, overlayState, overlayAnimationClock, playsWelcomeAnimation, captureTestMarkerColor);
        Content = CursorOverlayView;

        SourceInitialized += HandleSourceInitialized;
    }

    public BlueCursorOverlayView CursorOverlayView { get; }

    public DisplayMonitorInfo OverlayMonitor => overlayMonitor;

    public bool IsOverlayClosed { get; private set; }

    /// <summary>Puts the window exactly over its monitor, in physical pixels, at the top of the topmost band.</summary>
    public void ApplyExactMonitorBounds()
    {
        ApplyExactMonitorBounds(shouldRefreshCachedFrameStyles: false);
    }

    /// <summary>Mac: fadeOutAndHideOverlay animates alphaValue to 0 with an ease-in curve.</summary>
    public void BeginFadeOut(TimeSpan fadeDuration, Action fadeCompletedCallback)
    {
        if (IsOverlayClosed || fadeDuration <= TimeSpan.Zero)
        {
            fadeCompletedCallback();
            return;
        }

        DoubleAnimation windowFadeAnimation = new(Opacity, 0.0, new Duration(fadeDuration))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        windowFadeAnimation.Completed += (_, _) => fadeCompletedCallback();
        BeginAnimation(OpacityProperty, windowFadeAnimation);
    }

    public void CloseOverlay()
    {
        if (IsOverlayClosed)
        {
            return;
        }

        IsOverlayClosed = true;
        BeginAnimation(OpacityProperty, null);
        Close();
    }

    /// <summary>One line for the capture test report: device, monitor bounds, actual window bounds, DPI and capture exclusion.</summary>
    public string DescribeCaptureExclusionStatus()
    {
        PhysicalPixelRectangle monitorBounds = overlayMonitor.BoundsInPhysicalPixels;

        string windowBoundsDescription = "unknown";
        string displayAffinityDescription = "unknown";
        if (overlayWindowHandle != IntPtr.Zero)
        {
            if (OverlayNativeMethods.GetWindowRect(overlayWindowHandle, out OverlayNativeRectangle windowRectangle))
            {
                windowBoundsDescription = $"{windowRectangle.Left},{windowRectangle.Top} {windowRectangle.Width}x{windowRectangle.Height}";
            }

            if (OverlayNativeMethods.GetWindowDisplayAffinity(overlayWindowHandle, out uint currentDisplayAffinity))
            {
                displayAffinityDescription = $"0x{currentDisplayAffinity:X2}";
            }
        }

        return $"{overlayMonitor.DeviceName}: monitor {monitorBounds.Left},{monitorBounds.Top} {monitorBounds.Width}x{monitorBounds.Height}, "
            + $"window {windowBoundsDescription}, window DPI scale {CursorOverlayView.WindowDpiScale:0.##}, "
            + $"{captureExclusionResultDescription}, display affinity now {displayAffinityDescription}";
    }

    /// <summary>
    /// Runs after WPF created the HWND and before the window is shown, which is the only moment
    /// to change styles and placement without the user seeing a flash.
    /// </summary>
    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        overlayWindowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(overlayWindowHandle)?.AddHook(HandleOverlayWindowMessage);

        ApplyClickThroughExtendedStyles();
        ApplyExactMonitorBounds(shouldRefreshCachedFrameStyles: true);
        ApplyCaptureExclusion();

        double windowDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        CursorOverlayView.UpdateWindowDpiScale(windowDpiScale);

        PhysicalPixelRectangle monitorBounds = overlayMonitor.BoundsInPhysicalPixels;
        ClickyApplicationLog.Write(
            OverlayLogCategory,
            $"Created overlay on {overlayMonitor.DeviceName}: physical bounds {monitorBounds.Left},{monitorBounds.Top} {monitorBounds.Width}x{monitorBounds.Height}, "
            + $"monitor DPI scale {overlayMonitor.DpiScale:0.##}, window DPI scale {windowDpiScale:0.##}, {captureExclusionResultDescription}");
    }

    private void ApplyClickThroughExtendedStyles()
    {
        long existingExtendedStyle = OverlayNativeMethods.GetExtendedWindowStyle(overlayWindowHandle);

        // WS_EX_TRANSPARENT (Mac: ignoresMouseEvents = true): every click goes to the window underneath.
        //   On a layered window this covers the drawn pixels too, not just the fully transparent ones.
        // WS_EX_NOACTIVATE (Mac: canBecomeKey false): the window never becomes the foreground window,
        //   even if something does reach it.
        // WS_EX_TOOLWINDOW: keeps the overlay out of Alt+Tab and Task View.
        // WS_EX_LAYERED is already set by WPF because of AllowsTransparency.
        long overlayExtendedStyle = existingExtendedStyle
            | OverlayNativeMethods.ExtendedStyleTransparent
            | OverlayNativeMethods.ExtendedStyleNoActivate
            | OverlayNativeMethods.ExtendedStyleToolWindow;

        int setStyleErrorCode = OverlayNativeMethods.SetExtendedWindowStyle(overlayWindowHandle, overlayExtendedStyle);
        if (setStyleErrorCode != 0)
        {
            ClickyApplicationLog.Write(OverlayLogCategory, $"SetWindowLongPtr for the click-through styles failed on {overlayMonitor.DeviceName} with Win32 error {setStyleErrorCode}.");
        }
    }

    private void ApplyExactMonitorBounds(bool shouldRefreshCachedFrameStyles)
    {
        if (overlayWindowHandle == IntPtr.Zero || IsOverlayClosed)
        {
            return;
        }

        uint setWindowPositionFlags = OverlayNativeMethods.SetWindowPositionNoActivate | OverlayNativeMethods.SetWindowPositionNoOwnerZOrder;
        if (shouldRefreshCachedFrameStyles)
        {
            // SetWindowLongPtr documentation: some style changes only take effect after SWP_FRAMECHANGED.
            setWindowPositionFlags |= OverlayNativeMethods.SetWindowPositionFrameChanged;
        }

        PhysicalPixelRectangle monitorBounds = overlayMonitor.BoundsInPhysicalPixels;
        bool didSetWindowPosition = OverlayNativeMethods.SetWindowPos(
            overlayWindowHandle,
            OverlayNativeMethods.TopmostInsertAfterWindowHandle,
            monitorBounds.Left,
            monitorBounds.Top,
            monitorBounds.Width,
            monitorBounds.Height,
            setWindowPositionFlags);

        if (!didSetWindowPosition)
        {
            int setWindowPositionErrorCode = Marshal.GetLastWin32Error();
            ClickyApplicationLog.Write(OverlayLogCategory, $"SetWindowPos failed on {overlayMonitor.DeviceName} with Win32 error {setWindowPositionErrorCode}.");
        }
    }

    /// <summary>
    /// Keeps the overlay out of the screenshots sent to Claude, so Claude never sees its own cursor
    /// or bubbles. The Mac gets this from ScreenCaptureKit excluding the app's windows; Windows does it
    /// per window with WDA_EXCLUDEFROMCAPTURE, which Microsoft documents as hiding the window everywhere
    /// except on the monitor itself (checked here with BitBlt screen copies).
    /// </summary>
    private void ApplyCaptureExclusion()
    {
        if (!shouldExcludeFromScreenCapture)
        {
            captureExclusionResultDescription = "capture exclusion turned off (overlay appears in screenshots)";
            return;
        }

        if (OverlayNativeMethods.SetWindowDisplayAffinity(overlayWindowHandle, OverlayNativeMethods.DisplayAffinityExcludeFromCapture))
        {
            captureExclusionResultDescription = "excluded from screen capture";
            return;
        }

        int displayAffinityErrorCode = Marshal.GetLastWin32Error();
        captureExclusionResultDescription = $"SetWindowDisplayAffinity failed with Win32 error {displayAffinityErrorCode}";
        ClickyApplicationLog.Write(OverlayLogCategory, $"SetWindowDisplayAffinity failed on {overlayMonitor.DeviceName} with Win32 error {displayAffinityErrorCode}; the overlay will show up in screenshots.");
    }

    private IntPtr HandleOverlayWindowMessage(IntPtr windowHandle, int windowMessage, IntPtr wordParameter, IntPtr longParameter, ref bool handled)
    {
        switch (windowMessage)
        {
            case OverlayNativeMethods.WindowMessageMouseActivate:
                // Second layer behind WS_EX_NOACTIVATE: if a click ever reaches the overlay, it still must not activate it.
                handled = true;
                return new IntPtr(OverlayNativeMethods.MouseActivateNoActivate);

            case OverlayNativeMethods.WindowMessageNonClientHitTest:
                // Second layer behind WS_EX_TRANSPARENT: report every point as "not here" so the
                // mouse always belongs to the window underneath.
                handled = true;
                return new IntPtr(OverlayNativeMethods.HitTestTransparent);

            case OverlayNativeMethods.WindowMessageDpiChanged:
                // Not marked handled: WPF needs this message to update its own DPI and re-render
                // crisply. WPF then resizes the window to the rectangle Windows suggests, which keeps
                // the DIP size but no longer matches the monitor, so the exact physical bounds go
                // back on as soon as WPF has finished with the message.
                Dispatcher.InvokeAsync(ApplyExactMonitorBounds, DispatcherPriority.Send);
                break;
        }

        return IntPtr.Zero;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        CursorOverlayView.UpdateWindowDpiScale(newDpi.DpiScaleX);
        ClickyApplicationLog.Write(OverlayLogCategory, $"Overlay on {overlayMonitor.DeviceName} changed DPI scale from {oldDpi.DpiScaleX:0.##} to {newDpi.DpiScaleX:0.##}.");
    }
}
