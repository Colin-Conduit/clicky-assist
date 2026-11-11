using System.Net.Http;
using Clicky.Core.Diagnostics;

namespace Clicky.Core.Transcription;

/// <summary>
/// Creates AssemblyAI streaming sessions. Port of AssemblyAIStreamingTranscriptionProvider.swift.
///
/// The Swift provider keeps one long-lived URLSession for every websocket because creating
/// and tearing down a session per push-to-talk corrupted the OS connection pool after a few
/// rapid presses. The .NET equivalent is one shared <see cref="SocketsHttpHandler"/> wrapped
/// in an <see cref="HttpMessageInvoker"/> that every <c>ClientWebSocket.ConnectAsync</c> call
/// reuses, instead of each ClientWebSocket building its own handler.
/// </summary>
public sealed class AssemblyAIStreamingTranscriptionProvider : IDisposable
{
    public const string ProviderDisplayName = "AssemblyAI";
    private const string LogCategory = "AssemblyAI";

    private readonly AssemblyAITemporaryTokenClient temporaryTokenClient;
    private readonly HttpMessageInvoker sharedWebSocketMessageInvoker;

    public AssemblyAIStreamingTranscriptionProvider(AssemblyAITemporaryTokenClient temporaryTokenClient)
    {
        this.temporaryTokenClient = temporaryTokenClient;

        SocketsHttpHandler sharedWebSocketHandler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        sharedWebSocketMessageInvoker = new HttpMessageInvoker(sharedWebSocketHandler, disposeHandler: true);
    }

    public string DisplayName => ProviderDisplayName;

    /// <summary>
    /// Fetches a fresh temporary token, opens the websocket, and waits for AssemblyAI's Begin message.
    /// </summary>
    public async Task<AssemblyAIStreamingTranscriptionSession> StartStreamingSessionAsync(
        IReadOnlyList<string> keyterms,
        Action<string> onTranscriptUpdate,
        Action<string> onFinalTranscriptReady,
        Action<Exception> onError,
        CancellationToken cancellationToken)
    {
        string temporaryToken = await temporaryTokenClient.FetchTemporaryTokenAsync(cancellationToken).ConfigureAwait(false);
        ClickyDiagnosticLog.Write(LogCategory, $"Fetched temporary token (length {temporaryToken.Length}).");

        Uri websocketUriContainingToken = AssemblyAIStreamingConfiguration.BuildWebsocketUri(temporaryToken, keyterms);

        AssemblyAIStreamingTranscriptionSession streamingSession = new(
            websocketUriContainingToken,
            sharedWebSocketMessageInvoker,
            onTranscriptUpdate,
            onFinalTranscriptReady,
            onError);

        try
        {
            await streamingSession.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await streamingSession.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return streamingSession;
    }

    public void Dispose()
    {
        sharedWebSocketMessageInvoker.Dispose();
    }
}
