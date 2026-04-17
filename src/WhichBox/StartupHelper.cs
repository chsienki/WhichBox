#pragma warning disable CA1416 // Platform compatibility
using Microsoft.Win32;

namespace WhichBox;

/// <summary>
/// Manages the "Run at Startup" registry entry in HKCU\...\Run.
/// </summary>
internal static class StartupHelper
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhichBox";

    /// <summary>
    /// Returns true if the WhichBox startup entry exists in the registry.
    /// </summary>
    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is not null;
        }
    }

    /// <summary>
    /// Adds or removes the startup registry entry.
    /// </summary>
    public static void SetRegistered(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
                key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
