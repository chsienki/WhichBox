using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using static WhichBox.NativeMethods;

namespace WhichBox;

/// <summary>
/// Always-on file logger for diagnosing crashes and disappearance of the
/// taskbar indicator (especially across RDP/console session switches).
/// Writes to %LOCALAPPDATA%\WhichBox\whichbox.log with a 1 MB rolling cap;
/// the previous file is preserved as whichbox.log.old. Thread-safe.
/// </summary>
internal static class Logger
{
    private const long MaxSize = 1024 * 1024;
    private const int RotateCheckInterval = 100;

    private static readonly object _lock = new();
    private static readonly string _folder;
    private static readonly string _path;
    private static int _writeCount;

    public static string Folder => _folder;
    public static string LogPath => _path;

    static Logger()
    {
        _folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WhichBox");
        try { Directory.CreateDirectory(_folder); } catch { }
        _path = Path.Combine(_folder, "whichbox.log");
        TryRotate();
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Crash(string source, Exception ex)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== CRASH in {source} ===");
        AppendException(sb, ex, depth: 0);
        sb.Append("=== end crash ===");
        Write("CRASH", sb.ToString());
    }

    /// <summary>
    /// Writes a startup banner with environment context. Should be called as
    /// early as possible from Main so we know the process started even if it
    /// dies before any window is created.
    /// </summary>
    public static void LogStartupBanner()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("==================================================");
            sb.AppendLine($"WhichBox starting at {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"  Version       : {GetVersion()}");
            sb.AppendLine($"  PID           : {Environment.ProcessId}");
            sb.AppendLine($"  ExePath       : {Environment.ProcessPath}");
            sb.AppendLine($"  OS            : {RuntimeInformation.OSDescription}");
            sb.AppendLine($"  Architecture  : {RuntimeInformation.ProcessArchitecture} (OS {RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"  MachineName   : {Environment.MachineName}");
            sb.AppendLine($"  UserName      : {Environment.UserName}");
            sb.AppendLine($"  SessionId     : {Process.GetCurrentProcess().SessionId}");
            sb.AppendLine($"  SESSIONNAME   : {Environment.GetEnvironmentVariable("SESSIONNAME")}");
            sb.AppendLine($"  RemoteSession : {IsRemoteSession()}");
            sb.AppendLine($"  WorkingDir    : {Environment.CurrentDirectory}");
            sb.Append("==================================================");
            Write("INIT", sb.ToString());
        }
        catch (Exception ex)
        {
            Crash("LogStartupBanner", ex);
        }
    }

    /// <summary>
    /// Opens the log folder in Explorer so the user can grab whichbox.log.
    /// </summary>
    public static void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_folder}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Warn($"OpenLogFolder failed: {ex.Message}");
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                var line = $"[{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] [{level,-5}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);

                if (++_writeCount >= RotateCheckInterval)
                {
                    _writeCount = 0;
                    TryRotate();
                }
            }
        }
        catch
        {
            // Swallow -- the logger must never crash the app.
        }
    }

    private static void TryRotate()
    {
        try
        {
            if (!File.Exists(_path)) return;
            if (new FileInfo(_path).Length < MaxSize) return;

            var old = _path + ".old";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            File.Move(_path, old);
        }
        catch { }
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}{ex.GetType().FullName}: {ex.Message}");
        sb.AppendLine($"{indent}  HResult: 0x{ex.HResult:X8}");
        if (ex is System.ComponentModel.Win32Exception win32)
        {
            sb.AppendLine($"{indent}  NativeError: {win32.NativeErrorCode}");
        }
        if (ex.StackTrace is { } stack)
        {
            foreach (var raw in stack.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                sb.AppendLine($"{indent}  {line}");
            }
        }
        if (ex is AggregateException agg)
        {
            int i = 0;
            foreach (var inner in agg.InnerExceptions)
            {
                sb.AppendLine($"{indent}--- Aggregate inner [{i++}] ---");
                AppendException(sb, inner, depth + 1);
            }
        }
        else if (ex.InnerException is { } inner)
        {
            sb.AppendLine($"{indent}--- Inner ---");
            AppendException(sb, inner, depth + 1);
        }
    }

    private static string GetVersion()
    {
        try
        {
            var attr = typeof(Logger).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attr?.InformationalVersion ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static bool IsRemoteSession()
    {
        try
        {
            return GetSystemMetrics(SM_REMOTESESSION) != 0;
        }
        catch
        {
            return false;
        }
    }
}
