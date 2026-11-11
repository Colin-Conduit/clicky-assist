namespace Clicky.Core.PushToTalk;

/// <summary>
/// The push-to-talk shortcuts a user can pick. Windows versions of the Mac options:
/// Control maps to Ctrl, Option maps to Alt. The Mac's Shift + Fn has no Windows
/// equivalent (the Fn key is handled in keyboard firmware and never reaches the OS),
/// so it is not offered.
/// </summary>
public enum PushToTalkShortcutOption
{
    /// <summary>Hold Ctrl and Alt. The default, like Control + Option on the Mac.</summary>
    CtrlAlt,
    ShiftCtrl,
    CtrlAltSpace,
    ShiftCtrlSpace,
}

[Flags]
public enum PushToTalkModifierKeys
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
}

public enum PushToTalkShortcutTransition
{
    None,
    Pressed,
    Released,
}

/// <summary>Win32 virtual-key codes the push-to-talk logic cares about.</summary>
public static class PushToTalkVirtualKeyCodes
{
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Alt = 0x12;
    public const int Space = 0x20;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftAlt = 0xA4;
    public const int RightAlt = 0xA5;

    public static IReadOnlyList<int> GetVirtualKeyCodesForModifier(PushToTalkModifierKeys singleModifierKey)
    {
        return singleModifierKey switch
        {
            PushToTalkModifierKeys.Control => [LeftControl, RightControl, Control],
            PushToTalkModifierKeys.Alt => [LeftAlt, RightAlt, Alt],
            PushToTalkModifierKeys.Shift => [LeftShift, RightShift, Shift],
            _ => [],
        };
    }

    /// <summary>Returns the modifier a key code belongs to, or None for non-modifier keys.</summary>
    public static PushToTalkModifierKeys GetModifierForVirtualKeyCode(int virtualKeyCode)
    {
        return virtualKeyCode switch
        {
            LeftControl or RightControl or Control => PushToTalkModifierKeys.Control,
            LeftAlt or RightAlt or Alt => PushToTalkModifierKeys.Alt,
            LeftShift or RightShift or Shift => PushToTalkModifierKeys.Shift,
            _ => PushToTalkModifierKeys.None,
        };
    }
}

/// <summary>Display text, required keys and settings serialization for each shortcut option.</summary>
public static class PushToTalkShortcutDefinitions
{
    public const PushToTalkShortcutOption DefaultShortcutOption = PushToTalkShortcutOption.CtrlAlt;

    public static IReadOnlyList<PushToTalkShortcutOption> AllShortcutOptions { get; } =
    [
        PushToTalkShortcutOption.CtrlAlt,
        PushToTalkShortcutOption.ShiftCtrl,
        PushToTalkShortcutOption.CtrlAltSpace,
        PushToTalkShortcutOption.ShiftCtrlSpace,
    ];

    /// <summary>Human-readable form for the panel hint, for example "Ctrl + Alt".</summary>
    public static string GetDisplayText(PushToTalkShortcutOption shortcutOption)
    {
        return string.Join(" + ", GetKeyCapsuleLabels(shortcutOption));
    }

    public static IReadOnlyList<string> GetKeyCapsuleLabels(PushToTalkShortcutOption shortcutOption)
    {
        return shortcutOption switch
        {
            PushToTalkShortcutOption.CtrlAlt => ["Ctrl", "Alt"],
            PushToTalkShortcutOption.ShiftCtrl => ["Shift", "Ctrl"],
            PushToTalkShortcutOption.CtrlAltSpace => ["Ctrl", "Alt", "Space"],
            PushToTalkShortcutOption.ShiftCtrlSpace => ["Shift", "Ctrl", "Space"],
            _ => throw new ArgumentOutOfRangeException(nameof(shortcutOption), shortcutOption, null),
        };
    }

    public static PushToTalkModifierKeys GetRequiredModifierKeys(PushToTalkShortcutOption shortcutOption)
    {
        return shortcutOption switch
        {
            PushToTalkShortcutOption.CtrlAlt or PushToTalkShortcutOption.CtrlAltSpace => PushToTalkModifierKeys.Control | PushToTalkModifierKeys.Alt,
            PushToTalkShortcutOption.ShiftCtrl or PushToTalkShortcutOption.ShiftCtrlSpace => PushToTalkModifierKeys.Shift | PushToTalkModifierKeys.Control,
            _ => throw new ArgumentOutOfRangeException(nameof(shortcutOption), shortcutOption, null),
        };
    }

    /// <summary>True for the "+ Space" variants, which press on Space down and release on Space up.</summary>
    public static bool RequiresSpaceKey(PushToTalkShortcutOption shortcutOption)
    {
        return shortcutOption is PushToTalkShortcutOption.CtrlAltSpace or PushToTalkShortcutOption.ShiftCtrlSpace;
    }

    /// <summary>Settings file value, for example "CtrlAlt".</summary>
    public static string ToSettingsValue(PushToTalkShortcutOption shortcutOption)
    {
        return shortcutOption.ToString();
    }

    /// <summary>Parses a settings value case-insensitively; unknown or empty values give the default.</summary>
    public static PushToTalkShortcutOption ParseSettingsValueOrDefault(string? settingsValue)
    {
        if (!string.IsNullOrWhiteSpace(settingsValue)
            && Enum.TryParse(settingsValue.Trim(), ignoreCase: true, out PushToTalkShortcutOption parsedShortcutOption)
            && Enum.IsDefined(parsedShortcutOption))
        {
            return parsedShortcutOption;
        }

        return DefaultShortcutOption;
    }
}
