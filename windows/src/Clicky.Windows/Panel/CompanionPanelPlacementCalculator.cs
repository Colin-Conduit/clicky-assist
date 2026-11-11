using Clicky.Core.Pointing;
using Clicky.Windows.Display;

namespace Clicky.Windows.Panel;

/// <summary>Which monitor edge the taskbar, and so the notification area, is docked to.</summary>
public enum TaskbarEdge
{
    Bottom,
    Top,
    Left,
    Right,
}

/// <summary>Top-left corner of the visible panel card (not the window, which adds a shadow margin), in physical pixels.</summary>
public readonly record struct CompanionPanelCardPlacement(int CardLeftInPhysicalPixels, int CardTopInPhysicalPixels, TaskbarEdge TaskbarEdge);

/// <summary>
/// Pure placement math for the companion panel. The Mac drops the panel 4 points below the
/// status item in the menu bar; the Windows counterpart is to sit just inside the work area
/// next to the taskbar that holds the notification area icon.
/// </summary>
public static class CompanionPanelPlacementCalculator
{
    /// <summary>
    /// Finds the taskbar edge by comparing the work area with the monitor bounds: an edge where
    /// the work area stops short of the monitor has an app bar docked on it. If several edges do
    /// (for example a docked toolbar plus the taskbar), the edge closest to the anchor wins, since
    /// the anchor is where the user clicked the tray icon. With an auto-hide taskbar, or a monitor
    /// without a taskbar, the work area equals the bounds and the Windows default (bottom) is used.
    /// </summary>
    public static TaskbarEdge DetectTaskbarEdge(
        PhysicalPixelRectangle monitorBoundsInPhysicalPixels,
        PhysicalPixelRectangle workAreaInPhysicalPixels,
        PhysicalPixelPoint anchorPointInPhysicalPixels)
    {
        List<(TaskbarEdge ReservedEdge, double DistanceFromAnchor)> reservedEdges = [];

        if (workAreaInPhysicalPixels.Bottom < monitorBoundsInPhysicalPixels.Bottom)
        {
            reservedEdges.Add((TaskbarEdge.Bottom, monitorBoundsInPhysicalPixels.Bottom - anchorPointInPhysicalPixels.Y));
        }

        if (workAreaInPhysicalPixels.Top > monitorBoundsInPhysicalPixels.Top)
        {
            reservedEdges.Add((TaskbarEdge.Top, anchorPointInPhysicalPixels.Y - monitorBoundsInPhysicalPixels.Top));
        }

        if (workAreaInPhysicalPixels.Left > monitorBoundsInPhysicalPixels.Left)
        {
            reservedEdges.Add((TaskbarEdge.Left, anchorPointInPhysicalPixels.X - monitorBoundsInPhysicalPixels.Left));
        }

        if (workAreaInPhysicalPixels.Right < monitorBoundsInPhysicalPixels.Right)
        {
            reservedEdges.Add((TaskbarEdge.Right, monitorBoundsInPhysicalPixels.Right - anchorPointInPhysicalPixels.X));
        }

        if (reservedEdges.Count == 0)
        {
            return TaskbarEdge.Bottom;
        }

        return reservedEdges.MinBy(reservedEdge => Math.Abs(reservedEdge.DistanceFromAnchor)).ReservedEdge;
    }

    /// <summary>
    /// Places the card inside the target monitor's work area with <paramref name="gapFromTaskbarInDips"/>
    /// between the card and the taskbar. Along the taskbar the card is centered on the anchor; the result
    /// is then clamped so the same gap is kept from every other work-area edge too.
    /// </summary>
    /// <param name="targetMonitor">Monitor containing the anchor; its DPI scale converts DIPs to physical pixels.</param>
    /// <param name="anchorPointInPhysicalPixels">Usually the cursor position when the tray icon was clicked.</param>
    /// <param name="cardWidthInDips">Visible card width, without the window's shadow margin.</param>
    /// <param name="cardHeightInDips">Visible card height, without the window's shadow margin.</param>
    /// <param name="gapFromTaskbarInDips">Space between the card and the taskbar (and the other work-area edges).</param>
    public static CompanionPanelCardPlacement CalculateCardPlacement(
        DisplayMonitorInfo targetMonitor,
        PhysicalPixelPoint anchorPointInPhysicalPixels,
        double cardWidthInDips,
        double cardHeightInDips,
        double gapFromTaskbarInDips)
    {
        double monitorDpiScale = targetMonitor.DpiScale;
        double cardWidthInPhysicalPixels = cardWidthInDips * monitorDpiScale;
        double cardHeightInPhysicalPixels = cardHeightInDips * monitorDpiScale;
        double gapInPhysicalPixels = gapFromTaskbarInDips * monitorDpiScale;
        PhysicalPixelRectangle workArea = targetMonitor.WorkAreaInPhysicalPixels;

        // Range the card's top-left corner may take while keeping the gap from every work-area edge.
        double minimumCardLeft = workArea.Left + gapInPhysicalPixels;
        double maximumCardLeft = workArea.Right - gapInPhysicalPixels - cardWidthInPhysicalPixels;
        double minimumCardTop = workArea.Top + gapInPhysicalPixels;
        double maximumCardTop = workArea.Bottom - gapInPhysicalPixels - cardHeightInPhysicalPixels;

        TaskbarEdge taskbarEdge = DetectTaskbarEdge(
            targetMonitor.BoundsInPhysicalPixels,
            workArea,
            anchorPointInPhysicalPixels);

        double cardCenteredOnAnchorLeft = anchorPointInPhysicalPixels.X - (cardWidthInPhysicalPixels / 2);
        double cardCenteredOnAnchorTop = anchorPointInPhysicalPixels.Y - (cardHeightInPhysicalPixels / 2);

        (double unclampedCardLeft, double unclampedCardTop) = taskbarEdge switch
        {
            TaskbarEdge.Top => (cardCenteredOnAnchorLeft, minimumCardTop),
            TaskbarEdge.Left => (minimumCardLeft, cardCenteredOnAnchorTop),
            TaskbarEdge.Right => (maximumCardLeft, cardCenteredOnAnchorTop),
            _ => (cardCenteredOnAnchorLeft, maximumCardTop),
        };

        double cardLeft = ClampPreferringMinimum(unclampedCardLeft, minimumCardLeft, maximumCardLeft);
        double cardTop = ClampPreferringMinimum(unclampedCardTop, minimumCardTop, maximumCardTop);

        return new CompanionPanelCardPlacement(
            (int)Math.Round(cardLeft),
            (int)Math.Round(cardTop),
            taskbarEdge);
    }

    /// <summary>
    /// Like Math.Clamp, but when the range is empty (a card taller or wider than the work area)
    /// it returns the minimum instead of throwing, so the card's header and close button stay on screen.
    /// </summary>
    private static double ClampPreferringMinimum(double unclampedValue, double minimumValue, double maximumValue)
    {
        return Math.Max(minimumValue, Math.Min(unclampedValue, maximumValue));
    }
}
