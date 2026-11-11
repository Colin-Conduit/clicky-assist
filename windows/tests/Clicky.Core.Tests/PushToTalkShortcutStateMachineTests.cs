using Clicky.Core.PushToTalk;
using static Clicky.Core.PushToTalk.PushToTalkVirtualKeyCodes;

namespace Clicky.Core.Tests;

public sealed class PushToTalkShortcutStateMachineTests
{
    private const int LetterAKey = 0x41;

    [Fact]
    public void CtrlAltPressesWhenBothAreHeldAndReleasesWhenEitherGoesUp()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: false));
        Assert.False(stateMachine.IsShortcutCurrentlyPressed);
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftControl, isKeyDown: false));
    }

    [Fact]
    public void OrderOfModifierPressesDoesNotMatter()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(RightAlt, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(RightControl, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(RightControl, isKeyDown: false));
    }

    [Fact]
    public void AutoRepeatKeyDownsDoNotPressTwice()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);

        for (int repeatIndex = 0; repeatIndex < 20; repeatIndex++)
        {
            Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
            Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true));
        }

        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
    }

    [Fact]
    public void ExtraModifiersAndOtherKeysDoNotBreakAnActivePress()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftShift, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);

        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LetterAKey, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LetterAKey, isKeyDown: false));
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftShift, isKeyDown: false));
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
    }

    [Fact]
    public void ReleasingOneSideWhileTheOtherSideIsHeldKeepsThePress()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(RightControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftControl, isKeyDown: false));
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(RightControl, isKeyDown: false));
    }

    [Fact]
    public void AltGrReportedAsLeftCtrlPlusRightAltCountsAsCtrlAlt()
    {
        // On AltGr layouts Windows sends a synthetic Left Ctrl down before Right Alt down.
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);

        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(RightAlt, isKeyDown: true));
    }

    [Fact]
    public void GenericAndSideSpecificCodesForTheSameModifierAreReconciled()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(Control, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);

        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(LeftControl, isKeyDown: false));
    }

    [Fact]
    public void ShiftCtrlUsesItsOwnModifiers()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.ShiftCtrl);

        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(RightShift, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(RightShift, isKeyDown: false));
    }

    [Fact]
    public void SpaceShortcutPressesOnSpaceDownAndReleasesOnSpaceUp()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAltSpace);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(Space, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(Space, isKeyDown: true));

        // Like the Mac, releasing a modifier before Space does not end the press.
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: false));
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.HandleKeyEvent(Space, isKeyDown: false));
    }

    [Fact]
    public void SpaceWithoutModifiersDoesNothing()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAltSpace);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(Space, isKeyDown: true));
        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(Space, isKeyDown: false));
    }

    [Fact]
    public void ReconcileReleasesWhenSecureDesktopSwallowedTheKeyUp()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);

        // Ctrl + Alt + Del: the hook never sees the key-ups, but the OS reports nothing held.
        PushToTalkShortcutTransition reconcileTransition = stateMachine.ReconcileWithPhysicalKeyState(_ => false);

        Assert.Equal(PushToTalkShortcutTransition.Released, reconcileTransition);
        Assert.False(stateMachine.IsShortcutCurrentlyPressed);

        // State was cleaned, so the next real press works normally.
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        Assert.Equal(PushToTalkShortcutTransition.Pressed, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
    }

    [Fact]
    public void ReconcileKeepsPressWhileKeysAreStillDown()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);

        PushToTalkShortcutTransition reconcileTransition = stateMachine.ReconcileWithPhysicalKeyState(
            virtualKeyCode => virtualKeyCode is LeftControl or LeftAlt);

        Assert.Equal(PushToTalkShortcutTransition.None, reconcileTransition);
        Assert.True(stateMachine.IsShortcutCurrentlyPressed);
    }

    [Fact]
    public void ReconcileDropsStaleModifiersEvenWhenNotPressed()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);

        // Left Ctrl's key-up was lost. Without cleanup, pressing Alt alone would start push-to-talk.
        stateMachine.ReconcileWithPhysicalKeyState(_ => false);

        Assert.Equal(PushToTalkShortcutTransition.None, stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true));
    }

    [Fact]
    public void ChangingShortcutWhilePressedReleases()
    {
        PushToTalkShortcutStateMachine stateMachine = new(PushToTalkShortcutOption.CtrlAlt);
        stateMachine.HandleKeyEvent(LeftControl, isKeyDown: true);
        stateMachine.HandleKeyEvent(LeftAlt, isKeyDown: true);

        Assert.Equal(PushToTalkShortcutTransition.Released, stateMachine.ChangeShortcutOption(PushToTalkShortcutOption.CtrlAltSpace));
        Assert.Equal(PushToTalkShortcutOption.CtrlAltSpace, stateMachine.ShortcutOption);
    }

    [Theory]
    [InlineData("CtrlAlt", PushToTalkShortcutOption.CtrlAlt)]
    [InlineData("ctrlaltspace", PushToTalkShortcutOption.CtrlAltSpace)]
    [InlineData(" ShiftCtrl ", PushToTalkShortcutOption.ShiftCtrl)]
    [InlineData("ShiftFunction", PushToTalkShortcutOption.CtrlAlt)]
    [InlineData("99", PushToTalkShortcutOption.CtrlAlt)]
    [InlineData(null, PushToTalkShortcutOption.CtrlAlt)]
    public void SettingsValuesParseWithDefaultFallback(string? settingsValue, PushToTalkShortcutOption expectedShortcutOption)
    {
        Assert.Equal(expectedShortcutOption, PushToTalkShortcutDefinitions.ParseSettingsValueOrDefault(settingsValue));
    }

    [Fact]
    public void DisplayTextUsesWindowsKeyNames()
    {
        Assert.Equal("Ctrl + Alt", PushToTalkShortcutDefinitions.GetDisplayText(PushToTalkShortcutOption.CtrlAlt));
        Assert.Equal("Ctrl + Alt + Space", PushToTalkShortcutDefinitions.GetDisplayText(PushToTalkShortcutOption.CtrlAltSpace));
    }
}
