using Clicky.Windows.Diagnostics;
using Clicky.Windows.Orchestration;

namespace Clicky.Windows.Debugging;

/// <summary>
/// Debug modes that talk to the worker:
/// --token-test fetches an AssemblyAI streaming token and prints only its length;
/// --ask "text" runs one complete turn with typed text in place of speech (screenshots,
/// streamed Claude reply printed to the console, POINT flight, and spoken reply).
/// </summary>
public static class WorkerTurnTestRunner
{
    private const string LogCategory = "WorkerTest";

    public static async Task<int> RunTokenTestAsync(ClickyApplicationServices applicationServices)
    {
        try
        {
            string temporaryToken = await applicationServices.TranscriptionTokenClient.FetchTemporaryTokenAsync(CancellationToken.None);
            ClickyApplicationLog.Write(LogCategory, $"Transcribe token fetched from {applicationServices.WorkerConfiguration.TranscribeTokenEndpointUrl} (length {temporaryToken.Length}).");
            return 0;
        }
        catch (Exception tokenException)
        {
            ClickyApplicationLog.Write(LogCategory, $"Transcribe token request failed: {tokenException.GetType().Name}: {tokenException.Message}");
            return 1;
        }
    }

    public static async Task<int> RunAskAsync(ClickyApplicationServices applicationServices, string typedTranscript)
    {
        int tokenTestExitCode = await RunTokenTestAsync(applicationServices);

        CompanionManager companionManager = applicationServices.CompanionManager;
        companionManager.Start();
        companionManager.ShowCursorOverlayForDebugging();

        ClickyApplicationLog.Write(LogCategory, $"Asking ({typedTranscript.Length} characters) with model {applicationServices.ClaudeChatClient.ModelIdentifier}.");
        ClickyApplicationLog.WriteConsoleText("--- streamed reply ---" + Environment.NewLine);

        CompanionTurnResult turnResult = await companionManager.RunTypedDebugTurnAsync(
            typedTranscript,
            responseTextDelta => ClickyApplicationLog.WriteConsoleText(responseTextDelta));

        ClickyApplicationLog.WriteConsoleText(Environment.NewLine + "--- end of reply ---" + Environment.NewLine);
        ClickyApplicationLog.Write(LogCategory, $"Spoken text: {turnResult.SpokenText}");
        ClickyApplicationLog.Write(
            LogCategory,
            turnResult.PointTarget is null
                ? "Point target: none"
                : $"Point target: {turnResult.PointTarget.TargetLocationInPhysicalPixels} on {turnResult.PointTarget.TargetScreen.MonitorDeviceName} (\"{turnResult.PointTarget.ElementLabel}\")");
        ClickyApplicationLog.Write(LogCategory, $"Turn {(turnResult.DidSucceed ? "succeeded" : "failed: " + turnResult.FailureDescription)}.");

        return turnResult.DidSucceed && tokenTestExitCode == 0 ? 0 : 1;
    }
}
