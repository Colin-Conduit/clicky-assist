using System.Text.Json;

namespace Clicky.Core.Transcription;

/// <summary>Thrown when the worker cannot hand out an AssemblyAI streaming token.</summary>
public sealed class TranscriptionTokenException : Exception
{
    public TranscriptionTokenException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Fetches a short-lived AssemblyAI streaming token from the worker's /transcribe-token
/// route. The real AssemblyAI key stays on the worker. The token is a credential for
/// 480 seconds, so it is never written to logs; callers may log its length only.
/// </summary>
public sealed class AssemblyAITemporaryTokenClient
{
    private static readonly TimeSpan MaximumRequestDuration = TimeSpan.FromSeconds(15);

    private readonly HttpClient sharedHttpClient;
    private readonly Uri transcribeTokenEndpointUrl;

    public AssemblyAITemporaryTokenClient(HttpClient sharedHttpClient, Uri transcribeTokenEndpointUrl)
    {
        this.sharedHttpClient = sharedHttpClient;
        this.transcribeTokenEndpointUrl = transcribeTokenEndpointUrl;
    }

    public async Task<string> FetchTemporaryTokenAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(MaximumRequestDuration);

        // The worker only accepts POST on every route, same as the Mac client uses.
        using HttpRequestMessage tokenRequest = new(HttpMethod.Post, transcribeTokenEndpointUrl)
        {
            Content = new ByteArrayContent([]),
        };

        using HttpResponseMessage tokenResponse = await sharedHttpClient.SendAsync(tokenRequest, requestTimeout.Token).ConfigureAwait(false);
        string tokenResponseBody = await tokenResponse.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);

        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new TranscriptionTokenException(
                $"Failed to fetch AssemblyAI token (HTTP {(int)tokenResponse.StatusCode}): {tokenResponseBody}");
        }

        return ParseTokenFromResponseBody(tokenResponseBody);
    }

    /// <summary>The worker relays AssemblyAI's JSON, which looks like {"token":"...","expires_in_seconds":480}.</summary>
    public static string ParseTokenFromResponseBody(string tokenResponseBody)
    {
        try
        {
            using JsonDocument tokenDocument = JsonDocument.Parse(tokenResponseBody);
            if (tokenDocument.RootElement.ValueKind == JsonValueKind.Object
                && tokenDocument.RootElement.TryGetProperty("token", out JsonElement tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                return tokenElement.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new TranscriptionTokenException("Invalid token response from proxy.");
    }
}
