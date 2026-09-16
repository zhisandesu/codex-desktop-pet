using System.IO;
using System.Text.Json;
using XiaobianPet.Models;

namespace XiaobianPet.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly string _settingsPath;

    public SettingsStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XiaobianPet",
            "settings.json"))
    {
    }

    internal SettingsStore(string settingsPath)
    {
        _settingsPath = settingsPath;
    }

    public PetSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return Sanitize(new PetSettings());
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<PetSettings>(json, SerializerOptions);
            return settings is null ? new PetSettings() : Sanitize(settings);
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return Sanitize(new PetSettings());
        }
    }

    public async Task SaveAsync(PetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _saveGate.WaitAsync().ConfigureAwait(false);
        string? temporaryPath = null;

        try
        {
            var directory = Path.GetDirectoryName(_settingsPath)
                ?? throw new InvalidOperationException("设置文件路径缺少目录。");
            Directory.CreateDirectory(directory);

            temporaryPath = $"{_settingsPath}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(Sanitize(settings), SerializerOptions);
            await File.WriteAllTextAsync(temporaryPath, json).ConfigureAwait(false);
            File.Move(temporaryPath, _settingsPath, true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _saveGate.Release();
        }
    }

    private static PetSettings Sanitize(PetSettings settings)
    {
        // Never manufacture consent while migrating an older, missing, or damaged
        // settings file. Sharing and automatic extraction stay off until the owner
        // explicitly enables them in the UI.
        var memorySharingConsentRecorded = settings.MemorySharingConsentRecorded;
        var shareMemoryWithDialogue = memorySharingConsentRecorded &&
                                      settings.ShareMemoryWithDialogue;
        var automaticMemoryEnabled = shareMemoryWithDialogue &&
                                     settings.AutomaticMemoryEnabled;
        return new PetSettings
        {
            WindowLeft = settings.WindowLeft is { } left && double.IsFinite(left) ? left : null,
            WindowTop = settings.WindowTop is { } top && double.IsFinite(top) ? top : null,
            TtsEnabled = settings.TtsEnabled,
            TtsVoice = TtsVoiceCatalog.Sanitize(settings.TtsVoice),
            DynamicDialogueEnabled = settings.DynamicDialogueEnabled,
            TaskContextFeedbackEnabled = settings.TaskContextFeedbackEnabled,
            ShareMemoryWithDialogue = shareMemoryWithDialogue,
            MemorySharingConsentRecorded = memorySharingConsentRecorded,
            AutomaticMemoryEnabled = automaticMemoryEnabled,
            MemorySystemVersion = 2,
            PreferAgentPlanAsr = settings.PreferAgentPlanAsr,
            LastCwd = string.IsNullOrWhiteSpace(settings.LastCwd) ? null : settings.LastCwd.Trim(),
            UiOpacity = double.IsFinite(settings.UiOpacity)
                ? Math.Clamp(settings.UiOpacity, 0.60, 1.00)
                : 1.00,
            PetSizeScale = double.IsFinite(settings.PetSizeScale)
                ? Math.Clamp(
                    settings.PetSizeScale,
                    PetSettings.MinimumPetSizeScale,
                    PetSettings.MaximumPetSizeScale)
                : PetSettings.DefaultPetSizeScale
        };
    }

}
