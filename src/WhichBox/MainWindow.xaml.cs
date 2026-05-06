using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using static WhichBox.NativeMethods;

namespace WhichBox;

public sealed partial class MainWindow : Window
{
    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly string _machineName = Environment.MachineName;
    private readonly Settings _settings;
    private readonly NativeContextMenu _contextMenu;
    private readonly UpdateChecker _updateChecker = new();
    private readonly uint _taskbarCreatedMsg;
    private readonly DispatcherTimer _healthCheck;
    private nint _prevWndProc;
    private WndProcDelegate? _wndProcDelegate; // prevent GC
    private bool _parentedToTaskbar;
    private bool _restarting;
    private int _heartbeatTickCounter;

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _settings = Settings.Load();
        _contextMenu = new NativeContextMenu();

        // Re-assert the native crash filter in case WinUI / WindowsAppRuntime
        // installed its own SetUnhandledExceptionFilter during initialization.
        NativeCrashHandler.Install();

        var initialTaskbar = FindWindowW("Shell_TrayWnd", null);
        Logger.Info($"MainWindow ctor: hwnd=0x{_hwnd:X} parent=0x{GetParent(_hwnd):X} taskbar=0x{initialTaskbar:X} machine={_machineName} dpi={GetDpiForWindow(_hwnd)}");

        // Register for the shell's TaskbarCreated message, broadcast when
        // explorer.exe recreates the taskbar (e.g., after a crash or DPI change).
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        // Register for session change notifications (RDP connect/disconnect, lock/unlock).
        WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);

        // Subclass our HWND to catch WM_DPICHANGED, WM_DISPLAYCHANGE,
        // WM_WTSSESSION_CHANGE, and the TaskbarCreated shell message
        // so we can reposition/re-parent.
        _wndProcDelegate = WndProc;
        _prevWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        // Hide title bar
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(false, false);
        }

        // Enable true window transparency using WinUIEx's TransparentTintBackdrop.
        // Applied before SetParent; DWM composition persists through reparenting.
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop();

        MachineNameText.Text = _machineName;
        ApplyColor();

        // Handle right-click. RightTapped fires after button release, so
        // TrackPopupMenu won't be dismissed by the mouse-up event.
        // We defer the call so it runs after WinUI finishes processing
        // the pointer event chain.
        Root.RightTapped += (_, e) =>
        {
            try
            {
                e.Handled = true;
                GetCursorPos(out var pt);
                Logger.Info($"RightTapped at cursor=({pt.X},{pt.Y}){Environment.NewLine}{CaptureClickDiagnostics(pt)}");
                SafeEnqueue("HandleContextMenu", () => HandleContextMenu(pt.X, pt.Y));
            }
            catch (Exception ex)
            {
                Logger.Crash("Root.RightTapped", ex);
            }
        };

        // Once content renders, set up the composition mask and move to taskbar
        Root.Loaded += (_, _) =>
        {
            try
            {
                Logger.Info("Root.Loaded fired");
                // Scale fade distance by DPI so the gradient looks consistent across monitors.
                // At 200% DPI, 24px fade on a ~124px-wide window ≈ 19% horizontal fade.
                // At 100% DPI, 12px fade on a ~74px-wide window ≈ 16% horizontal fade.
                var dpi = GetDpiForWindow(_hwnd);
                var fadePx = 14f * (dpi / 96f);
                CompositionMaskHelper.Apply(Root, LabelBorder, ContentHost, MaskHost, fadePixels: fadePx);
                MoveToTaskbar();

                // Check for updates in the background (fire and forget)
                _updateChecker.UpdateFound += () =>
                    SafeEnqueue("UpdateChecker.UpdateFound", () => UpdateDot.Visibility = Visibility.Visible);
                _ = _updateChecker.CheckAsync();
            }
            catch (Exception ex)
            {
                Logger.Crash("Root.Loaded", ex);
            }
        };

        // Periodic health check: if our parent taskbar HWND becomes invalid
        // (e.g., after session switch, Explorer restart we somehow missed),
        // re-parent to the current taskbar. Also emits a heartbeat once a
        // minute so a silent disappearance can be pinned down to a 60-second
        // window in the log.
        _healthCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthCheck.Tick += (_, _) =>
        {
            try
            {
                var taskbar = FindWindowW("Shell_TrayWnd", null);
                if (taskbar == 0)
                {
                    Logger.Warn($"HealthCheck: Shell_TrayWnd not found (Win32 err={Marshal.GetLastPInvokeError()})");
                    return;
                }
                var currentParent = GetParent(_hwnd);
                if (!_parentedToTaskbar || currentParent != taskbar)
                {
                    Logger.Info($"HealthCheck: re-parenting needed (flag={_parentedToTaskbar} parent=0x{currentParent:X} taskbar=0x{taskbar:X})");
                    _parentedToTaskbar = false;
                    MoveToTaskbar();
                }

                // Heartbeat: 12 ticks * 5s = 60s.
                if (++_heartbeatTickCounter >= 12)
                {
                    _heartbeatTickCounter = 0;
                    EmitHeartbeat(currentParent, taskbar);
                }
            }
            catch (Exception ex)
            {
                Logger.Crash("HealthCheck.Tick", ex);
            }
        };
        _healthCheck.Start();

        Closed += (_, _) =>
        {
            try
            {
                Logger.Info("MainWindow.Closed fired -- shutting down cleanly");
                _healthCheck.Stop();
                WTSUnRegisterSessionNotification(_hwnd);
                _contextMenu.Destroy();
            }
            catch (Exception ex)
            {
                Logger.Crash("MainWindow.Closed", ex);
            }
        };
    }

    /// <summary>
    /// Spawns a fresh WhichBox.exe with --wait-for-pid pointing at our
    /// own process ID, then exits immediately. The new process waits for
    /// us to die before initializing, ensuring no race on shared
    /// WindowsAppRuntime / COM endpoints.
    ///
    /// Used as the only viable mitigation for the WinUI 3
    /// DesktopChildSiteBridge fail-fast that fires shortly after every
    /// RDP/console session transition. The bridge corruption can't be
    /// avoided in-process, so we sidestep it by being a different
    /// process by the time the next system-pumped message arrives.
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

    /// <summary>
    /// Captures a snapshot of cursor / window / monitor / DPI state at the
    /// moment of a right-click. Used to diagnose why the context menu
    /// sometimes appears on a different monitor than the indicator.
    /// </summary>
    internal string CaptureClickDiagnostics(POINT cursor)
    {
        var sb = new StringBuilder();
        try
        {
            var dpiCtx = GetThreadDpiAwarenessContext();
            sb.AppendLine($"  thread DPI context : {DpiContextName(dpiCtx)} (raw=0x{dpiCtx:X})");
            sb.AppendLine($"  system DPI         : {GetDpiForSystem()}");
            sb.AppendLine($"  WhichBox HWND DPI  : {GetDpiForWindow(_hwnd)}");

            if (GetWindowRect(_hwnd, out var winRect))
            {
                sb.AppendLine($"  WhichBox HWND rect : ({winRect.Left},{winRect.Top},{winRect.Right},{winRect.Bottom}) size={winRect.Right - winRect.Left}x{winRect.Bottom - winRect.Top}");
            }

            sb.AppendLine($"  cursor             : ({cursor.X},{cursor.Y})");

            AppendMonitorInfo(sb, "cursor monitor    ", MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST));
            AppendMonitorInfo(sb, "WhichBox monitor  ", MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST));

            var taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar != 0)
            {
                if (GetWindowRect(taskbar, out var tbRect))
                {
                    sb.AppendLine($"  taskbar HWND rect  : ({tbRect.Left},{tbRect.Top},{tbRect.Right},{tbRect.Bottom})");
                }
                AppendMonitorInfo(sb, "taskbar monitor   ", MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST));
            }

            var menuOwner = _contextMenu.OwnerHwnd;
            if (menuOwner != 0)
            {
                if (GetWindowRect(menuOwner, out var mownerRect))
                {
                    sb.AppendLine($"  menu owner rect    : ({mownerRect.Left},{mownerRect.Top},{mownerRect.Right},{mownerRect.Bottom})");
                }
                AppendMonitorInfo(sb, "menu owner monitor", MonitorFromWindow(menuOwner, MONITOR_DEFAULTTONEAREST));
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  (CaptureClickDiagnostics failed: {ex.Message})");
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendMonitorInfo(StringBuilder sb, string label, nint hMonitor)
    {
        if (hMonitor == 0)
        {
            sb.AppendLine($"  {label} : null");
            return;
        }
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(hMonitor, ref info))
        {
            var primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0 ? " PRIMARY" : "";
            sb.AppendLine($"  {label} : hMon=0x{hMonitor:X}{primary} rcMon=({info.rcMonitor.Left},{info.rcMonitor.Top},{info.rcMonitor.Right},{info.rcMonitor.Bottom}) rcWork=({info.rcWork.Left},{info.rcWork.Top},{info.rcWork.Right},{info.rcWork.Bottom})");
        }
        else
        {
            sb.AppendLine($"  {label} : hMon=0x{hMonitor:X} (GetMonitorInfo failed)");
        }
    }

    /// <summary>
    /// Posts an action onto the dispatcher queue and ensures any exception is
    /// logged. Without this wrapper, exceptions in posted callbacks bypass
    /// every handler we registered (TaskScheduler, AppDomain, Application)
    /// and silently disappear, which is exactly the failure mode we are
    /// trying to diagnose.
    /// </summary>
    private void SafeEnqueue(string source, Action action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { Logger.Crash(source, ex); }
        });
    }

    /// <summary>
    /// Writes a one-line status snapshot to the log every 60 seconds. If the
    /// process dies silently, the absence of further heartbeats narrows the
    /// time-of-death to a 60-second window. Includes DWM-cloaked state and
    /// IsWindow/IsWindowVisible so we can also tell "process is alive but
    /// the window was destroyed/cloaked" apart from "process is dead".
    /// </summary>
    private void EmitHeartbeat(nint currentParent, nint taskbar)
    {
        try
        {
            bool isWindow = IsWindow(_hwnd);
            bool isVisible = isWindow && IsWindowVisible(_hwnd);
            int cloaked = 0;
            if (isWindow)
            {
                // Best-effort -- ignore HRESULT, default 0 means "not cloaked"
                _ = DwmGetWindowAttribute(_hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int));
            }
            var rect = default(RECT);
            if (isWindow) GetWindowRect(_hwnd, out rect);
            long memMB = 0;
            try { memMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024; } catch { }
            Logger.Info($"Heartbeat: window={isWindow} visible={isVisible} cloaked=0x{cloaked:X} parent=0x{currentParent:X} taskbar=0x{taskbar:X} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}) memMB={memMB}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"EmitHeartbeat failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
            {
                Logger.Info($"WndProc: {(msg == WM_DPICHANGED ? "WM_DPICHANGED" : "WM_DISPLAYCHANGE")} wParam=0x{wParam:X} lParam=0x{lParam:X}");
                // DPI or display configuration changed (e.g., RDP -> physical console).
                // Defer repositioning so the taskbar has time to finish resizing.
                SafeEnqueue("WndProc.RepositionAfterDpiChange", () => RepositionInTaskbar());
            }
            else if (msg == WM_WTSSESSION_CHANGE)
            {
                var reason = (int)wParam;
                Logger.Info($"WndProc: WM_WTSSESSION_CHANGE reason={WtsReasonName(reason)} sessionId=0x{lParam:X}");
                if (reason == WTS_CONSOLE_CONNECT || reason == WTS_REMOTE_CONNECT || reason == WTS_SESSION_UNLOCK)
                {
                    // Diagnosed via WER dump (0xC0000409 / FAST_FAIL_FATAL_APP_EXIT
                    // at Microsoft_UI_Input!DesktopChildSiteBridge::WndProc+0xfb):
                    // the WinUI 3 child-site bridge is corrupted by the session
                    // transition itself, not by anything we do to the HWND.
                    // It fails-fast on the next system-pumped message regardless
                    // of whether we touch the window. The only reliable mitigation
                    // is to spawn a fresh process with a clean bridge and exit.
                    SafeEnqueue("WndProc.SessionChangeRestart", () => SelfRestart($"session change ({WtsReasonName(reason)})"));
                }
            }
            else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                Logger.Info("WndProc: TaskbarCreated -- explorer recreated the taskbar, re-parenting");
                _parentedToTaskbar = false;
                SafeEnqueue("WndProc.TaskbarCreatedReparent", () => MoveToTaskbar());
            }
            else if (msg == WM_CLOSE || msg == WM_DESTROY || msg == WM_NCDESTROY)
            {
                // Catch unexpected window destruction. We don't normally close
                // the window except via the Exit menu item, so seeing these
                // messages outside of that flow is a strong diagnostic clue.
                var name = msg switch
                {
                    WM_CLOSE => "WM_CLOSE",
                    WM_DESTROY => "WM_DESTROY",
                    WM_NCDESTROY => "WM_NCDESTROY",
                    _ => $"0x{msg:X}"
                };
                Logger.Info($"WndProc: {name} received parent=0x{GetParent(hWnd):X} taskbar=0x{FindWindowW("Shell_TrayWnd", null):X}");
            }
        }
        catch (Exception ex)
        {
            Logger.Crash($"WndProc msg=0x{msg:X4}", ex);
        }

        return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Parents this window to the taskbar, using the Deskband11 technique:
    /// change style to WS_CHILD, SetParent onto Shell_TrayWnd, position
    /// to the left of TrayNotifyWnd.
    ///
    /// Idempotent: if the window is already correctly parented and styled
    /// (which is the common case after an RDP/console session switch
    /// because Explorer keeps the same Shell_TrayWnd HWND across sessions)
    /// the SetParent and SetWindowLongW calls are skipped. This avoids
    /// poking Microsoft.UI.Input.dll's per-HWND state during the input
    /// subsystem's session-transition window, which has been observed to
    /// trip __fastfail(FAST_FAIL_FATAL_APP_EXIT) inside the WindowsAppRuntime.
    /// </summary>
    private void MoveToTaskbar()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        Logger.Info($"MoveToTaskbar: hwnd=0x{_hwnd:X} taskbar=0x{taskbar:X} parentedFlag={_parentedToTaskbar} currentParent=0x{GetParent(_hwnd):X}");
        if (taskbar == 0)
        {
            Logger.Warn($"MoveToTaskbar: Shell_TrayWnd not found, aborting (Win32 err={Marshal.GetLastPInvokeError()})");
            return;
        }

        if (!_parentedToTaskbar)
        {
            var currentStyle = GetWindowLongW(_hwnd, GWL_STYLE);
            var currentParent = GetParent(_hwnd);
            var desiredStyle = (currentStyle & ~(WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD;

            bool styleNeedsChange = currentStyle != desiredStyle;
            bool parentNeedsChange = currentParent != taskbar;

            if (styleNeedsChange)
            {
                SetWindowLongW(_hwnd, GWL_STYLE, desiredStyle);
                Logger.Info($"MoveToTaskbar: style 0x{currentStyle:X} -> 0x{desiredStyle:X}");
            }
            else
            {
                Logger.Info($"MoveToTaskbar: style already 0x{currentStyle:X}, skipping SetWindowLongW");
            }

            if (parentNeedsChange)
            {
                var prevParent = SetParent(_hwnd, taskbar);
                var setParentErr = Marshal.GetLastPInvokeError();
                var newParent = GetParent(_hwnd);
                Logger.Info($"MoveToTaskbar: SetParent prev=0x{prevParent:X} (Win32 err={setParentErr}), now=0x{newParent:X} (expected 0x{taskbar:X})");

                if (newParent != taskbar)
                {
                    Logger.Warn("MoveToTaskbar: SetParent FAILED -- not setting _parentedToTaskbar flag");
                    return;
                }
            }
            else
            {
                Logger.Info($"MoveToTaskbar: parent already 0x{taskbar:X}, skipping SetParent");
            }

            _parentedToTaskbar = true;
        }

        PositionInTaskbar(taskbar);
    }

    /// <summary>
    /// Repositions within the current taskbar parent. If the parent is
    /// no longer Shell_TrayWnd (taskbar was recreated), falls back to
    /// full MoveToTaskbar.
    /// </summary>
    private void RepositionInTaskbar()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == 0) return;

        // Verify we're still parented to the taskbar
        if (GetParent(_hwnd) != taskbar)
        {
            _parentedToTaskbar = false;
            MoveToTaskbar();
            return;
        }

        PositionInTaskbar(taskbar);
    }

    /// <summary>
    /// Calculates and applies the correct position and size within the taskbar.
    /// </summary>
    private void PositionInTaskbar(nint taskbar)
    {
        // After SetParent, the child window may inherit a different DPI awareness
        // context, causing GetWindowRect to return logical (virtualized) coords on
        // some machines but physical on others. Force per-monitor-v2 so
        // GetWindowRect always returns physical pixels, matching SetWindowPos.
        var prevContext = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            PositionInTaskbarCore(taskbar);
        }
        finally
        {
            SetThreadDpiAwarenessContext(prevContext);
        }
    }

    private void PositionInTaskbarCore(nint taskbar)
    {
        if (!GetWindowRect(taskbar, out var taskbarRect))
        {
            Logger.Warn($"PositionInTaskbar: GetWindowRect(taskbar) failed (Win32 err={Marshal.GetLastPInvokeError()})");
            return;
        }
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        Logger.Info($"PositionInTaskbar: taskbarRect=({taskbarRect.Left},{taskbarRect.Top},{taskbarRect.Right},{taskbarRect.Bottom}) size={taskbarWidth}x{taskbarHeight}");

        // Inset vertically so the window doesn't fill the full taskbar height.
        var scale = GetDpiForWindow(taskbar) / 96.0;
        var verticalInset = (int)(4 * scale);
        var windowHeight = taskbarHeight - (verticalInset * 2);

        // Scale font size to fit the taskbar height (in logical pixels for WinUI)
        var logicalHeight = windowHeight / scale;
        // Font scales with taskbar height and DPI. At higher DPI the extra physical
        // pixels let larger text look crisp; at 100% we match system tray text size.
        var fontSize = Math.Max(10, logicalHeight * 0.275 * scale);

        // Width estimate based on dynamic font size (Segoe UI Bold ~0.75 em per char)
        var charWidth = fontSize * 0.75;
        var horizontalPad = fontSize * 0.8;
        var estimatedWidth = (int)((_machineName.Length * charWidth + horizontalPad * 2 + 4) * scale);

        // Scale font and padding to match DPI so the colored area extends proportionally.
        // Set directly (not via TryEnqueue) so WinUI layout updates before we measure.
        MachineNameText.FontSize = fontSize;
        LabelBorder.Padding = new Microsoft.UI.Xaml.Thickness(horizontalPad, 2, horizontalPad, 2);
        Root.UpdateLayout();

        // First pass: set size and a temporary position (far left) so the
        // window is created at the right height, then measure its actual width.
        var firstPass = SetWindowPos(_hwnd, 0, 0, verticalInset, estimatedWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!firstPass)
        {
            Logger.Warn($"PositionInTaskbar: first-pass SetWindowPos failed (Win32 err={Marshal.GetLastPInvokeError()})");
        }

        // Read the actual window width -- WinUI may have clamped it to a minimum.
        if (!GetWindowRect(_hwnd, out var actualRect))
        {
            Logger.Warn($"PositionInTaskbar: GetWindowRect(self) after first pass failed (Win32 err={Marshal.GetLastPInvokeError()})");
            return;
        }
        var actualWidth = actualRect.Right - actualRect.Left;

        // Find the anchor point: TrayNotifyWnd left edge (includes the chevron).
        var trayNotify = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        int xPos;
        if (trayNotify != 0 && GetWindowRect(trayNotify, out var trayRect))
        {
            var anchorLeft = trayRect.Left - taskbarRect.Left;
            xPos = anchorLeft - actualWidth - (int)(4 * scale);
        }
        else
        {
            xPos = taskbarWidth - actualWidth - (int)(4 * scale);
        }

        // Defensive clamp: in some multi-monitor / RDP layouts TrayNotifyWnd
        // reports negative-coord rects relative to the taskbar (observed:
        // xPos=-709 placing the indicator off-screen left). Fall back to
        // the right-edge position if the calculated x is outside the
        // taskbar's visible bounds.
        var maxX = taskbarWidth - actualWidth - (int)(4 * scale);
        if (xPos < 0 || xPos > maxX)
        {
            Logger.Warn($"PositionInTaskbar: xPos={xPos} outside taskbar (width={taskbarWidth}); clamping to {maxX}");
            xPos = Math.Max(0, maxX);
        }

        // Second pass: position using the actual measured width.
        var ok = SetWindowPos(_hwnd, 0, xPos, verticalInset, actualWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
        var setPosErr = ok ? 0 : Marshal.GetLastPInvokeError();
        Logger.Info($"PositionInTaskbar: final SetWindowPos x={xPos} y={verticalInset} w={actualWidth} h={windowHeight} ok={ok} err={setPosErr} parent=0x{GetParent(_hwnd):X}");

        if (GetWindowRect(_hwnd, out var finalRect))
        {
            Logger.Info($"PositionInTaskbar: post-position GetWindowRect=({finalRect.Left},{finalRect.Top},{finalRect.Right},{finalRect.Bottom})");
        }
    }

    private void ApplyColor()
    {
        var entry = _settings.ChosenColor is { } chosen
            ? new PaletteEntry("Custom", chosen)
            : ColorPalette.GetDefaultColor(_machineName);

        var bg = entry.Color;
        var fg = ColorPalette.GetContrastForeground(bg);

        LabelBorder.Background = new SolidColorBrush(bg);
        Root.Background = new SolidColorBrush(Colors.Transparent);
        MachineNameText.Foreground = new SolidColorBrush(fg);
    }

    private void HandleContextMenu(int x, int y)
    {
        var result = _contextMenu.Show(x, y,
            startupChecked: StartupHelper.IsRegistered,
            updateVersion: _updateChecker.LatestVersion);
        switch (result.Action)
        {
            case MenuAction.SelectColor:
                _settings.ChosenColor = result.Color;
                _settings.Save();
                ApplyColor();
                break;
            case MenuAction.ResetColor:
                _settings.ChosenColor = null;
                _settings.Save();
                ApplyColor();
                break;
            case MenuAction.ToggleStartup:
                StartupHelper.SetRegistered(!StartupHelper.IsRegistered);
                break;
            case MenuAction.ReattachToTaskbar:
                Logger.Info("ReattachToTaskbar: user requested manual re-attach");
                _parentedToTaskbar = false;
                MoveToTaskbar();
                break;
            case MenuAction.OpenLogFolder:
                Logger.Info("OpenLogFolder: user requested log folder");
                Logger.OpenLogFolder();
                break;
            case MenuAction.CheckForUpdates:
                _ = _updateChecker.CheckAsync();
                break;
            case MenuAction.Update:
                _ = _updateChecker.DownloadAndInstallAsync();
                break;
            case MenuAction.Exit:
                Close();
                break;
        }
    }
}
