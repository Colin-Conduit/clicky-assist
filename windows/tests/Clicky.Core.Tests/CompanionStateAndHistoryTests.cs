using Clicky.Core.Audio;
using Clicky.Core.Companion;
using Clicky.Core.Conversation;
using Clicky.Core.Pointing;
using Clicky.Core.Screen;
using Clicky.Core.Settings;

namespace Clicky.Core.Tests;

public sealed class CompanionStateAndHistoryTests : IDisposable
{
    private readonly string temporaryFolderPath = Path.Combine(Path.GetTempPath(), "clicky-settings-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(temporaryFolderPath))
        {
            Directory.Delete(temporaryFolderPath, recursive: true);
        }
    }

    [Fact]
    public void HistoryKeepsOnlyTheLastTenExchanges()
    {
        ConversationHistory conversationHistory = new();

        for (int exchangeNumber = 1; exchangeNumber <= 13; exchangeNumber++)
        {
            conversationHistory.AppendExchange($"question {exchangeNumber}", $"answer {exchangeNumber}");
        }

        IReadOnlyList<ConversationExchange> storedExchanges = conversationHistory.GetExchangesOldestFirst();
        Assert.Equal(10, storedExchanges.Count);
        Assert.Equal("question 4", storedExchanges[0].UserTranscript);
        Assert.Equal("answer 13", storedExchanges[^1].AssistantResponse);
    }

    [Fact]
    public void HistorySnapshotIsNotAffectedByLaterAppends()
    {
        ConversationHistory conversationHistory = new();
        conversationHistory.AppendExchange("first", "one");

        IReadOnlyList<ConversationExchange> snapshotBeforeAppend = conversationHistory.GetExchangesOldestFirst();
        conversationHistory.AppendExchange("second", "two");

        Assert.Single(snapshotBeforeAppend);
        Assert.Equal(2, conversationHistory.ExchangeCount);
        conversationHistory.Clear();
        Assert.Equal(0, conversationHistory.ExchangeCount);
    }

    [Theory]
    [InlineData(CompanionVoiceState.Idle, false, false, false, CompanionVoiceState.Idle)]
    [InlineData(CompanionVoiceState.Idle, true, false, false, CompanionVoiceState.Listening)]
    [InlineData(CompanionVoiceState.Listening, false, true, false, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Idle, false, false, true, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Idle, true, true, false, CompanionVoiceState.Processing)]
    [InlineData(CompanionVoiceState.Responding, true, false, false, CompanionVoiceState.Responding)]
    [InlineData(CompanionVoiceState.Responding, false, false, false, CompanionVoiceState.Responding)]
    public void VoiceStateFollowsDictationFlagsButNeverOverridesResponding(
        CompanionVoiceState currentVoiceState,
        bool isRecording,
        bool isFinalizing,
        bool isPreparing,
        CompanionVoiceState expectedVoiceState)
    {
        Assert.Equal(expectedVoiceState, CompanionVoiceStateResolver.ResolveFromDictationFlags(currentVoiceState, isRecording, isFinalizing, isPreparing));
    }

    [Fact]
    public void PanelStatusTextMatchesMacWords()
    {
        Assert.Equal("Setup", CompanionVoiceStateResolver.GetPanelStatusText(false, true, CompanionVoiceState.Idle));
        Assert.Equal("Ready", CompanionVoiceStateResolver.GetPanelStatusText(true, false, CompanionVoiceState.Idle));
        Assert.Equal("Active", CompanionVoiceStateResolver.GetPanelStatusText(true, true, CompanionVoiceState.Idle));
        Assert.Equal("Listening", CompanionVoiceStateResolver.GetPanelStatusText(true, true, CompanionVoiceState.Listening));
        Assert.Equal("Processing", CompanionVoiceStateResolver.GetPanelStatusText(true, true, CompanionVoiceState.Processing));
        Assert.Equal("Responding", CompanionVoiceStateResolver.GetPanelStatusText(true, true, CompanionVoiceState.Responding));
    }

    [Fact]
    public void SingleScreenUsesTheMacSingleScreenLabel()
    {
        Assert.Equal("user's screen (cursor is here)", ScreenCaptureLabeling.BuildScreenLabel(0, 1, isCursorScreen: true));
    }

    [Fact]
    public void MultiScreenLabelsMatchMacStrings()
    {
        // The Mac labels use an em dash (U+2014); built from its code point to keep it out of source.
        string emDash = ((char)0x2014).ToString();

        Assert.Equal(
            $"screen 1 of 2 {emDash} cursor is on this screen (primary focus)",
            ScreenCaptureLabeling.BuildScreenLabel(0, 2, isCursorScreen: true));
        Assert.Equal(
            $"screen 2 of 2 {emDash} secondary screen",
            ScreenCaptureLabeling.BuildScreenLabel(1, 2, isCursorScreen: false));
        Assert.Equal(
            $"screen 2 of 2 {emDash} secondary screen (image dimensions: 1280x720 pixels)",
            ScreenCaptureLabeling.AppendImageDimensions(ScreenCaptureLabeling.BuildScreenLabel(1, 2, false), new ScreenshotDimensions(1280, 720)));
    }

    [Fact]
    public void CursorScreenMovesFirstAndOthersKeepTheirOrder()
    {
        string[] monitorsInEnumerationOrder = ["DISPLAY1", "DISPLAY2", "DISPLAY3"];

        IReadOnlyList<string> orderedMonitors = ScreenCaptureLabeling.OrderCursorScreenFirst(
            monitorsInEnumerationOrder, monitorName => monitorName == "DISPLAY3");

        Assert.Equal(["DISPLAY3", "DISPLAY1", "DISPLAY2"], orderedMonitors);
    }

    [Fact]
    public void SettingsRoundTripAndDefaultsWhenMissing()
    {
        ClickyUserSettingsStore settingsStore = new(Path.Combine(temporaryFolderPath, "Clicky", "settings.json"));

        ClickyUserSettings defaultSettings = settingsStore.Load();
        Assert.Equal("claude-sonnet-5-5", defaultSettings.SelectedModelIdentifier);
        Assert.Equal("CtrlAlt", defaultSettings.PushToTalkShortcut);
        Assert.False(defaultSettings.HasCompletedOnboarding);
        Assert.True(defaultSettings.IsClickyCursorEnabled);

        defaultSettings.SelectedModelIdentifier = "claude-opus-5-5";
        defaultSettings.HasCompletedOnboarding = true;
        defaultSettings.PushToTalkShortcut = "CtrlAltSpace";
        settingsStore.Save(defaultSettings);

        ClickyUserSettings reloadedSettings = new ClickyUserSettingsStore(settingsStore.SettingsFilePath).Load();
        Assert.Equal("claude-opus-5-5", reloadedSettings.SelectedModelIdentifier);
        Assert.True(reloadedSettings.HasCompletedOnboarding);
        Assert.Equal(Clicky.Core.PushToTalk.PushToTalkShortcutOption.CtrlAltSpace, reloadedSettings.PushToTalkShortcutOption);
        Assert.False(File.Exists(settingsStore.SettingsFilePath + ".tmp"));
    }

    [Fact]
    public void CorruptOrStaleSettingsAreNormalized()
    {
        string settingsFilePath = Path.Combine(temporaryFolderPath, "settings.json");
        Directory.CreateDirectory(temporaryFolderPath);

        File.WriteAllText(settingsFilePath, "{ this is not json");
        Assert.Equal("claude-sonnet-5-5", new ClickyUserSettingsStore(settingsFilePath).Load().SelectedModelIdentifier);

        File.WriteAllText(settingsFilePath, "{\"selectedModel\":\"claude-sonnet-4-6\",\"pushToTalkShortcut\":\"ShiftFunction\"}");
        ClickyUserSettings normalizedSettings = new ClickyUserSettingsStore(settingsFilePath).Load();
        Assert.Equal("claude-sonnet-5-5", normalizedSettings.SelectedModelIdentifier);
        Assert.Equal("CtrlAlt", normalizedSettings.PushToTalkShortcut);
    }

    [Fact]
    public void DefaultSettingsPathIsUnderLocalAppDataClicky()
    {
        string defaultSettingsFilePath = ClickyUserSettingsStore.GetDefaultSettingsFilePath();

        Assert.EndsWith(Path.Combine("Clicky", "settings.json"), defaultSettingsFilePath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), defaultSettingsFilePath);
    }

    [Fact]
    public void AudioMeterBoostsRmsAndReleasesSlowly()
    {
        AudioPowerLevelMeter audioPowerLevelMeter = new();
        float[] loudSamples = Enumerable.Repeat(0.05f, 480).ToArray();
        float[] silentSamples = new float[480];

        double loudLevel = audioPowerLevelMeter.ApplySamples(loudSamples);
        double levelAfterSilence = audioPowerLevelMeter.ApplySamples(silentSamples);

        Assert.Equal(0.51, loudLevel, 6);
        Assert.Equal(0.51 * 0.72, levelAfterSilence, 6);
        Assert.Equal(1.0, AudioPowerLevelMeter.ComputeBoostedLevel(Enumerable.Repeat(0.9f, 10).ToArray()));
    }
}
