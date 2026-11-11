using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Clicky.Core.Configuration;

/// <summary>
/// Where the worker base URL came from. Reported in logs so a developer can tell
/// at a glance whether the env var, the config file, or the fallback won.
/// </summary>
public enum WorkerBaseUrlSource
{
    EnvironmentVariable,
    AppSettingsFile,
    BuiltInDefault,
}

/// <summary>
/// The resolved Cloudflare Worker location plus the three routes the app calls.
/// The app never talks to Anthropic, ElevenLabs or AssemblyAI directly; every
/// request goes through these worker routes so no API key ships in the app.
/// </summary>
public sealed record ClickyWorkerConfiguration(Uri WorkerBaseUrl, WorkerBaseUrlSource WorkerBaseUrlSource)
{
    public Uri ChatEndpointUrl => new(WorkerBaseUrl, "chat");
    public Uri TextToSpeechEndpointUrl => new(WorkerBaseUrl, "tts");
    public Uri TranscribeTokenEndpointUrl => new(WorkerBaseUrl, "transcribe-token");
}

/// <summary>
/// Resolves the worker URL in priority order: the CLICKY_WORKER_URL environment
/// variable, then "WorkerUrl" in appsettings.json next to the executable, then
/// http://localhost:8787 (the default port of <c>npx wrangler dev</c>).
/// There is deliberately no hardcoded deployed worker URL.
/// </summary>
public static class ClickyWorkerConfigurationLoader
{
    public const string WorkerUrlEnvironmentVariableName = "CLICKY_WORKER_URL";
    public const string AppSettingsFileName = "appsettings.json";
    public const string AppSettingsWorkerUrlPropertyName = "WorkerUrl";
    public const string DefaultWorkerBaseUrl = "http://localhost:8787";

    /// <summary>
    /// Loads configuration from the real process environment and the appsettings.json
    /// that sits in <paramref name="applicationDirectoryPath"/>.
    /// </summary>
    public static ClickyWorkerConfiguration LoadFromProcessEnvironment(string applicationDirectoryPath)
    {
        string appSettingsFilePath = Path.Combine(applicationDirectoryPath, AppSettingsFileName);
        return Load(Environment.GetEnvironmentVariable, appSettingsFilePath);
    }

    /// <summary>
    /// Testable core of the loader. <paramref name="readEnvironmentVariable"/> is injected
    /// so tests never depend on the machine's real environment.
    /// </summary>
    public static ClickyWorkerConfiguration Load(
        Func<string, string?> readEnvironmentVariable,
        string? appSettingsFilePath)
    {
        string? environmentVariableValue = readEnvironmentVariable(WorkerUrlEnvironmentVariableName);
        if (TryParseWorkerBaseUrl(environmentVariableValue, out Uri? workerBaseUrlFromEnvironment))
        {
            return new ClickyWorkerConfiguration(workerBaseUrlFromEnvironment, WorkerBaseUrlSource.EnvironmentVariable);
        }

        string? appSettingsWorkerUrlValue = ReadWorkerUrlFromAppSettingsFile(appSettingsFilePath);
        if (TryParseWorkerBaseUrl(appSettingsWorkerUrlValue, out Uri? workerBaseUrlFromAppSettings))
        {
            return new ClickyWorkerConfiguration(workerBaseUrlFromAppSettings, WorkerBaseUrlSource.AppSettingsFile);
        }

        TryParseWorkerBaseUrl(DefaultWorkerBaseUrl, out Uri? defaultWorkerBaseUrl);
        return new ClickyWorkerConfiguration(defaultWorkerBaseUrl!, WorkerBaseUrlSource.BuiltInDefault);
    }

    /// <summary>
    /// Accepts only absolute http or https URLs. A trailing slash is always added so
    /// relative route resolution ("chat", "tts") appends to the path instead of
    /// replacing its last segment.
    /// </summary>
    public static bool TryParseWorkerBaseUrl(string? candidateWorkerUrl, [NotNullWhen(true)] out Uri? workerBaseUrl)
    {
        workerBaseUrl = null;

        if (string.IsNullOrWhiteSpace(candidateWorkerUrl))
        {
            return false;
        }

        string trimmedCandidateWorkerUrl = candidateWorkerUrl.Trim();
        if (!trimmedCandidateWorkerUrl.EndsWith('/'))
        {
            trimmedCandidateWorkerUrl += "/";
        }

        if (!Uri.TryCreate(trimmedCandidateWorkerUrl, UriKind.Absolute, out Uri? parsedWorkerUrl))
        {
            return false;
        }

        bool isHttpOrHttps = parsedWorkerUrl.Scheme == Uri.UriSchemeHttp || parsedWorkerUrl.Scheme == Uri.UriSchemeHttps;
        if (!isHttpOrHttps)
        {
            return false;
        }

        workerBaseUrl = parsedWorkerUrl;
        return true;
    }

    private static string? ReadWorkerUrlFromAppSettingsFile(string? appSettingsFilePath)
    {
        if (string.IsNullOrWhiteSpace(appSettingsFilePath) || !File.Exists(appSettingsFilePath))
        {
            return null;
        }

        try
        {
            using FileStream appSettingsFileStream = File.OpenRead(appSettingsFilePath);
            using JsonDocument appSettingsDocument = JsonDocument.Parse(appSettingsFileStream);

            if (appSettingsDocument.RootElement.ValueKind == JsonValueKind.Object
                && appSettingsDocument.RootElement.TryGetProperty(AppSettingsWorkerUrlPropertyName, out JsonElement workerUrlElement)
                && workerUrlElement.ValueKind == JsonValueKind.String)
            {
                return workerUrlElement.GetString();
            }
        }
        catch (JsonException)
        {
            // A malformed appsettings.json should not crash the app; fall through to the default.
        }
        catch (IOException)
        {
            // Same for a file that disappears or is locked between the exists check and the read.
        }

        return null;
    }
}
