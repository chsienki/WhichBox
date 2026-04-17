using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI;

namespace WhichBox;

/// <summary>
/// Persists user preferences to %APPDATA%\WhichBox\settings.json.
/// </summary>
internal sealed class Settings
{
    private static readonly string s_settingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WhichBox");

    private static readonly string s_settingsPath =
        Path.Combine(s_settingsDir, "settings.json");

    public Color? ChosenColor { get; set; }

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(s_settingsPath))
                return new Settings();

            var json = File.ReadAllText(s_settingsPath);
            var data = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsData);
            if (data?.ChosenColorHex is { } hex && hex.StartsWith('#') && hex.Length == 7)
            {
                var r = Convert.ToByte(hex[1..3], 16);
                var g = Convert.ToByte(hex[3..5], 16);
                var b = Convert.ToByte(hex[5..7], 16);
                return new Settings { ChosenColor = Color.FromArgb(0xFF, r, g, b) };
            }
        }
        catch
        {
            // Corrupted settings -- start fresh
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(s_settingsDir);
            var data = new SettingsData
            {
                ChosenColorHex = ChosenColor is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null
            };
            var json = JsonSerializer.Serialize(data, SettingsJsonContext.Default.SettingsData);
            File.WriteAllText(s_settingsPath, json);
        }
        catch
        {
            // Best effort
        }
    }

    internal sealed class SettingsData
    {
        public string? ChosenColorHex { get; set; }
    }
}

[JsonSerializable(typeof(Settings.SettingsData))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
