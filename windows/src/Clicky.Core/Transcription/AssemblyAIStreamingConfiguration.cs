using System.Text;
using System.Text.Json;

namespace Clicky.Core.Transcription;

/// <summary>
/// Connection settings for AssemblyAI's v3 streaming websocket, matching the Swift provider.
/// </summary>
public static class AssemblyAIStreamingConfiguration
{
    public const string WebsocketBaseUrl = "wss://streaming.assemblyai.com/v3/ws";
    public const int TargetSampleRateInHertz = 16_000;
    public const string AudioEncoding = "pcm_s16le";
    public const string SpeechModelIdentifier = "u3-rt-pro";

    /// <summary>
    /// Two bytes per sample, one channel. AssemblyAI accepts binary frames between 50 ms
    /// and 1000 ms of audio, so the app sends 100 ms frames and pads the final one to 50 ms.
    /// </summary>
    public const int BytesPerSecondOfAudio = TargetSampleRateInHertz * 2;
    public const int AudioFrameDurationInMilliseconds = 100;
    public const int MinimumAudioFrameDurationInMilliseconds = 50;
    public const int AudioFrameSizeInBytes = BytesPerSecondOfAudio * AudioFrameDurationInMilliseconds / 1000;
    public const int MinimumAudioFrameSizeInBytes = BytesPerSecondOfAudio * MinimumAudioFrameDurationInMilliseconds / 1000;

    /// <summary>After ForceEndpoint, how long to wait for the final Turn before using the best partial.</summary>
    public static readonly TimeSpan ExplicitFinalTranscriptGracePeriod = TimeSpan.FromSeconds(1.4);

    /// <summary>How long the dictation layer waits after key-up before giving up on this provider entirely.</summary>
    public static readonly TimeSpan FinalTranscriptFallbackDelay = TimeSpan.FromSeconds(2.8);

    /// <summary>
    /// Builds the websocket URL. It carries the temporary token, so the result must never be logged.
    /// </summary>
    public static Uri BuildWebsocketUri(string temporaryToken, IReadOnlyList<string> keyterms)
    {
        List<KeyValuePair<string, string>> queryParameters =
        [
            new("sample_rate", TargetSampleRateInHertz.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("encoding", AudioEncoding),
            new("format_turns", "true"),
            new("speech_model", SpeechModelIdentifier),
        ];

        List<string> normalizedKeyterms = keyterms
            .Select(keyterm => keyterm.Trim())
            .Where(keyterm => keyterm.Length > 0)
            .ToList();

        if (normalizedKeyterms.Count > 0)
        {
            queryParameters.Add(new("keyterms_prompt", JsonSerializer.Serialize(normalizedKeyterms)));
        }

        queryParameters.Add(new("token", temporaryToken));

        StringBuilder websocketUrlBuilder = new(WebsocketBaseUrl);
        for (int queryParameterIndex = 0; queryParameterIndex < queryParameters.Count; queryParameterIndex++)
        {
            KeyValuePair<string, string> queryParameter = queryParameters[queryParameterIndex];
            websocketUrlBuilder.Append(queryParameterIndex == 0 ? '?' : '&');
            websocketUrlBuilder.Append(Uri.EscapeDataString(queryParameter.Key));
            websocketUrlBuilder.Append('=');
            websocketUrlBuilder.Append(Uri.EscapeDataString(queryParameter.Value));
        }

        return new Uri(websocketUrlBuilder.ToString());
    }
}

/// <summary>
/// Words AssemblyAI should bias toward. The base list is the Mac app's list unchanged;
/// contextual terms are appended, then everything is trimmed and de-duplicated
/// case-insensitively while keeping first-seen order.
/// </summary>
public static class TranscriptionKeyterms
{
    public static IReadOnlyList<string> BaseKeyterms { get; } =
    [
        "makesomething",
        "Learning Buddy",
        "Codex",
        "Claude",
        "Anthropic",
        "OpenAI",
        "SwiftUI",
        "Xcode",
        "Vercel",
        "Next.js",
        "localhost",
    ];

    public static IReadOnlyList<string> BuildKeyterms(IReadOnlyList<string> contextualKeyterms)
    {
        HashSet<string> seenNormalizedKeyterms = new(StringComparer.Ordinal);
        List<string> orderedKeyterms = [];

        foreach (string candidateKeyterm in BaseKeyterms.Concat(contextualKeyterms))
        {
            string trimmedKeyterm = candidateKeyterm.Trim();
            if (trimmedKeyterm.Length == 0)
            {
                continue;
            }

            if (seenNormalizedKeyterms.Add(trimmedKeyterm.ToLowerInvariant()))
            {
                orderedKeyterms.Add(trimmedKeyterm);
            }
        }

        return orderedKeyterms;
    }
}
