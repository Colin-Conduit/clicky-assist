using System.Windows;
using System.Windows.Media;

namespace Clicky.Windows.Panel;

/// <summary>
/// Windows copies of the DesignSystem.swift tokens the panel uses (DS.Colors and DS.CornerRadius),
/// under the same names. Every brush is frozen: frozen brushes can be shared by many elements
/// without WPF tracking changes on them, and they are safe to read from any thread.
/// </summary>
public static class ClickyDesignTokens
{
    /// <summary>DS.Colors as WPF colors.</summary>
    public static class Colors
    {
        /// <summary>DS.Colors.background, the panel fill.</summary>
        public static readonly Color Background = Color.FromRgb(0x10, 0x12, 0x11);

        /// <summary>DS.Colors.surface1.</summary>
        public static readonly Color Surface1 = Color.FromRgb(0x17, 0x19, 0x18);

        /// <summary>DS.Colors.borderSubtle, used for dividers and the segmented control outline.</summary>
        public static readonly Color BorderSubtle = Color.FromRgb(0x37, 0x3B, 0x39);

        /// <summary>DS.Colors.textPrimary.</summary>
        public static readonly Color TextPrimary = Color.FromRgb(0xEC, 0xEE, 0xED);

        /// <summary>DS.Colors.textSecondary.</summary>
        public static readonly Color TextSecondary = Color.FromRgb(0xAD, 0xB5, 0xB2);

        /// <summary>DS.Colors.textTertiary.</summary>
        public static readonly Color TextTertiary = Color.FromRgb(0x6B, 0x73, 0x6F);

        /// <summary>DS.Colors.textOnAccent (plain white).</summary>
        public static readonly Color TextOnAccent = Color.FromRgb(0xFF, 0xFF, 0xFF);

        /// <summary>DS.Colors.accent (Tailwind blue 600).</summary>
        public static readonly Color Accent = Color.FromRgb(0x25, 0x63, 0xEB);

        /// <summary>DS.Colors.accentHover (Tailwind blue 700).</summary>
        public static readonly Color AccentHover = Color.FromRgb(0x1D, 0x4E, 0xD8);

        /// <summary>DS.Colors.success (Tailwind emerald 400).</summary>
        public static readonly Color Success = Color.FromRgb(0x34, 0xD3, 0x99);

        /// <summary>DS.Colors.warning (Radix amber 9).</summary>
        public static readonly Color Warning = Color.FromRgb(0xFF, 0xB2, 0x24);

        /// <summary>DS.Colors.blue400, the status dot while Clicky is busy.</summary>
        public static readonly Color Blue400 = Color.FromRgb(0x60, 0xA5, 0xFA);

        /// <summary>DS.Colors.overlayCursorBlue, the blue cursor triangle (also the tray icon fill).</summary>
        public static readonly Color OverlayCursorBlue = Color.FromRgb(0x33, 0x80, 0xFF);

        // The Mac panel draws these fills inline as Color.white.opacity(...). They are named here
        // so the panel XAML never carries a hex value. Alpha is the opacity times 255, rounded.

        /// <summary>White at 6% opacity: the segmented model picker's track.</summary>
        public static readonly Color SegmentedControlFill = Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF);

        /// <summary>White at 10% opacity: the selected segment in the model picker.</summary>
        public static readonly Color SelectedSegmentFill = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

        /// <summary>White at 8% opacity: the header close button circle.</summary>
        public static readonly Color CloseButtonFill = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

        /// <summary>White at 14% opacity: the close button circle while hovered (Windows hover feedback; the Mac has none).</summary>
        public static readonly Color CloseButtonHoverFill = Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
    }

    /// <summary>Frozen brushes for every color in <see cref="Colors"/>, under the same names.</summary>
    public static class Brushes
    {
        public static readonly SolidColorBrush Background = CreateFrozenBrush(ClickyDesignTokens.Colors.Background);
        public static readonly SolidColorBrush Surface1 = CreateFrozenBrush(ClickyDesignTokens.Colors.Surface1);
        public static readonly SolidColorBrush BorderSubtle = CreateFrozenBrush(ClickyDesignTokens.Colors.BorderSubtle);
        public static readonly SolidColorBrush TextPrimary = CreateFrozenBrush(ClickyDesignTokens.Colors.TextPrimary);
        public static readonly SolidColorBrush TextSecondary = CreateFrozenBrush(ClickyDesignTokens.Colors.TextSecondary);
        public static readonly SolidColorBrush TextTertiary = CreateFrozenBrush(ClickyDesignTokens.Colors.TextTertiary);
        public static readonly SolidColorBrush TextOnAccent = CreateFrozenBrush(ClickyDesignTokens.Colors.TextOnAccent);
        public static readonly SolidColorBrush Accent = CreateFrozenBrush(ClickyDesignTokens.Colors.Accent);
        public static readonly SolidColorBrush AccentHover = CreateFrozenBrush(ClickyDesignTokens.Colors.AccentHover);
        public static readonly SolidColorBrush Success = CreateFrozenBrush(ClickyDesignTokens.Colors.Success);
        public static readonly SolidColorBrush Warning = CreateFrozenBrush(ClickyDesignTokens.Colors.Warning);
        public static readonly SolidColorBrush Blue400 = CreateFrozenBrush(ClickyDesignTokens.Colors.Blue400);
        public static readonly SolidColorBrush OverlayCursorBlue = CreateFrozenBrush(ClickyDesignTokens.Colors.OverlayCursorBlue);
        public static readonly SolidColorBrush SegmentedControlFill = CreateFrozenBrush(ClickyDesignTokens.Colors.SegmentedControlFill);
        public static readonly SolidColorBrush SelectedSegmentFill = CreateFrozenBrush(ClickyDesignTokens.Colors.SelectedSegmentFill);
        public static readonly SolidColorBrush CloseButtonFill = CreateFrozenBrush(ClickyDesignTokens.Colors.CloseButtonFill);
        public static readonly SolidColorBrush CloseButtonHoverFill = CreateFrozenBrush(ClickyDesignTokens.Colors.CloseButtonHoverFill);

        private static SolidColorBrush CreateFrozenBrush(Color brushColor)
        {
            SolidColorBrush frozenBrush = new(brushColor);
            frozenBrush.Freeze();
            return frozenBrush;
        }
    }

    /// <summary>DS.CornerRadius, in device-independent pixels.</summary>
    public static class CornerRadii
    {
        /// <summary>DS.CornerRadius.small: the segmented model picker track.</summary>
        public const double Small = 6;

        /// <summary>DS.CornerRadius.medium.</summary>
        public const double Medium = 8;

        /// <summary>DS.CornerRadius.large: the full-width Start button.</summary>
        public const double Large = 10;

        /// <summary>DS.CornerRadius.extraLarge: the panel card.</summary>
        public const double ExtraLarge = 12;
    }

    /// <summary>Fonts standing in for the Mac's San Francisco system font and SF Symbols.</summary>
    public static class Typography
    {
        /// <summary>
        /// Segoe UI Variable Text (Windows 11) with Segoe UI as the fallback. WPF walks a
        /// comma-separated family list in order, so Windows 10 machines without the variable
        /// font get Segoe UI automatically.
        /// </summary>
        public static readonly FontFamily TextFontFamily = new("Segoe UI Variable Text, Segoe UI");

        /// <summary>Segoe Fluent Icons (Windows 11) with Segoe MDL2 Assets (Windows 10) as the fallback. Both use the same code points.</summary>
        public static readonly FontFamily IconFontFamily = new("Segoe Fluent Icons, Segoe MDL2 Assets");

        /// <summary>
        /// The Mac's ".medium" weight (500). Neither Segoe UI nor the named instances of Segoe UI
        /// Variable have a 500 face, and WPF matches 500 to the Semibold face, so medium text renders
        /// as semibold. Change this one value to FontWeights.Normal for lighter copy.
        /// </summary>
        public static readonly FontWeight MediumWeight = FontWeights.Medium;
    }
}
