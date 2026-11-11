using System.Text.Json;
using System.Text.Json.Serialization;
using Clicky.Core.Claude;
using Clicky.Core.PushToTalk;

namespace Clicky.Core.Settings;

/// <summary>
/// Per-user preferences, the Windows counterpart of the Mac app's UserDefaults keys
/// (selectedClaudeModel, isClickyCursorEnabled, hasCompletedOnboarding).
/// Stored as JSON in %LOCALAPPDATA%\Clicky\settings.json.
/// </summary>
public sealed class ClickyUserSettings
{
    [JsonPropertyName("selectedModel")]
    public string SelectedModelIdentifier { get; set; } = ClaudeModelIdentifiers.DefaultSonnetModelIdentifier;

    /// <summary>One of CtrlAlt, ShiftCtrl, CtrlAltSpace, ShiftCtrlSpace.</summary>
    [JsonPropertyName("pushToTalkShortcut")]
    public string PushToTalkShortcut { get; set; } = PushToTalkShortcutDefinitions.ToSettingsValue(PushToTalkShortcutDefinitions.DefaultShortcutOption);

    [JsonPropertyName("hasCompletedOnboarding")]
    public bool HasCompletedOnboarding { get; set; }

    /// <summary>
    /// When false the blue cursor only appears for the length of an interaction
    /// (transient mode). The Mac panel currently hides this toggle; same here.
    /// </summary>
    [JsonPropertyName("isClickyCursorEnabled")]
    public bool IsClickyCursorEnabled { get; set; } = true;

    [JsonIgnore]
    public PushToTalkShortcutOption PushToTalkShortcutOption => PushToTalkShortcutDefinitions.ParseSettingsValueOrDefault(PushToTalkShortcut);
}

/// <summary>
/// Loads and saves <see cref="ClickyUserSettings"/>. A missing or corrupt file yields
/// defaults instead of an error, and unknown values are normalized on load.
/// </summary>
public sealed class ClickyUserSettingsStore
{
    public const string SettingsFolderName = "Clicky";
    public const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions SettingsJsonSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly object settingsFileLock = new();

    public ClickyUserSettingsStore(string settingsFilePath)
    {
        SettingsFilePath = settingsFilePath;
    }

    public string SettingsFilePath { get; }

    /// <summary>%LOCALAPPDATA%\Clicky\settings.json</summary>
    public static string GetDefaultSettingsFilePath()
    {
        string localApplicationDataFolderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationDataFolderPath, SettingsFolderName, SettingsFileName);
    }

    public ClickyUserSettings Load()
    {
        lock (settingsFileLock)
        {
            ClickyUserSettings loadedSettings = TryReadSettingsFile() ?? new ClickyUserSettings();
            loadedSettings.SelectedModelIdentifier = ClaudeModelIdentifiers.NormalizeModelIdentifier(loadedSettings.SelectedModelIdentifier);
            loadedSettings.PushToTalkShortcut = PushToTalkShortcutDefinitions.ToSettingsValue(loadedSettings.PushToTalkShortcutOption);
            return loadedSettings;
        }
    }

    /// <summary>Writes to a temporary file first, then swaps it in, so a crash mid-write cannot leave a half-written file.</summary>
    public void Save(ClickyUserSettings settingsToSave)
    {
        lock (settingsFileLock)
        {
            string settingsFolderPath = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(settingsFolderPath);

            string temporarySettingsFilePath = SettingsFilePath + ".tmp";
            string settingsJson = JsonSerializer.Serialize(settingsToSave, SettingsJsonSerializerOptions);
            File.WriteAllText(temporarySettingsFilePath, settingsJson);
            File.Move(temporarySettingsFilePath, SettingsFilePath, overwrite: true);
        }
    }

    private ClickyUserSettings? TryReadSettingsFile()
    {
        if (!File.Exists(SettingsFilePath))
        {
            return null;
        }

        try
        {
            string settingsJson = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<ClickyUserSettings>(settingsJson, SettingsJsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
