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

    /// <summary>
    /// Hides the indicator on taskbars too narrow to share -- phones and other
    /// small RDP clients, where the indicator otherwise sits on top of the
    /// Start button and makes it unclickable.
    /// </summary>
    public bool HideOnNarrowTaskbar { get; set; } = true;

    /// <summary>
    /// Taskbar width, in logical pixels, below which the indicator hides when
    /// <see cref="HideOnNarrowTaskbar"/> is on. Not exposed in the menu; edit
    /// settings.json to tune it.
    /// </summary>
    public int NarrowTaskbarWidth { get; set; } = DefaultNarrowTaskbarWidth;

    private const int DefaultNarrowTaskbarWidth = 800;

    public static Settings Load()
    {
        try
        {
            if (!File.Exists(s_settingsPath))
                return new Settings();

            var json = File.ReadAllText(s_settingsPath);
            var data = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.SettingsData);
            if (data is null)
                return new Settings();

            var settings = new Settings
            {
                HideOnNarrowTaskbar = data.HideOnNarrowTaskbar ?? true,
                NarrowTaskbarWidth = data.NarrowTaskbarWidth is int w and > 0 ? w : DefaultNarrowTaskbarWidth
            };

            if (data.ChosenColorHex is { } hex && hex.StartsWith('#') && hex.Length == 7)
            {
                var r = Convert.ToByte(hex[1..3], 16);
                var g = Convert.ToByte(hex[3..5], 16);
                var b = Convert.ToByte(hex[5..7], 16);
                settings.ChosenColor = Color.FromArgb(0xFF, r, g, b);
            }

            return settings;
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
                ChosenColorHex = ChosenColor is { } c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : null,
                HideOnNarrowTaskbar = HideOnNarrowTaskbar,
                NarrowTaskbarWidth = NarrowTaskbarWidth
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
        public bool? HideOnNarrowTaskbar { get; set; }
        public int? NarrowTaskbarWidth { get; set; }
    }
}

[JsonSerializable(typeof(Settings.SettingsData))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
