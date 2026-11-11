using Clicky.Core.Configuration;

namespace Clicky.Core.Tests;

public sealed class ClickyWorkerConfigurationLoaderTests : IDisposable
{
    private readonly string temporaryFolderPath = Path.Combine(Path.GetTempPath(), "clicky-config-tests-" + Guid.NewGuid().ToString("N"));

    public ClickyWorkerConfigurationLoaderTests()
    {
        Directory.CreateDirectory(temporaryFolderPath);
    }

    public void Dispose()
    {
        Directory.Delete(temporaryFolderPath, recursive: true);
    }

    [Fact]
    public void EnvironmentVariableWinsOverAppSettings()
    {
        string appSettingsFilePath = WriteAppSettings("{\"WorkerUrl\":\"http://from-file.test:9000\"}");

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(
            environmentVariableName => environmentVariableName == "CLICKY_WORKER_URL" ? "https://from-env.test" : null,
            appSettingsFilePath);

        Assert.Equal(WorkerBaseUrlSource.EnvironmentVariable, workerConfiguration.WorkerBaseUrlSource);
        Assert.Equal("https://from-env.test/", workerConfiguration.WorkerBaseUrl.ToString());
    }

    [Fact]
    public void AppSettingsIsUsedWhenEnvironmentVariableIsMissing()
    {
        string appSettingsFilePath = WriteAppSettings("{\"WorkerUrl\":\"http://from-file.test:9000\"}");

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => null, appSettingsFilePath);

        Assert.Equal(WorkerBaseUrlSource.AppSettingsFile, workerConfiguration.WorkerBaseUrlSource);
        Assert.Equal("http://from-file.test:9000/chat", workerConfiguration.ChatEndpointUrl.ToString());
    }

    [Fact]
    public void FallsBackToWranglerDevDefault()
    {
        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => null, Path.Combine(temporaryFolderPath, "missing.json"));

        Assert.Equal(WorkerBaseUrlSource.BuiltInDefault, workerConfiguration.WorkerBaseUrlSource);
        Assert.Equal("http://localhost:8787/", workerConfiguration.WorkerBaseUrl.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.test")]
    [InlineData("/relative/path")]
    public void InvalidEnvironmentValuesAreSkipped(string invalidEnvironmentValue)
    {
        string appSettingsFilePath = WriteAppSettings("{\"WorkerUrl\":\"http://from-file.test\"}");

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => invalidEnvironmentValue, appSettingsFilePath);

        Assert.Equal(WorkerBaseUrlSource.AppSettingsFile, workerConfiguration.WorkerBaseUrlSource);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"WorkerUrl\": 42}")]
    [InlineData("{}")]
    public void MalformedAppSettingsFallsBackToDefault(string malformedAppSettingsJson)
    {
        string appSettingsFilePath = WriteAppSettings(malformedAppSettingsJson);

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => null, appSettingsFilePath);

        Assert.Equal(WorkerBaseUrlSource.BuiltInDefault, workerConfiguration.WorkerBaseUrlSource);
    }

    [Theory]
    [InlineData("https://worker.example.test", "https://worker.example.test/tts")]
    [InlineData("https://worker.example.test/", "https://worker.example.test/tts")]
    [InlineData("https://example.test/clicky", "https://example.test/clicky/tts")]
    public void RoutesAppendToTheBasePath(string workerBaseUrl, string expectedTextToSpeechUrl)
    {
        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => workerBaseUrl, null);

        Assert.Equal(expectedTextToSpeechUrl, workerConfiguration.TextToSpeechEndpointUrl.ToString());
        Assert.EndsWith("/transcribe-token", workerConfiguration.TranscribeTokenEndpointUrl.ToString());
    }

    [Fact]
    public void ShippedAppSettingsPointsAtLocalWranglerDev()
    {
        string shippedAppSettingsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "appsettings.json");

        ClickyWorkerConfiguration workerConfiguration = ClickyWorkerConfigurationLoader.Load(_ => null, shippedAppSettingsPath);

        Assert.Equal(WorkerBaseUrlSource.AppSettingsFile, workerConfiguration.WorkerBaseUrlSource);
        Assert.Equal("http://localhost:8787/", workerConfiguration.WorkerBaseUrl.ToString());
    }

    private string WriteAppSettings(string appSettingsJson)
    {
        string appSettingsFilePath = Path.Combine(temporaryFolderPath, "appsettings.json");
        File.WriteAllText(appSettingsFilePath, appSettingsJson);
        return appSettingsFilePath;
    }
}
