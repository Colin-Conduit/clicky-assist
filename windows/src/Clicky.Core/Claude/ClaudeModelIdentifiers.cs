namespace Clicky.Core.Claude;

/// <summary>
/// A model choice shown in the panel's segmented picker.
/// </summary>
public sealed record ClaudeModelPickerOption(string PickerLabel, string ModelIdentifier);

/// <summary>
/// The only place model identifiers live. The worker's /chat route forwards the
/// request body to Anthropic unchanged, so whatever identifier the client sends
/// here is the model that answers.
/// </summary>
public static class ClaudeModelIdentifiers
{
    /// <summary>Default model for voice responses.</summary>
    public const string DefaultSonnetModelIdentifier = "claude-sonnet-5-5";

    /// <summary>Optional, slower and more capable model offered in the picker.</summary>
    public const string OpusModelIdentifier = "claude-opus-5-5";

    /// <summary>Picker options in display order, mirroring the Mac panel (Sonnet, then Opus).</summary>
    public static IReadOnlyList<ClaudeModelPickerOption> PickerOptions { get; } =
    [
        new ClaudeModelPickerOption("Sonnet", DefaultSonnetModelIdentifier),
        new ClaudeModelPickerOption("Opus", OpusModelIdentifier),
    ];

    public static bool IsKnownModelIdentifier(string? candidateModelIdentifier)
    {
        return PickerOptions.Any(pickerOption => pickerOption.ModelIdentifier == candidateModelIdentifier);
    }

    /// <summary>
    /// Returns the stored identifier when it is one of the picker options, otherwise the
    /// default. Protects against a settings file left behind by an older build that
    /// stored a model identifier this build no longer offers.
    /// </summary>
    public static string NormalizeModelIdentifier(string? storedModelIdentifier)
    {
        return IsKnownModelIdentifier(storedModelIdentifier)
            ? storedModelIdentifier!
            : DefaultSonnetModelIdentifier;
    }
}
