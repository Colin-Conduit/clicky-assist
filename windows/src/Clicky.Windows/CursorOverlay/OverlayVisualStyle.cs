using System.Windows;
using System.Windows.Media;

namespace Clicky.Windows.CursorOverlay;

/// <summary>
/// Colors and fonts for the cursor overlay, taken from DesignSystem.swift (DS.Colors) on the Mac.
/// Brushes are frozen so every overlay window can share them without per-window copies.
/// </summary>
internal static class OverlayVisualStyle
{
    /// <summary>DS.Colors.overlayCursorBlue (#3380FF): the triangle, waveform, spinner and blue bubbles.</summary>
    public static readonly Color OverlayCursorBlueColor = Color.FromRgb(0x33, 0x80, 0xFF);

    /// <summary>DS.Colors.surface1 (#171918): response bubble fill.</summary>
    public static readonly Color ResponseBubbleSurfaceColor = Color.FromRgb(0x17, 0x19, 0x18);

    /// <summary>DS.Colors.borderSubtle (#373B39): response bubble outline.</summary>
    public static readonly Color ResponseBubbleBorderColor = Color.FromRgb(0x37, 0x3B, 0x39);

    /// <summary>DS.Colors.textPrimary (#ECEEED): response bubble text.</summary>
    public static readonly Color ResponseBubbleTextColor = Color.FromRgb(0xEC, 0xEE, 0xED);

    public static readonly SolidColorBrush OverlayCursorBlueBrush = CreateFrozenSolidBrush(OverlayCursorBlueColor, 1.0);

    public static readonly SolidColorBrush BlueBubbleTextBrush = CreateFrozenSolidBrush(Colors.White, 1.0);

    /// <summary>Mac: DS.Colors.surface1.opacity(0.95).</summary>
    public static readonly SolidColorBrush ResponseBubbleSurfaceBrush = CreateFrozenSolidBrush(ResponseBubbleSurfaceColor, 0.95);

    /// <summary>Mac: DS.Colors.borderSubtle.opacity(0.5).</summary>
    public static readonly SolidColorBrush ResponseBubbleBorderBrush = CreateFrozenSolidBrush(ResponseBubbleBorderColor, 0.5);

    public static readonly SolidColorBrush ResponseBubbleTextBrush = CreateFrozenSolidBrush(ResponseBubbleTextColor, 1.0);

    /// <summary>
    /// The Mac uses the system font (SF Pro). The closest Windows 11 match is Segoe UI Variable Text;
    /// WPF falls back to the next family in a comma-separated list when one is not installed,
    /// so Windows 10 machines get Segoe UI.
    /// </summary>
    public static readonly FontFamily CompanionBubbleFontFamily = new("Segoe UI Variable Text, Segoe UI");

    public static SolidColorBrush CreateFrozenSolidBrush(Color brushColor, double brushOpacity)
    {
        SolidColorBrush frozenBrush = new(brushColor) { Opacity = brushOpacity };
        frozenBrush.Freeze();
        return frozenBrush;
    }

    /// <summary>
    /// Rounds a DIP coordinate to the nearest whole physical pixel. Bubbles hold text, and text
    /// placed at a fractional pixel offset renders noticeably blurrier.
    /// </summary>
    public static double SnapToWholeDevicePixel(double coordinateInDips, double windowDpiScale)
    {
        return Math.Round(coordinateInDips * windowDpiScale) / windowDpiScale;
    }

    /// <summary>Size used to ask a bubble how big it wants to be with no constraints.</summary>
    public static readonly Size UnconstrainedMeasureSize = new(double.PositiveInfinity, double.PositiveInfinity);
}
