using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using static WhichBox.NativeMethods;

namespace WhichBox;

/// <summary>
/// Owns one <see cref="TaskbarWindow"/> per taskbar -- the primary
/// <c>Shell_TrayWnd</c> plus one <c>Shell_SecondaryTrayWnd</c> for every
/// additional monitor -- and keeps that set in sync as monitors and taskbars
/// come and go (docking a laptop, switching to RDP, toggling "show taskbar on
/// all displays", etc.).
///
/// A single reconcile loop is the backbone: a periodic timer enumerates the
/// live taskbars, spawns windows for new ones, closes windows for taskbars that
/// disappeared, and repositions the survivors when their taskbar is resized.
/// The primary window forwards display/DPI/taskbar-recreated messages here as a
/// fast path so changes are usually reflected immediately rather than at the
/// next tick. Shared state (settings, update check, context menu) lives here so
/// every window stays consistent -- e.g. a colour picked on one monitor applies
/// to all of them.
/// </summary>
internal sealed class TaskbarManager
{
    // Monitor hot-plug and RDP transitions are rare and not latency-critical,
    // so a few seconds of lag is acceptable. The primary window's forwarded
    // WM_DISPLAYCHANGE provides a near-instant fast path for the common cases.
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(3);
    private const int HeartbeatEveryTicks = 20; // 20 * 3s = 60s

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherTimer _reconcileTimer;

    // Secondary windows keyed by their Shell_SecondaryTrayWnd HWND. The primary
    // window is tracked separately because Shell_TrayWnd effectively always
    // exists while Explorer is running, so it is never torn down by reconcile.
    private readonly Dictionary<nint, TaskbarWindow> _secondaryWindows = [];
    private TaskbarWindow? _primaryWindow;

    private int _heartbeatTickCounter;
    private bool _restarting;
    private bool _exiting;

    public Settings Settings { get; }
    public UpdateChecker UpdateChecker { get; }
    public NativeContextMenu ContextMenu { get; }
    public string MachineName { get; }

    public TaskbarManager()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings = Settings.Load();
        UpdateChecker = new UpdateChecker();
        ContextMenu = new NativeContextMenu();
        MachineName = Environment.MachineName;

        UpdateChecker.UpdateFound += () =>
            SafeEnqueue("UpdateChecker.UpdateFound", ShowUpdateDotOnAll);

        _reconcileTimer = new DispatcherTimer { Interval = ReconcileInterval };
        _reconcileTimer.Tick += (_, _) =>
        {
            try { OnTimerTick(); }
            catch (Exception ex) { Logger.Crash("TaskbarManager.Tick", ex); }
        };
    }

    /// <summary>Creates the initial windows and starts the reconcile loop.</summary>
    public void Start()
    {
        Reconcile("startup", force: true);
        _reconcileTimer.Start();
        _ = UpdateChecker.CheckAsync();
    }

    /// <summary>Every live window (primary first), as a snapshot list.</summary>
    public IReadOnlyList<TaskbarWindow> Windows
    {
        get
        {
            var list = new List<TaskbarWindow>(_secondaryWindows.Count + 1);
            if (_primaryWindow is not null) list.Add(_primaryWindow);
            list.AddRange(_secondaryWindows.Values);
            return list;
        }
    }

    private void OnTimerTick()
    {
        Reconcile("timer", force: false);

        if (++_heartbeatTickCounter >= HeartbeatEveryTicks)
        {
            _heartbeatTickCounter = 0;
            EmitHeartbeat();
        }
    }

    /// <summary>
    /// Brings the set of windows in line with the taskbars currently present:
    /// creates the primary window if needed, adds/removes secondary windows to
    /// match the live <c>Shell_SecondaryTrayWnd</c> set, and asks every survivor
    /// to re-verify its parent and reposition (always when <paramref name="force"/>
    /// is set, otherwise only if its taskbar was resized).
    /// </summary>
    private void Reconcile(string reason, bool force)
    {
        if (_exiting) return;

        // --- Primary window (Shell_TrayWnd) ---
        var primaryTaskbar = FindPrimaryTaskbar();
        if (_primaryWindow is null)
        {
            if (primaryTaskbar != 0)
            {
                Logger.Info($"Reconcile[{reason}]: creating primary window for taskbar=0x{primaryTaskbar:X}");
                _primaryWindow = CreateWindow(primaryTaskbar, isPrimary: true);
            }
            else
            {
                Logger.Warn($"Reconcile[{reason}]: primary taskbar (Shell_TrayWnd) not found yet");
            }
        }
        else
        {
            _primaryWindow.RefreshPosition(force);
        }

        // --- Secondary windows (Shell_SecondaryTrayWnd, one per extra monitor) ---
        var secondaries = FindSecondaryTaskbars();
        var live = new HashSet<nint>(secondaries);

        // Close windows whose taskbar vanished (monitor unplugged / RDP / toggle).
        var removed = _secondaryWindows.Keys.Where(hwnd => !live.Contains(hwnd)).ToList();
        foreach (var hwnd in removed)
        {
            Logger.Info($"Reconcile[{reason}]: secondary taskbar 0x{hwnd:X} gone -- closing its window");
            var win = _secondaryWindows[hwnd];
            _secondaryWindows.Remove(hwnd);
            CloseWindow(win);
        }

        // Create windows for new taskbars; reposition the ones we already have.
        foreach (var hwnd in secondaries)
        {
            if (_secondaryWindows.TryGetValue(hwnd, out var existing))
            {
                existing.RefreshPosition(force);
            }
            else
            {
                Logger.Info($"Reconcile[{reason}]: new secondary taskbar 0x{hwnd:X} -- creating window");
                _secondaryWindows[hwnd] = CreateWindow(hwnd, isPrimary: false);
            }
        }
    }

    private TaskbarWindow CreateWindow(nint taskbar, bool isPrimary)
    {
        var win = new TaskbarWindow(taskbar, isPrimary, this);
        win.Activate();
        return win;
    }

    private static void CloseWindow(TaskbarWindow win)
    {
        try { win.Close(); }
        catch (Exception ex) { Logger.Warn($"CloseWindow failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Called from a window's Closed handler so we drop our reference.</summary>
    internal void OnWindowClosed(TaskbarWindow win)
    {
        if (ReferenceEquals(win, _primaryWindow))
        {
            _primaryWindow = null;
            if (!_exiting)
                Logger.Warn("Primary window closed unexpectedly -- reconcile will recreate it");
        }
        else
        {
            var key = _secondaryWindows.FirstOrDefault(kv => ReferenceEquals(kv.Value, win)).Key;
            if (key != 0) _secondaryWindows.Remove(key);
        }
    }

    // ===== Fast-path notifications forwarded from the primary window =====

    /// <summary>A display or DPI change occurred; re-sync and reposition all.</summary>
    internal void OnDisplayChanged(string reason)
    {
        Logger.Info($"OnDisplayChanged: {reason} -- reconciling all taskbars");
        Reconcile(reason, force: true);
    }

    /// <summary>
    /// Explorer recreated the taskbars (TaskbarCreated). The primary re-finds
    /// Shell_TrayWnd; reconcile drops the now-dead secondary windows and builds
    /// fresh ones for the recreated Shell_SecondaryTrayWnd handles.
    /// </summary>
    internal void OnTaskbarRecreated()
    {
        Logger.Info("OnTaskbarRecreated -- re-parenting primary and rebuilding secondaries");
        _primaryWindow?.ForceReattach();
        Reconcile("TaskbarCreated", force: true);
    }

    /// <summary>
    /// Handles an RDP/console session transition. The WinUI child-site bridge is
    /// corrupted by the transition itself and fails-fast on the next pumped
    /// message, so the only reliable fix is to relaunch into a clean process.
    /// </summary>
    internal void HandleSessionChange(int reason)
    {
        if (reason == WTS_CONSOLE_CONNECT || reason == WTS_REMOTE_CONNECT || reason == WTS_SESSION_UNLOCK)
            SelfRestart($"session change ({WtsReasonName(reason)})");
    }

    // ===== Context menu (shared across all windows) =====

    /// <summary>
    /// Shows the shared context menu at the given screen coordinates and applies
    /// the result across every window. <paramref name="source"/> is the window
    /// that was right-clicked, used for per-window actions like diagnostics.
    /// </summary>
    internal void ShowContextMenu(int x, int y, TaskbarWindow source)
    {
        var result = ContextMenu.Show(x, y,
            startupChecked: StartupHelper.IsRegistered,
            updateVersion: UpdateChecker.LatestVersion);
        switch (result.Action)
        {
            case MenuAction.SelectColor:
                Settings.ChosenColor = result.Color;
                Settings.Save();
                ApplyColorToAll();
                break;
            case MenuAction.ResetColor:
                Settings.ChosenColor = null;
                Settings.Save();
                ApplyColorToAll();
                break;
            case MenuAction.ToggleStartup:
                StartupHelper.SetRegistered(!StartupHelper.IsRegistered);
                break;
            case MenuAction.ReattachToTaskbar:
                Logger.Info("ReattachToTaskbar: user requested manual re-attach of all windows");
                ReattachAll();
                break;
            case MenuAction.OpenLogFolder:
                Logger.Info("OpenLogFolder: user requested log folder");
                Logger.OpenLogFolder();
                break;
            case MenuAction.CaptureDiagnostics:
                Logger.Info("CaptureDiagnostics: user requested sizing diagnostics");
                source.HandleCopyDiagnostics();
                break;
            case MenuAction.CheckForUpdates:
                _ = UpdateChecker.CheckAsync();
                break;
            case MenuAction.Update:
                _ = UpdateChecker.DownloadAndInstallAsync();
                break;
            case MenuAction.Exit:
                Exit();
                break;
        }
    }

    private void ApplyColorToAll()
    {
        foreach (var win in Windows) win.ApplyColor();
    }

    private void ReattachAll()
    {
        _primaryWindow?.ForceReattach();
        foreach (var win in _secondaryWindows.Values) win.ForceReattach();
        Reconcile("reattach", force: true);
    }

    private void ShowUpdateDotOnAll()
    {
        foreach (var win in Windows) win.SetUpdateDotVisible(true);
    }

    /// <summary>
    /// Writes a status snapshot every 60 seconds. If the process dies silently,
    /// the absence of further heartbeats narrows the time-of-death to a
    /// 60-second window; each window logs its own window/parent/rect state.
    /// </summary>
    private void EmitHeartbeat()
    {
        try
        {
            var windows = Windows;
            long memMB = 0;
            try { memMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024; } catch { }
            Logger.Info($"Heartbeat: windows={windows.Count} (primary={_primaryWindow is not null} secondary={_secondaryWindows.Count}) primaryTaskbar=0x{FindPrimaryTaskbar():X} memMB={memMB}");
            foreach (var win in windows) win.LogHeartbeat();
        }
        catch (Exception ex)
        {
            Logger.Warn($"EmitHeartbeat failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Spawns a fresh WhichBox.exe with --wait-for-pid pointing at our own
    /// process ID, then exits immediately. The new process waits for us to die
    /// before initializing, ensuring no race on shared WindowsAppRuntime / COM
    /// endpoints. The only viable mitigation for the WinUI 3 child-site bridge
    /// fail-fast that fires after every RDP/console session transition.
    /// </summary>
    private void SelfRestart(string reason)
    {
        if (_restarting) return;
        _restarting = true;

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            Logger.Warn("SelfRestart: Environment.ProcessPath is null, cannot restart");
            _restarting = false;
            return;
        }

        Logger.Info($"SelfRestart ({reason}): spawning new instance and exiting (current PID={Environment.ProcessId})");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--wait-for-pid {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"SelfRestart: spawn failed: {ex.GetType().Name}: {ex.Message}");
            _restarting = false;
            return;
        }

        Logger.Info("SelfRestart: exiting now");
        Environment.Exit(0);
    }

    /// <summary>Closes every window and shuts the application down.</summary>
    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Logger.Info("TaskbarManager.Exit: shutting down cleanly");

        try { _reconcileTimer.Stop(); } catch { }

        foreach (var win in _secondaryWindows.Values.ToList()) CloseWindow(win);
        _secondaryWindows.Clear();

        if (_primaryWindow is not null)
        {
            CloseWindow(_primaryWindow);
            _primaryWindow = null;
        }

        try { ContextMenu.Destroy(); }
        catch (Exception ex) { Logger.Warn($"ContextMenu.Destroy failed: {ex.Message}"); }

        try { Application.Current.Exit(); }
        catch (Exception ex) { Logger.Warn($"Application.Exit failed: {ex.Message}"); }
    }

    private void SafeEnqueue(string source, Action action)
    {
        _dispatcher.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { Logger.Crash(source, ex); }
        });
    }
}
