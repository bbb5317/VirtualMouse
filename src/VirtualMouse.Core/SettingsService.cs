using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VirtualMouse.Core;

/// <summary>
/// Persists TrackingSettings to a JSON file in the user's local app data folder.
/// Settings survive application restarts and are user-specific.
/// </summary>
public class SettingsService
{
    private readonly ILogger<SettingsService> _logger;

    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VirtualMouse");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Loads settings from disk. Returns a new default TrackingSettings instance
    /// if the file does not exist or cannot be parsed.
    /// </summary>
    public TrackingSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                var json = File.ReadAllText(SettingsFilePath);
                var settings = JsonSerializer.Deserialize<TrackingSettings>(json, JsonOptions);
                if (settings != null)
                {
                    _logger.LogInformation("Settings loaded from {Path}.", SettingsFilePath);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}. Using defaults.", SettingsFilePath);
        }

        _logger.LogInformation("No saved settings found. Using defaults.");
        return new TrackingSettings();
    }

    /// <summary>
    /// Saves the current settings to disk.
    /// </summary>
    public void Save(TrackingSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsFilePath, json);
            _logger.LogInformation("Settings saved to {Path}.", SettingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}.", SettingsFilePath);
        }
    }

    /// <summary>
    /// Deletes the saved settings file, effectively resetting to defaults on next load.
    /// </summary>
    public void Reset()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                File.Delete(SettingsFilePath);
                _logger.LogInformation("Settings file deleted (reset to defaults).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete settings file.");
        }
    }

    /// <summary>
    /// Returns the path where settings are stored (useful for diagnostics).
    /// </summary>
    public static string GetSettingsPath() => SettingsFilePath;
}
