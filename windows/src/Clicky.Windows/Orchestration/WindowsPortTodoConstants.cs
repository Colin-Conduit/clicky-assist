namespace Clicky.Windows.Orchestration;

/// <summary>
/// Values the Windows port could not determine on its own. Each one is either tied to the
/// upstream Mac author's personal accounts or assets, or needs a product decision. They are
/// collected here so a human can fill them in; nothing else in the app hardcodes a guess.
/// </summary>
public static class WindowsPortTodoConstants
{
    /// <summary>
    /// TODO(windows-port): final wording for the line spoken with the Windows system voice when
    /// Claude or TTS fails. The Mac line asks the user to message the upstream author by name,
    /// which does not apply to this fork. This neutral placeholder is used until someone decides.
    /// </summary>
    public const string ResponseErrorFallbackSpokenLine =
        "sorry, i couldn't get an answer right now. check that the clicky worker is running and try again.";

    /// <summary>
    /// TODO(windows-port): onboarding intro video. The Mac plays an HLS stream hosted on the
    /// upstream author's Mux account at the cursor, and triggers the pointing demo 40 seconds in.
    /// WPF's MediaElement cannot play HLS, and the video belongs to the upstream project.
    /// Null means the Windows onboarding goes straight from the welcome bubble to the demo.
    /// </summary>
    public const string? OnboardingIntroVideoUrl = null;

    /// <summary>
    /// TODO(windows-port): onboarding background music. The Mac bundles ff.mp3 and plays it at 30%
    /// volume during onboarding; its license for redistribution in this fork is unknown, so it is
    /// not copied. Null means no music.
    /// </summary>
    public const string? OnboardingMusicAssetFileName = null;

    /// <summary>
    /// TODO(windows-port): where the panel's feedback button should point. The Mac panel has a
    /// "Got feedback? DM me" button linking to the upstream author's X profile. Null hides the
    /// button on Windows.
    /// </summary>
    public const string? FeedbackContactUrl = null;

    /// <summary>
    /// TODO(windows-port): analytics. The Mac sends usage events to PostHog through
    /// ClickyAnalytics.swift with the upstream project's key, and posts onboarding emails to a
    /// FormSpark form. Neither is ported; false keeps the Windows app free of telemetry until
    /// this fork chooses a provider and adds its own key on the worker side.
    /// </summary>
    public const bool IsUsageAnalyticsEnabled = false;
}
