namespace Clicky.Core.PushToTalk;

/// <summary>
/// Turns raw key-down and key-up events from the low-level keyboard hook into
/// push-to-talk pressed and released transitions. Port of
/// BuddyPushToTalkShortcut.shortcutTransition on the Mac.
///
/// Rules, same as the Mac:
/// <list type="bullet">
/// <item>Modifier-only shortcuts (Ctrl + Alt): pressed when every required modifier is held
/// (extra modifiers are fine), released as soon as any required modifier goes up.</item>
/// <item>Space shortcuts (Ctrl + Alt + Space): pressed on Space down while the modifiers are held,
/// released on Space up. Letting go of a modifier first does not release.</item>
/// </list>
///
/// Windows-specific:
/// <list type="bullet">
/// <item>Holding a key auto-repeats key-down events; repeats never produce a second Pressed.</item>
/// <item>On keyboard layouts with an AltGr key (German, French, Polish and others), pressing AltGr
/// makes Windows report Left Ctrl + Right Alt. AltGr alone therefore satisfies Ctrl + Alt and starts
/// push-to-talk, and typing AltGr characters (for example @ or the euro sign) does too. Users on those
/// layouts can switch to a Space shortcut in settings.json.</item>
/// <item>Ctrl + Alt + Del, Win + L and UAC prompts switch to the secure desktop, which swallows the
/// key-up events. <see cref="ReconcileWithPhysicalKeyState"/> releases a stuck shortcut by checking
/// the real key state.</item>
/// </list>
/// Not thread-safe; the keyboard hook thread owns it.
/// </summary>
public sealed class PushToTalkShortcutStateMachine
{
    private readonly HashSet<int> heldModifierVirtualKeyCodes = [];
    private PushToTalkShortcutOption currentShortcutOption;

    public PushToTalkShortcutStateMachine(PushToTalkShortcutOption shortcutOption)
    {
        currentShortcutOption = shortcutOption;
    }

    public PushToTalkShortcutOption ShortcutOption => currentShortcutOption;

    public bool IsShortcutCurrentlyPressed { get; private set; }

    /// <summary>
    /// Switches to a different shortcut. Returns Released if the old shortcut was held,
    /// so the caller can end the in-progress recording.
    /// </summary>
    public PushToTalkShortcutTransition ChangeShortcutOption(PushToTalkShortcutOption newShortcutOption)
    {
        currentShortcutOption = newShortcutOption;
        if (!IsShortcutCurrentlyPressed)
        {
            return PushToTalkShortcutTransition.None;
        }

        IsShortcutCurrentlyPressed = false;
        return PushToTalkShortcutTransition.Released;
    }

    public PushToTalkShortcutTransition HandleKeyEvent(int virtualKeyCode, bool isKeyDown)
    {
        PushToTalkModifierKeys modifierForKey = PushToTalkVirtualKeyCodes.GetModifierForVirtualKeyCode(virtualKeyCode);
        bool isModifierKeyEvent = modifierForKey != PushToTalkModifierKeys.None;

        if (isModifierKeyEvent)
        {
            if (isKeyDown)
            {
                heldModifierVirtualKeyCodes.Add(virtualKeyCode);
            }
            else
            {
                ReleaseModifierKeyCode(virtualKeyCode);
            }
        }

        if (PushToTalkShortcutDefinitions.RequiresSpaceKey(currentShortcutOption))
        {
            return HandleKeyEventForSpaceShortcut(virtualKeyCode, isKeyDown);
        }

        // Modifier-only shortcuts are evaluated only on modifier changes, like the Mac's flagsChanged.
        return isModifierKeyEvent
            ? EvaluateModifierOnlyShortcut()
            : PushToTalkShortcutTransition.None;
    }

    /// <summary>
    /// Compares what the state machine believes is held with what the OS says is physically
    /// down, dropping keys whose key-up was never delivered. Returns Released when that ends
    /// an active press. Call this periodically while the shortcut is pressed.
    /// </summary>
    public PushToTalkShortcutTransition ReconcileWithPhysicalKeyState(Func<int, bool> isVirtualKeyPhysicallyDown)
    {
        heldModifierVirtualKeyCodes.RemoveWhere(heldVirtualKeyCode => !isVirtualKeyPhysicallyDown(heldVirtualKeyCode));

        if (!IsShortcutCurrentlyPressed)
        {
            return PushToTalkShortcutTransition.None;
        }

        bool isShortcutStillPhysicallyHeld = PushToTalkShortcutDefinitions.RequiresSpaceKey(currentShortcutOption)
            ? isVirtualKeyPhysicallyDown(PushToTalkVirtualKeyCodes.Space)
            : AreRequiredModifiersHeld();

        if (isShortcutStillPhysicallyHeld)
        {
            return PushToTalkShortcutTransition.None;
        }

        IsShortcutCurrentlyPressed = false;
        return PushToTalkShortcutTransition.Released;
    }

    /// <summary>Forgets all key state, for example when the hook is reinstalled.</summary>
    public void Reset()
    {
        heldModifierVirtualKeyCodes.Clear();
        IsShortcutCurrentlyPressed = false;
    }

    private PushToTalkShortcutTransition EvaluateModifierOnlyShortcut()
    {
        bool areRequiredModifiersHeld = AreRequiredModifiersHeld();

        if (areRequiredModifiersHeld && !IsShortcutCurrentlyPressed)
        {
            IsShortcutCurrentlyPressed = true;
            return PushToTalkShortcutTransition.Pressed;
        }

        if (!areRequiredModifiersHeld && IsShortcutCurrentlyPressed)
        {
            IsShortcutCurrentlyPressed = false;
            return PushToTalkShortcutTransition.Released;
        }

        return PushToTalkShortcutTransition.None;
    }

    private PushToTalkShortcutTransition HandleKeyEventForSpaceShortcut(int virtualKeyCode, bool isKeyDown)
    {
        if (virtualKeyCode != PushToTalkVirtualKeyCodes.Space)
        {
            return PushToTalkShortcutTransition.None;
        }

        if (isKeyDown && !IsShortcutCurrentlyPressed && AreRequiredModifiersHeld())
        {
            IsShortcutCurrentlyPressed = true;
            return PushToTalkShortcutTransition.Pressed;
        }

        if (!isKeyDown && IsShortcutCurrentlyPressed)
        {
            IsShortcutCurrentlyPressed = false;
            return PushToTalkShortcutTransition.Released;
        }

        return PushToTalkShortcutTransition.None;
    }

    private bool AreRequiredModifiersHeld()
    {
        PushToTalkModifierKeys requiredModifierKeys = PushToTalkShortcutDefinitions.GetRequiredModifierKeys(currentShortcutOption);

        foreach (PushToTalkModifierKeys singleModifierKey in new[] { PushToTalkModifierKeys.Control, PushToTalkModifierKeys.Alt, PushToTalkModifierKeys.Shift })
        {
            if (!requiredModifierKeys.HasFlag(singleModifierKey))
            {
                continue;
            }

            bool isAnyVariantOfModifierHeld = PushToTalkVirtualKeyCodes
                .GetVirtualKeyCodesForModifier(singleModifierKey)
                .Any(heldModifierVirtualKeyCodes.Contains);

            if (!isAnyVariantOfModifierHeld)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Injected input sometimes reports the generic code (VK_CONTROL) on key-down and the
    /// side-specific code (VK_LCONTROL) on key-up, or the reverse. A key-up for one form
    /// therefore also clears the generic form of the same modifier.
    /// </summary>
    private void ReleaseModifierKeyCode(int releasedVirtualKeyCode)
    {
        heldModifierVirtualKeyCodes.Remove(releasedVirtualKeyCode);

        PushToTalkModifierKeys releasedModifier = PushToTalkVirtualKeyCodes.GetModifierForVirtualKeyCode(releasedVirtualKeyCode);
        int genericVirtualKeyCodeForModifier = releasedModifier switch
        {
            PushToTalkModifierKeys.Control => PushToTalkVirtualKeyCodes.Control,
            PushToTalkModifierKeys.Alt => PushToTalkVirtualKeyCodes.Alt,
            PushToTalkModifierKeys.Shift => PushToTalkVirtualKeyCodes.Shift,
            _ => 0,
        };

        bool isReleasedKeyGeneric = releasedVirtualKeyCode == genericVirtualKeyCodeForModifier;
        if (isReleasedKeyGeneric)
        {
            foreach (int modifierVariantVirtualKeyCode in PushToTalkVirtualKeyCodes.GetVirtualKeyCodesForModifier(releasedModifier))
            {
                heldModifierVirtualKeyCodes.Remove(modifierVariantVirtualKeyCode);
            }
        }
        else if (genericVirtualKeyCodeForModifier != 0)
        {
            heldModifierVirtualKeyCodes.Remove(genericVirtualKeyCodeForModifier);
        }
    }
}
