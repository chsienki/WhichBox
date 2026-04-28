using System.Globalization;

namespace WhichBox;

/// <summary>
/// Lightweight file-based logger for diagnosing taskbar parenting issues
/// (especially over RDP). Writes to %LOCALAPPDATA%\WhichBox\debug.log.
/// Enabled when the WHICHBOX_DEBUG environment variable is set OR when
/// a debug.log file already exists in the WhichBox folder.
/// </summary>
internal static class DebugLog
{
    private static readonly object _lock = new();
    private static readonly string? _path = Init();
    private static readonly bool _enabled = _path is not null;

    private static string? Init()
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WhichBox");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "debug.log");

            // Enable if env var is set OR the file already exists (so users
            // can opt in just by `New-Item debug.log`).
            var envEnabled = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WHICHBOX_DEBUG"));
            if (!envEnabled && !File.Exists(path)) return null;

            // Truncate if it grows beyond ~512 KB to avoid unbounded growth.
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024)
                    File.WriteAllText(path, "");
            }
            catch { }

            return path;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsEnabled => _enabled;

    public static void Write(string message)
    {
        if (!_enabled || _path is null) return;
        try
        {
            lock (_lock)
            {
                var line = $"[{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch { }
    }
}
