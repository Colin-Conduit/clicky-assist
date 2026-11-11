using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Claude;
using Clicky.Core.Pointing;
using Clicky.Windows.Companion;
using Clicky.Windows.Diagnostics;
using Clicky.Windows.Display;

namespace Clicky.Windows.Panel;

/// <summary>
/// The floating companion panel that opens from the notification area icon. The Windows
/// counterpart of the Mac's borderless, non-activating NSPanel hosting CompanionPanelView:
/// it never takes focus from the app the user is in, never appears in the taskbar or Alt+Tab,
/// and hides itself when the user presses a mouse button anywhere outside it.
///
/// All state and visibility rules come from <see cref="CompanionPanelState"/>; this class only
/// copies them onto the view and forwards button clicks to <see cref="ICompanionPanelActions"/>.
/// Use it on the WPF UI thread only.
/// </summary>
public sealed partial class CompanionPanelWindow : Window
{
    /// <summary>Visible card width, as on the Mac (MenuBarPanelManager.panelWidth).</summary>
    private const double PanelCardWidthInDips = 320;

    /// <summary>Space between the card and the taskbar (and every other work-area edge).</summary>
    private const double PanelGapFromTaskbarInDips = 8;

    /// <summary>
    /// A press on the tray icon while the panel is open first hides the panel (outside click on
    /// button-down) and then reports a tray click (on button-up). Within this window after an
    /// outside-click dismissal, TogglePanel stays closed so that one click does not close and reopen it.
    /// </summary>
    private const long ReopenSuppressionAfterOutsideClickInMilliseconds = 300;

    /// <summary>
    /// Transparent room around the card inside the window for the drop shadow (blur 20, depth 10
    /// downward): the shadow reaches about 20 DIPs to the sides, 10 above and 30 below.
    /// </summary>
    private static readonly Thickness PanelShadowMarginInDips = new(24, 16, 24, 34);

    private const string ReadyToStartCopyText = "You're all set. Hit Start to meet Clicky.";
    private const string MicrophoneNeededTitleText = "Permissions needed";
    private const string MicrophoneNeededBodyText = "Microphone access is off. Turn it on below to keep using Clicky.";
    private const string IntroductionTitleText = "This is Clicky.";
    private const string IntroductionBodyText = "Clicky only takes a screenshot when you hold the hotkey. Turn on microphone access below to get started.";

    private readonly CompanionPanelState panelState;
    private readonly ICompanionPanelActions panelActions;
    private readonly PanelOutsideClickWatcher panelOutsideClickWatcher;
    private readonly DropShadowEffect statusDotGlowEffect;

    private PhysicalPixelPoint? currentAnchorPointInPhysicalPixels;
    private long? lastOutsideClickDismissalTickCount;
    private bool isPositioningWindow;
    private bool hasLoggedDpiMismatch;

    public CompanionPanelWindow(CompanionPanelState panelState, ICompanionPanelActions panelActions)
    {
        this.panelState = panelState;
        this.panelActions = panelActions;

        InitializeComponent();
        RegisterDesignTokenResources();

        PanelShadowHost.Width = PanelCardWidthInDips;
        PanelShadowHost.Margin = PanelShadowMarginInDips;
        Width = PanelCardWidthInDips + PanelShadowMarginInDips.Left + PanelShadowMarginInDips.Right;

        // Mac: .shadow(color: statusDotColor.opacity(0.6), radius: 4). The glow color follows the dot color.
        statusDotGlowEffect = new DropShadowEffect
        {
            ShadowDepth = 0,
            BlurRadius = 8,
            Opacity = 0.6,
        };
        StatusDot.Effect = statusDotGlowEffect;

        CreateModelPickerOptionButtons();

        panelOutsideClickWatcher = new PanelOutsideClickWatcher(Dispatcher, HandleMouseButtonPressedAnywhere);
        panelState.PropertyChanged += HandlePanelStatePropertyChanged;
        SizeChanged += HandlePanelWindowSizeChanged;
        DpiChanged += HandlePanelWindowDpiChanged;

        ApplyPanelStateToView();
    }

    public bool IsPanelOpen => IsVisible;

    /// <summary>
    /// Shows the panel next to the notification area of the monitor that contains the anchor,
    /// inside that monitor's work area, without activating it. If the panel is already open it moves.
    /// </summary>
    public void ShowNearNotificationArea(PhysicalPixelPoint anchorPointInPhysicalPixels)
    {
        currentAnchorPointInPhysicalPixels = anchorPointInPhysicalPixels;
        bool wasPanelOpen = IsPanelOpen;

        // Creating the window handle before the first Show lets the panel be moved while still
        // hidden. SourceInitialized (and so the no-activate style) runs during this call.
        new WindowInteropHelper(this).EnsureHandle();

        // First pass, while hidden: moves the window onto the target monitor so that a DPI change
        // between monitors is handled by WPF before anything is visible.
        PositionWindowNextToTaskbar(shouldBringToTopOfTopmostBand: false);

        if (!IsVisible)
        {
            Show();
        }

        // Second pass, after Show has run layout (SizeToContent) and any DPI change has been applied.
        // It also lifts the panel above the taskbar, which came to the top of the always-on-top band
        // when the user clicked it.
        UpdateLayout();
        CompanionPanelCardPlacement? finalCardPlacement = PositionWindowNextToTaskbar(shouldBringToTopOfTopmostBand: true);
        WarnOnceIfWindowDpiDiffersFromTargetMonitor();

        panelOutsideClickWatcher.StartWatching();

        if (!wasPanelOpen && finalCardPlacement is CompanionPanelCardPlacement shownCardPlacement)
        {
            ClickyApplicationLog.Write(
                "Panel",
                $"Panel shown at {shownCardPlacement.CardLeftInPhysicalPixels},{shownCardPlacement.CardTopInPhysicalPixels} next to a {shownCardPlacement.TaskbarEdge} taskbar.");
        }
    }

    /// <summary>Hides the panel and removes the outside-click hook. Safe to call when already hidden.</summary>
    public void HidePanel()
    {
        panelOutsideClickWatcher.StopWatching();

        if (IsVisible)
        {
            Hide();
        }
    }

    /// <summary>
    /// Opens the panel if it is closed and closes it if it is open. Stays closed when the panel was
    /// dismissed by an outside click within the last 300 ms: that press was the tray icon click now
    /// asking to toggle, so reopening would undo the user's close. (On the Mac the status item click
    /// never reaches the app's own global click monitor, so it does not need this check.)
    /// </summary>
    public void TogglePanel(PhysicalPixelPoint anchorPointInPhysicalPixels)
    {
        if (IsPanelOpen)
        {
            HidePanel();
            return;
        }

        if (WasDismissedByOutsideClickWithinReopenSuppressionWindow())
        {
            return;
        }

        ShowNearNotificationArea(anchorPointInPhysicalPixels);
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);

        // WS_EX_NOACTIVATE is the Windows equivalent of the Mac's .nonactivatingPanel: clicking the
        // panel's buttons does not pull focus away from the user's app. WS_EX_TOOLWINDOW keeps the
        // panel out of Alt+Tab (ShowInTaskbar=False already keeps it off the taskbar).
        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        PanelNativeMethods.AddExtendedWindowStyles(
            windowHandle,
            PanelNativeMethods.ExtendedWindowStyleNoActivate | PanelNativeMethods.ExtendedWindowStyleToolWindow);
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        panelState.PropertyChanged -= HandlePanelStatePropertyChanged;
        panelOutsideClickWatcher.Dispose();
        base.OnClosed(eventArgs);
    }

    /// <summary>
    /// Makes the design tokens available to the XAML under fixed keys. The XAML uses
    /// DynamicResource for all of them, so registering after InitializeComponent is fine.
    /// </summary>
    private void RegisterDesignTokenResources()
    {
        Resources["BackgroundBrush"] = ClickyDesignTokens.Brushes.Background;
        Resources["BorderSubtleBrush"] = ClickyDesignTokens.Brushes.BorderSubtle;
        Resources["TextPrimaryBrush"] = ClickyDesignTokens.Brushes.TextPrimary;
        Resources["TextSecondaryBrush"] = ClickyDesignTokens.Brushes.TextSecondary;
        Resources["TextTertiaryBrush"] = ClickyDesignTokens.Brushes.TextTertiary;
        Resources["TextOnAccentBrush"] = ClickyDesignTokens.Brushes.TextOnAccent;
        Resources["AccentBrush"] = ClickyDesignTokens.Brushes.Accent;
        Resources["AccentHoverBrush"] = ClickyDesignTokens.Brushes.AccentHover;
        Resources["SuccessBrush"] = ClickyDesignTokens.Brushes.Success;
        Resources["SegmentedControlFillBrush"] = ClickyDesignTokens.Brushes.SegmentedControlFill;
        Resources["SelectedSegmentFillBrush"] = ClickyDesignTokens.Brushes.SelectedSegmentFill;
        Resources["CloseButtonFillBrush"] = ClickyDesignTokens.Brushes.CloseButtonFill;
        Resources["CloseButtonHoverFillBrush"] = ClickyDesignTokens.Brushes.CloseButtonHoverFill;
        Resources["PanelTextFontFamily"] = ClickyDesignTokens.Typography.TextFontFamily;
        Resources["PanelIconFontFamily"] = ClickyDesignTokens.Typography.IconFontFamily;
        Resources["PanelMediumFontWeight"] = ClickyDesignTokens.Typography.MediumWeight;
    }

    private void CreateModelPickerOptionButtons()
    {
        Style modelOptionToggleButtonStyle = (Style)FindResource("PanelModelOptionToggleButtonStyle");

        foreach (ClaudeModelPickerOption modelPickerOption in ClaudeModelIdentifiers.PickerOptions)
        {
            ToggleButton modelOptionToggleButton = new()
            {
                Content = modelPickerOption.PickerLabel,
                Style = modelOptionToggleButtonStyle,
                Tag = modelPickerOption,
            };
            modelOptionToggleButton.Click += HandleModelOptionToggleButtonClick;
            ModelOptionButtonsPanel.Children.Add(modelOptionToggleButton);
        }
    }

    private void HandlePanelStatePropertyChanged(object? sender, PropertyChangedEventArgs propertyChangedEventArgs)
    {
        // The state raises one event per input change plus one per derived property (about a dozen).
        // Re-applying everything is cheap and idempotent, so the property name is not inspected.
        ApplyPanelStateToView();
    }

    private void ApplyPanelStateToView()
    {
        ApplyHeaderState();
        ApplyCopySectionState();

        ModelPickerRow.Visibility = ToVisibility(panelState.IsModelPickerVisible);
        SynchronizeModelPickerSelection();

        PermissionsSection.Visibility = ToVisibility(panelState.IsPermissionsSectionVisible);
        ApplyMicrophoneRowState();

        StartButton.Visibility = ToVisibility(panelState.IsStartButtonVisible);
        ReplayOnboardingButton.Visibility = ToVisibility(panelState.IsReplayOnboardingVisible);
    }

    private void ApplyHeaderState()
    {
        StatusTextBlock.Text = panelState.StatusText;

        (SolidColorBrush statusDotBrush, Color statusDotColor) = panelState.StatusDotKind switch
        {
            PanelStatusDotKind.ActiveIdle => (ClickyDesignTokens.Brushes.Success, ClickyDesignTokens.Colors.Success),
            PanelStatusDotKind.Busy => (ClickyDesignTokens.Brushes.Blue400, ClickyDesignTokens.Colors.Blue400),
            _ => (ClickyDesignTokens.Brushes.TextTertiary, ClickyDesignTokens.Colors.TextTertiary),
        };
        StatusDot.Fill = statusDotBrush;
        statusDotGlowEffect.Color = statusDotColor;
    }

    private void ApplyCopySectionState()
    {
        switch (panelState.CopyMode)
        {
            case PanelCopyMode.ReadyToTalk:
                ShowCopy(titleText: panelState.ReadyToTalkHintText, isTitleBold: false, bodyText: null);
                break;
            case PanelCopyMode.ReadyToStart:
                ShowCopy(titleText: ReadyToStartCopyText, isTitleBold: false, bodyText: null);
                break;
            case PanelCopyMode.MicrophoneNeededAfterOnboarding:
                ShowCopy(titleText: MicrophoneNeededTitleText, isTitleBold: true, bodyText: MicrophoneNeededBodyText);
                break;
            case PanelCopyMode.Introduction:
                ShowCopy(titleText: IntroductionTitleText, isTitleBold: true, bodyText: IntroductionBodyText);
                break;
        }
    }

    /// <summary>
    /// The single-line copy modes use 12 medium; the two-line modes use a 12 bold title over an
    /// 11 regular body, like the Mac's VStack branches.
    /// </summary>
    private void ShowCopy(string titleText, bool isTitleBold, string? bodyText)
    {
        CopyTitleTextBlock.Text = titleText;
        CopyTitleTextBlock.FontWeight = isTitleBold ? FontWeights.Bold : ClickyDesignTokens.Typography.MediumWeight;

        CopyBodyTextBlock.Text = bodyText ?? string.Empty;
        CopyBodyTextBlock.Visibility = ToVisibility(bodyText is not null);
    }

    private void ApplyMicrophoneRowState()
    {
        MicrophoneAvailability microphoneAvailability = panelState.MicrophoneAvailability;
        bool isMicrophoneAvailable = microphoneAvailability == MicrophoneAvailability.Available;
        bool isMicrophoneBlockedByPrivacySettings = microphoneAvailability == MicrophoneAvailability.BlockedByPrivacySettings;

        // Mac: the permission glyph is amber (warning) until granted.
        MicrophoneGlyphTextBlock.Foreground = isMicrophoneAvailable
            ? ClickyDesignTokens.Brushes.TextTertiary
            : ClickyDesignTokens.Brushes.Warning;

        MicrophoneGrantedIndicator.Visibility = ToVisibility(isMicrophoneAvailable);
        MicrophoneGrantButton.Visibility = ToVisibility(isMicrophoneBlockedByPrivacySettings);
        MicrophonePrivacySettingsCaption.Visibility = ToVisibility(isMicrophoneBlockedByPrivacySettings);
        NoMicrophoneFoundTextBlock.Visibility = ToVisibility(microphoneAvailability == MicrophoneAvailability.NoDeviceFound);
    }

    /// <summary>Checks the segment whose model identifier is the selected one in the panel state, and unchecks the rest.</summary>
    private void SynchronizeModelPickerSelection()
    {
        foreach (object modelPickerChild in ModelOptionButtonsPanel.Children)
        {
            if (modelPickerChild is ToggleButton { Tag: ClaudeModelPickerOption modelPickerOption } modelOptionToggleButton)
            {
                modelOptionToggleButton.IsChecked = modelPickerOption.ModelIdentifier == panelState.SelectedModelIdentifier;
            }
        }
    }

    private void HandleModelOptionToggleButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        if (sender is ToggleButton { Tag: ClaudeModelPickerOption clickedModelPickerOption })
        {
            panelActions.SelectModel(clickedModelPickerOption.ModelIdentifier);
        }

        // A ToggleButton flips its own IsChecked before raising Click (and would uncheck an already
        // selected segment). Re-reading the state keeps the picker showing what the companion stored.
        SynchronizeModelPickerSelection();
    }

    private void HandleCloseButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        HidePanel();
    }

    private void HandleMicrophoneGrantButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        panelActions.OpenMicrophonePrivacySettings();
    }

    private void HandleStartButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        panelActions.StartOnboarding();
    }

    private void HandleQuitButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        panelActions.QuitApplication();
    }

    private void HandleReplayOnboardingButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        panelActions.ReplayOnboarding();
    }

    /// <summary>Called on the UI thread by the outside-click watcher for every mouse button press on screen.</summary>
    private void HandleMouseButtonPressedAnywhere(PhysicalPixelPoint pressPointInPhysicalPixels)
    {
        if (!IsPanelOpen || IsPointInsidePanelCard(pressPointInPhysicalPixels))
        {
            return;
        }

        // Only this path records the time; the close button and push-to-talk hides must not
        // suppress a tray click that follows them.
        lastOutsideClickDismissalTickCount = Environment.TickCount64;
        HidePanel();
    }

    private bool WasDismissedByOutsideClickWithinReopenSuppressionWindow()
    {
        if (lastOutsideClickDismissalTickCount is not long dismissalTickCount)
        {
            return false;
        }

        long millisecondsSinceOutsideClickDismissal = Environment.TickCount64 - dismissalTickCount;
        return millisecondsSinceOutsideClickDismissal < ReopenSuppressionAfterOutsideClickInMilliseconds;
    }

    /// <summary>
    /// Hit-tests against the visible card rather than the whole window, so a press on the
    /// transparent shadow margin counts as outside, like a press anywhere else beside the panel.
    /// </summary>
    private bool IsPointInsidePanelCard(PhysicalPixelPoint pointInPhysicalPixels)
    {
        if (PresentationSource.FromVisual(PanelCard) is null)
        {
            return false;
        }

        // For a per-monitor DPI aware window, PointToScreen returns physical screen pixels.
        Point cardTopLeftInPhysicalPixels = PanelCard.PointToScreen(new Point(0, 0));
        Point cardBottomRightInPhysicalPixels = PanelCard.PointToScreen(new Point(PanelCard.ActualWidth, PanelCard.ActualHeight));

        return pointInPhysicalPixels.X >= cardTopLeftInPhysicalPixels.X
            && pointInPhysicalPixels.X < cardBottomRightInPhysicalPixels.X
            && pointInPhysicalPixels.Y >= cardTopLeftInPhysicalPixels.Y
            && pointInPhysicalPixels.Y < cardBottomRightInPhysicalPixels.Y;
    }

    /// <summary>
    /// Keeps the card's taskbar-side edge in place while the panel is open. SizeToContent grows and
    /// shrinks the window from its top edge, so without this a panel above a bottom taskbar would
    /// slide under the taskbar when, for example, the permissions section appears.
    /// </summary>
    private void HandlePanelWindowSizeChanged(object sender, SizeChangedEventArgs sizeChangedEventArgs)
    {
        if (IsPanelOpen)
        {
            PositionWindowNextToTaskbar(shouldBringToTopOfTopmostBand: false);
        }
    }

    /// <summary>
    /// WPF resizes the window to Windows' suggested rectangle when the DPI changes, which can move it.
    /// Re-placing after layout (Loaded priority runs after the layout and render passes) restores the
    /// intended position at the new scale.
    /// </summary>
    private void HandlePanelWindowDpiChanged(object sender, DpiChangedEventArgs dpiChangedEventArgs)
    {
        if (IsPanelOpen)
        {
            Dispatcher.InvokeAsync(
                () => PositionWindowNextToTaskbar(shouldBringToTopOfTopmostBand: false),
                DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// Moves the window so its card sits next to the taskbar of the anchor's monitor. Works in physical
    /// pixels with SetWindowPos because WPF's Left and Top are DIPs relative to the window's current
    /// monitor, which is ambiguous while moving between monitors with different scale factors.
    /// The size is left to WPF (SizeToContent); only the position is set.
    /// </summary>
    /// <returns>Where the card was placed, or null when there was nothing to place (no anchor yet, no window handle, no monitor, or a move already in progress).</returns>
    private CompanionPanelCardPlacement? PositionWindowNextToTaskbar(bool shouldBringToTopOfTopmostBand)
    {
        if (isPositioningWindow || currentAnchorPointInPhysicalPixels is not PhysicalPixelPoint anchorPointInPhysicalPixels)
        {
            return null;
        }

        IntPtr windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return null;
        }

        IReadOnlyList<DisplayMonitorInfo> connectedMonitors = DisplayMonitorEnumerator.EnumerateMonitors();
        DisplayMonitorInfo? targetMonitor = DisplayMonitorEnumerator.FindMonitorContainingPoint(connectedMonitors, anchorPointInPhysicalPixels);
        if (targetMonitor is null)
        {
            return null;
        }

        // Moving the window can change its DPI, which resizes it and raises SizeChanged and
        // DpiChanged; the flag stops those handlers from re-entering this method mid-move.
        isPositioningWindow = true;
        try
        {
            // Measuring the card directly gives its height in DIPs even before the window has been
            // shown (a hidden window skips layout). DIP sizes do not depend on the monitor's scale.
            PanelShadowHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double cardHeightInDips = PanelShadowHost.DesiredSize.Height - PanelShadowMarginInDips.Top - PanelShadowMarginInDips.Bottom;

            CompanionPanelCardPlacement cardPlacement = CompanionPanelPlacementCalculator.CalculateCardPlacement(
                targetMonitor,
                anchorPointInPhysicalPixels,
                PanelCardWidthInDips,
                cardHeightInDips,
                PanelGapFromTaskbarInDips);

            // The window is larger than the card by the shadow margin, scaled for the target monitor.
            int windowLeftInPhysicalPixels = (int)Math.Round(cardPlacement.CardLeftInPhysicalPixels - (PanelShadowMarginInDips.Left * targetMonitor.DpiScale));
            int windowTopInPhysicalPixels = (int)Math.Round(cardPlacement.CardTopInPhysicalPixels - (PanelShadowMarginInDips.Top * targetMonitor.DpiScale));

            PanelNativeMethods.MoveWindowWithoutActivating(
                windowHandle,
                windowLeftInPhysicalPixels,
                windowTopInPhysicalPixels,
                shouldBringToTopOfTopmostBand);

            return cardPlacement;
        }
        finally
        {
            isPositioningWindow = false;
        }
    }

    /// <summary>
    /// The placement math converts DIPs with the target monitor's scale. If the window has not picked
    /// up that monitor's DPI (for example a hidden window that Windows did not rescale), the card can
    /// be off by the scale difference until the DpiChanged handler re-places it; log it once to help diagnose.
    /// </summary>
    private void WarnOnceIfWindowDpiDiffersFromTargetMonitor()
    {
        if (hasLoggedDpiMismatch || currentAnchorPointInPhysicalPixels is not PhysicalPixelPoint anchorPointInPhysicalPixels)
        {
            return;
        }

        DisplayMonitorInfo? targetMonitor = DisplayMonitorEnumerator.FindMonitorContainingPoint(
            DisplayMonitorEnumerator.EnumerateMonitors(),
            anchorPointInPhysicalPixels);
        double windowDpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (targetMonitor is not null && Math.Abs(windowDpiScale - targetMonitor.DpiScale) > 0.01)
        {
            hasLoggedDpiMismatch = true;
            ClickyApplicationLog.Write(
                "Panel",
                $"Panel window scale {windowDpiScale:0.##} differs from monitor {targetMonitor.DeviceName} scale {targetMonitor.DpiScale:0.##} after placement.");
        }
    }

    private static Visibility ToVisibility(bool isVisible)
    {
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }
}
