using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Clicky.Core.TextToSpeech;

/// <summary>Thrown when the worker's /tts route returns a non-2xx status.</summary>
public sealed class TextToSpeechRequestException : Exception
{
    public TextToSpeechRequestException(string message, int httpStatusCode)
        : base(message)
    {
        HttpStatusCode = httpStatusCode;
    }

    public int HttpStatusCode { get; }
}

/// <summary>
/// Fetches spoken audio for a reply from the worker's /tts route (ElevenLabs behind it).
/// Port of the network half of ElevenLabsTTSClient.swift; playback lives in the Windows app.
/// The voice is chosen by the worker (ELEVENLABS_VOICE_ID), the model and voice settings here.
/// </summary>
public sealed class ElevenLabsSpeechClient
{
    public const string ElevenLabsModelIdentifier = "eleven_flash_v2_5";
    public const double VoiceStability = 0.5;
    public const double VoiceSimilarityBoost = 0.75;

    /// <summary>Matches the Mac session's 60 second resource timeout.</summary>
    public static readonly TimeSpan MaximumRequestDuration = TimeSpan.FromSeconds(60);

    private readonly HttpClient sharedHttpClient;
    private readonly Uri textToSpeechEndpointUrl;

    public ElevenLabsSpeechClient(HttpClient sharedHttpClient, Uri textToSpeechEndpointUrl)
    {
        this.sharedHttpClient = sharedHttpClient;
        this.textToSpeechEndpointUrl = textToSpeechEndpointUrl;
    }

    /// <summary>Returns the MP3 bytes for <paramref name="textToSpeak"/>.</summary>
    public async Task<byte[]> FetchSpeechAudioAsync(string textToSpeak, CancellationToken cancellationToken)
    {
        using HttpRequestMessage textToSpeechRequest = new(HttpMethod.Post, textToSpeechEndpointUrl)
        {
            Content = new StringContent(BuildRequestBodyJson(textToSpeak), Encoding.UTF8, "application/json"),
        };
        textToSpeechRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));

        using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(MaximumRequestDuration);

        using HttpResponseMessage textToSpeechResponse = await sharedHttpClient
            .SendAsync(textToSpeechRequest, requestTimeout.Token)
            .ConfigureAwait(false);

        if (!textToSpeechResponse.IsSuccessStatusCode)
        {
            string errorResponseBody = await textToSpeechResponse.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);
            int httpStatusCode = (int)textToSpeechResponse.StatusCode;
            throw new TextToSpeechRequestException($"TTS request failed (HTTP {httpStatusCode}): {errorResponseBody}", httpStatusCode);
        }

        return await textToSpeechResponse.Content.ReadAsByteArrayAsync(requestTimeout.Token).ConfigureAwait(false);
    }

    /// <summary>Same body as the Mac app: text, model_id and voice_settings.</summary>
    public static string BuildRequestBodyJson(string textToSpeak)
    {
        var requestBody = new
        {
            text = textToSpeak,
            model_id = ElevenLabsModelIdentifier,
            voice_settings = new
            {
                stability = VoiceStability,
                similarity_boost = VoiceSimilarityBoost,
            },
        };

        return JsonSerializer.Serialize(requestBody);
    }
}
