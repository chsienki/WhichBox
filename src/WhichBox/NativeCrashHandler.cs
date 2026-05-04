using System.Runtime.InteropServices;
using static WhichBox.NativeMethods;

namespace WhichBox;

/// <summary>
/// In-process native crash dumper. When a top-level SEH exception is left
/// unhandled (e.g. an access violation in WinUI / DirectComposition / a
/// native dependency), this writes a minidump to %LOCALAPPDATA%\WhichBox\Dumps
/// next to the log so we can post-mortem the failure.
///
/// Best-effort only: this canNOT catch Environment.FailFast,
/// RaiseFailFastException, TerminateProcess, or external kills. For those
/// the heartbeat in MainWindow's HealthCheck timer is the diagnostic signal.
///
/// The crash filter itself runs in a potentially corrupt process. It must
/// avoid managed allocations, locks, string formatting, and anything else
/// that could deadlock or fault. Everything is pre-computed at install time:
/// the dump path is pre-marshalled to unmanaged UTF-16, the process pseudo
/// handle is cached, and dbghelp.dll is pre-loaded.
/// </summary>
internal static unsafe class NativeCrashHandler
{
    private const int MaxDumps = 5;

    private static readonly string _dumpFolder;
    private static nint _dumpPathPtr;          // pre-marshalled UTF-16, never freed
    private static nint _processHandle;        // current-process pseudo handle
    private static uint _processId;
    private static int _installed;
    private static int _filterRunning;         // reentrancy guard

    public static string DumpFolder => _dumpFolder;

    static NativeCrashHandler()
    {
        _dumpFolder = Path.Combine(Logger.Folder, "Dumps");
        try { Directory.CreateDirectory(_dumpFolder); } catch { }
    }

    /// <summary>
    /// Installs the unhandled-exception filter. Idempotent for path/cache
    /// setup but safe to call again to re-assert the filter if some other
    /// component overwrote it (SetUnhandledExceptionFilter is "last installer
    /// wins"). Recommended call sites: as early as possible in Program.Main
    /// and again from MainWindow's constructor.
    /// </summary>
    public static void Install()
    {
        try
        {
            bool firstInstall = Interlocked.Exchange(ref _installed, 1) == 0;

            if (firstInstall)
            {
                // Pre-warm dbghelp so the loader doesn't have to find/load it
                // from inside a corrupted process.
                LoadLibraryW("dbghelp.dll");

                _processHandle = GetCurrentProcess();
                _processId = (uint)Environment.ProcessId;

                // Pre-marshal the dump path so the crash filter does no
                // string allocations. One unique path per process invocation.
                var dumpPath = Path.Combine(_dumpFolder,
                    $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{_processId}.dmp");
                _dumpPathPtr = Marshal.StringToCoTaskMemUni(dumpPath);

                PruneOldDumps();

                Logger.Info($"NativeCrashHandler: dumps -> {_dumpFolder} (this run: {Path.GetFileName(dumpPath)})");
                LogPreviousDumps();
            }

            delegate* unmanaged<EXCEPTION_POINTERS*, int> filterFn = &UnhandledFilter;
            var prev = SetUnhandledExceptionFilter((nint)filterFn);
            Logger.Info($"NativeCrashHandler {(firstInstall ? "installed" : "re-installed")} (prev top-level filter=0x{prev:X})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"NativeCrashHandler.Install failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly]
    private static int UnhandledFilter(EXCEPTION_POINTERS* info)
    {
        // Reentrancy guard. dbghelp is single-threaded; if we faulted again
        // from inside our own dump path, just terminate cleanly.
        if (Interlocked.Exchange(ref _filterRunning, 1) != 0)
            return EXCEPTION_EXECUTE_HANDLER;

        // Everything below is intentionally minimal: no managed allocations,
        // no locks, no string formatting, no Logger calls. The process state
        // may be corrupt; do as little as possible before writing the dump.
        nint hFile = INVALID_HANDLE_VALUE;
        try
        {
            if (_dumpPathPtr == 0) return EXCEPTION_EXECUTE_HANDLER;

            hFile = CreateFileW(_dumpPathPtr, GENERIC_WRITE, 0, 0,
                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, 0);
            if (hFile == INVALID_HANDLE_VALUE)
                return EXCEPTION_EXECUTE_HANDLER;

            var excInfo = new MINIDUMP_EXCEPTION_INFORMATION
            {
                ThreadId = GetCurrentThreadId(),
                ExceptionPointers = info,
                ClientPointers = 0,
            };

            uint dumpType = MiniDumpNormal
                          | MiniDumpWithThreadInfo
                          | MiniDumpWithUnloadedModules
                          | MiniDumpWithProcessThreadData;

            MiniDumpWriteDump(_processHandle, _processId, hFile, dumpType,
                &excInfo, 0, 0);
        }
        catch
        {
            // Filter must never throw -- we're terminating anyway.
        }
        finally
        {
            if (hFile != INVALID_HANDLE_VALUE && hFile != 0)
            {
                try { CloseHandle(hFile); } catch { }
            }
        }

        // Terminate without WER dialog. Do not chain to the previous filter
        // -- per rubber-duck guidance, running more code in a corrupt
        // process risks turning a crash into a hang or dialog box.
        return EXCEPTION_EXECUTE_HANDLER;
    }

    /// <summary>
    /// At install time, log any pre-existing dump files so a previous
    /// process's crash is visible in the log of the next run.
    /// </summary>
    private static void LogPreviousDumps()
    {
        try
        {
            var existing = new DirectoryInfo(_dumpFolder).GetFiles("crash-*.dmp");
            if (existing.Length == 0) return;

            Logger.Warn($"NativeCrashHandler: {existing.Length} previous crash dump(s) found in {_dumpFolder}:");
            foreach (var f in existing.OrderBy(f => f.LastWriteTimeUtc))
            {
                Logger.Warn($"  {f.Name} ({f.Length} bytes, {f.LastWriteTime:yyyy-MM-dd HH:mm:ss})");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"LogPreviousDumps failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Keep at most (MaxDumps - 1) dumps so this run can add one more
    /// without exceeding the cap.
    /// </summary>
    private static void PruneOldDumps()
    {
        try
        {
            var dumps = new DirectoryInfo(_dumpFolder).GetFiles("crash-*.dmp")
                .OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            for (int i = MaxDumps - 1; i < dumps.Count; i++)
            {
                try { dumps[i].Delete(); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"PruneOldDumps failed: {ex.Message}");
        }
    }
}
