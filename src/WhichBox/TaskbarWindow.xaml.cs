using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using static WhichBox.NativeMethods;

namespace WhichBox;

/// <summary>
/// A single machine-name indicator parented into one taskbar. The application
/// runs one of these per taskbar (the primary <c>Shell_TrayWnd</c> plus one
/// <c>Shell_SecondaryTrayWnd</c> per additional monitor); <see cref="TaskbarManager"/>
/// creates, positions, and tears them down as monitors come and go.
/// </summary>
public sealed partial class TaskbarWindow : Window
{
    // Extra width, in average character widths, added around the machine name
    // so it always sits in consistent breathing room (box sized as if the name
    // were this many characters longer).
    private const int WidthPaddingChars = 10;

    private readonly nint _hwnd;
    private readonly AppWindow _appWindow;
    private readonly string _machineName;
    private readonly Settings _settings;
    private readonly UpdateChecker _updateChecker;
    private readonly TaskbarManager _manager;
    private readonly bool _isPrimary;

    // The taskbar this window is parented to. For the primary window this is
    // re-resolved from Shell_TrayWnd on every (re)position so we follow an
    // Explorer restart; for secondaries it is the assigned Shell_SecondaryTrayWnd.
    private nint _taskbar;
    private uint _taskbarCreatedMsg; // primary only
    private nint _prevWndProc;
    private WndProcDelegate? _wndProcDelegate; // prevent GC (primary only)
    private bool _parentedToTaskbar;

    // Last taskbar rect we positioned against, so the manager's timer can skip
    // repositioning unless the taskbar actually moved or resized.
    private RECT _lastTaskbarRect;
    private bool _hasLastTaskbarRect;

    // Set while the indicator is hidden because its taskbar is too small to
    // share (see ApplyAutoHide). Tracked so we only call ShowWindow on a real
    // transition and so diagnostics can explain a missing indicator.
    private bool _autoHidden;
    private string? _autoHideReason;

    // Snapshot of the intermediate values from the most recent positioning
    // pass, surfaced by the "Capture Diagnostics" menu item so sizing bugs
    // across machines/DPIs can be reproduced from a single report.
    private PositioningSnapshot? _lastPositioning;

    /// <summary>This window's own HWND.</summary>
    internal nint Hwnd => _hwnd;

    /// <summary>The taskbar HWND this window is currently attached to.</summary>
    internal nint TaskbarHwnd => _taskbar;

    /// <summary>True for the window bound to the primary <c>Shell_TrayWnd</c>.</summary>
    internal bool IsPrimary => _isPrimary;

    /// <summary>Final positioned rect from the most recent pass, if any.</summary>
    internal RECT? LastFinalRect => _lastPositioning?.FinalRect;

    internal TaskbarWindow(nint taskbar, bool isPrimary, TaskbarManager manager)
    {
        InitializeComponent();

        _taskbar = taskbar;
        _isPrimary = isPrimary;
        _manager = manager;
        _settings = manager.Settings;
        _machineName = manager.MachineName;
        _updateChecker = manager.UpdateChecker;

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        // Re-assert the native crash filter in case WinUI / WindowsAppRuntime
        // installed its own SetUnhandledExceptionFilter during initialization.
        NativeCrashHandler.Install();

        Logger.Info($"TaskbarWindow ctor: hwnd=0x{_hwnd:X} primary={_isPrimary} taskbar=0x{_taskbar:X} parent=0x{GetParent(_hwnd):X} machine={_machineName} dpi={GetDpiForWindow(_hwnd)}");

        // Only the primary window drives structural change detection: it
        // registers for session-change notifications and subclasses its HWND to
        // catch WM_DPICHANGED / WM_DISPLAYCHANGE / TaskbarCreated, forwarding
        // them to the manager. Secondary windows are created, positioned, and
        // closed entirely by the manager's reconcile loop, so they need no hooks.
        if (_isPrimary)
        {
            // TaskbarCreated is broadcast when explorer.exe recreates the taskbar.
            _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

            // Session change notifications (RDP connect/disconnect, lock/unlock).
            WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION);

            _wndProcDelegate = WndProc;
            _prevWndProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC,
                Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        }

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
        // the pointer event chain. The menu is shared and owned by the manager.
        Root.RightTapped += (_, e) =>
        {
            try
            {
                e.Handled = true;
                GetCursorPos(out var pt);
                Logger.Info($"RightTapped at cursor=({pt.X},{pt.Y}){Environment.NewLine}{CaptureClickDiagnostics(pt)}");
                SafeEnqueue("ShowContextMenu", () => _manager.ShowContextMenu(pt.X, pt.Y, this));
            }
            catch (Exception ex)
            {
                Logger.Crash("Root.RightTapped", ex);
            }
        };

        // Once content renders, set up the composition mask and move to taskbar.
        Root.Loaded += (_, _) =>
        {
            try
            {
                Logger.Info($"Root.Loaded fired (primary={_isPrimary})");
                CompositionMaskHelper.Apply(Root, LabelBorder, ContentHost, MaskHost);
                MoveToTaskbar();

                // If the manager already found an update, show the dot immediately
                // (a window created after the check still needs to reflect it).
                if (_updateChecker.LatestVersion is not null)
                    UpdateDot.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Logger.Crash("Root.Loaded", ex);
            }
        };

        Closed += (_, _) =>
        {
            try
            {
                Logger.Info($"TaskbarWindow.Closed fired (primary={_isPrimary} taskbar=0x{_taskbar:X})");
                if (_isPrimary)
                    WTSUnRegisterSessionNotification(_hwnd);
                _manager.OnWindowClosed(this);
            }
            catch (Exception ex)
            {
                Logger.Crash("TaskbarWindow.Closed", ex);
            }
        };
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

            var taskbar = _taskbar;
            if (taskbar != 0)
            {
                if (GetWindowRect(taskbar, out var tbRect))
                {
                    sb.AppendLine($"  taskbar HWND rect  : ({tbRect.Left},{tbRect.Top},{tbRect.Right},{tbRect.Bottom})");
                }
                AppendMonitorInfo(sb, "taskbar monitor   ", MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST));
            }

            var menuOwner = _manager.ContextMenu.OwnerHwnd;
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
    /// Builds a complete, self-contained sizing report: environment, every
    /// monitor with its per-monitor DPI, taskbar geometry, live WinUI
    /// measurements, and the exact intermediate values from the most recent
    /// positioning pass. Designed to be pasted (with a screenshot) so sizing
    /// bugs across machines and resolutions can be reproduced from the numbers.
    /// </summary>
    internal string BuildDiagnosticsReport()
    {
        var sb = new StringBuilder();
        // Force PMv2 so every rect/DPI read below is in consistent physical
        // pixels, matching what PositionInTaskbar uses.
        var prevCtx = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        try
        {
            sb.AppendLine("===== WhichBox Sizing Diagnostics =====");
            sb.AppendLine($"Captured      : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine($"Version       : {Logger.Version}");
            sb.AppendLine($"Machine       : {_machineName} (len={_machineName.Length})");
            sb.AppendLine($"Arch          : {RuntimeInformation.ProcessArchitecture} (OS {RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"OS            : {RuntimeInformation.OSDescription}");
            sb.AppendLine($"RemoteSession : {GetSystemMetrics(SM_REMOTESESSION) != 0}");
            sb.AppendLine($"SESSIONNAME   : {Environment.GetEnvironmentVariable("SESSIONNAME")}");
            sb.AppendLine();

            var dpiCtx = GetThreadDpiAwarenessContext();
            var hwndDpi = GetDpiForWindow(_hwnd);
            sb.AppendLine("--- DPI / awareness ---");
            sb.AppendLine($"thread DPI ctx : {DpiContextName(dpiCtx)}");
            sb.AppendLine($"system DPI     : {GetDpiForSystem()}");
            sb.AppendLine($"WhichBox HWND  : dpi={hwndDpi} scale={hwndDpi / 96.0:0.###}");
            sb.AppendLine();

            var monitors = GetAllMonitors();
            sb.AppendLine($"--- Monitors ({monitors.Count}) ---");
            for (int i = 0; i < monitors.Count; i++)
            {
                AppendMonitorLine(sb, i, monitors[i]);
            }
            sb.AppendLine();

            sb.AppendLine("--- Taskbars ---");
            var primaryTaskbar = FindPrimaryTaskbar();
            AppendTaskbarLine(sb, "Shell_TrayWnd (primary)", primaryTaskbar);
            var secondaryTaskbars = FindSecondaryTaskbars();
            for (int i = 0; i < secondaryTaskbars.Count; i++)
            {
                AppendTaskbarLine(sb, $"Shell_SecondaryTrayWnd[{i}]", secondaryTaskbars[i]);
            }
            sb.AppendLine();

            sb.AppendLine($"--- Managed windows ({_manager.Windows.Count}) ---");
            sb.AppendLine($"this window    : hwnd=0x{_hwnd:X} primary={_isPrimary} taskbar=0x{_taskbar:X}");
            foreach (var win in _manager.Windows)
            {
                var rectStr = win.LastFinalRect is { } fr
                    ? $"({fr.Left},{fr.Top},{fr.Right},{fr.Bottom}) size={fr.Right - fr.Left}x{fr.Bottom - fr.Top}"
                    : "(not positioned yet)";
                sb.AppendLine($"  hwnd=0x{win.Hwnd:X} primary={win.IsPrimary} taskbar=0x{win.TaskbarHwnd:X} lastRect={rectStr}");
            }
            sb.AppendLine();

            sb.AppendLine("--- WinUI live measurements ---");
            try
            {
                if (Root.XamlRoot is { } xr)
                {
                    sb.AppendLine($"XamlRoot RasterizationScale : {xr.RasterizationScale:0.###}");
                    sb.AppendLine($"XamlRoot Size (DIPs)        : {xr.Size.Width:0.#}x{xr.Size.Height:0.#}");
                }
                sb.AppendLine($"Root ActualSize        : {Root.ActualWidth:0.#}x{Root.ActualHeight:0.#}");
                sb.AppendLine($"LabelBorder ActualSize : {LabelBorder.ActualWidth:0.#}x{LabelBorder.ActualHeight:0.#}");
                sb.AppendLine($"Text ActualSize        : {MachineNameText.ActualWidth:0.#}x{MachineNameText.ActualHeight:0.#}");
                sb.AppendLine($"Text DesiredSize       : {MachineNameText.DesiredSize.Width:0.#}x{MachineNameText.DesiredSize.Height:0.#}");
                sb.AppendLine($"Text FontSize          : {MachineNameText.FontSize:0.##}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"(WinUI measurements failed: {ex.Message})");
            }
            sb.AppendLine();

            sb.AppendLine("--- Last positioning pass ---");
            sb.AppendLine($"autoHide       : enabled={_settings.HideOnNarrowTaskbar} threshold={_settings.NarrowTaskbarWidth} logical px, hidden={_autoHidden}{(_autoHideReason is { } r ? $" ({r})" : "")}");
            if (_lastPositioning is { } s)
            {
                var age = (DateTime.UtcNow - s.TimestampUtc).TotalSeconds;
                sb.AppendLine($"age            : {age:0.#}s ago");
                sb.AppendLine($"taskbar dpi    : {s.TaskbarDpi} scale={s.Scale:0.###}");
                sb.AppendLine($"taskbar rect   : ({s.TaskbarRect.Left},{s.TaskbarRect.Top},{s.TaskbarRect.Right},{s.TaskbarRect.Bottom}) size={s.TaskbarWidth}x{s.TaskbarHeight}");
                sb.AppendLine($"verticalInset  : {s.VerticalInset}");
                sb.AppendLine($"windowHeight   : {s.WindowHeight}");
                sb.AppendLine($"logicalHeight  : {s.LogicalHeight:0.##}");
                sb.AppendLine($"fontSize       : {s.FontSize:0.##}");
                sb.AppendLine($"horizontalPad  : {s.HorizontalPad:0.##}");
                sb.AppendLine($"measuredTextDip: {s.MeasuredTextDip:0.##}");
                sb.AppendLine($"paddedTextDip  : {s.PaddedTextDip:0.##} (+{WidthPaddingChars} chars)");
                sb.AppendLine($"rasterScale    : {s.RasterScale:0.###}");
                sb.AppendLine($"contentWidth   : {s.ContentWidth}");
                sb.AppendLine($"actualWidth    : {s.ActualWidth}{(s.ActualWidth > s.ContentWidth ? "  <-- WinUI min-width clamp" : "")}");
                sb.AppendLine($"trayNotify     : found={s.TrayNotifyFound} rect=({s.TrayNotifyRect.Left},{s.TrayNotifyRect.Top},{s.TrayNotifyRect.Right},{s.TrayNotifyRect.Bottom})");
                sb.AppendLine($"anchorLeft     : {s.AnchorLeft}");
                sb.AppendLine($"xPos           : {s.XPos} (maxX={s.MaxX})");
                sb.AppendLine($"final rect     : ({s.FinalRect.Left},{s.FinalRect.Top},{s.FinalRect.Right},{s.FinalRect.Bottom}) size={s.FinalRect.Right - s.FinalRect.Left}x{s.FinalRect.Bottom - s.FinalRect.Top}");
            }
            else
            {
                sb.AppendLine("(no positioning pass recorded yet)");
            }
            sb.AppendLine("=======================================");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(BuildDiagnosticsReport failed: {ex.Message})");
        }
        finally
        {
            SetThreadDpiAwarenessContext(prevCtx);
        }
        return sb.ToString();
    }

    private static void AppendMonitorLine(StringBuilder sb, int index, nint hMon)
    {
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(hMon, ref info))
        {
            sb.AppendLine($"[{index}] 0x{hMon:X} (GetMonitorInfo failed)");
            return;
        }
        var primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0 ? "PRIMARY" : "       ";
        uint effDpi = 0, rawDpi = 0;
        GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out effDpi, out _);
        GetDpiForMonitor(hMon, MDT_RAW_DPI, out rawDpi, out _);
        var m = info.rcMonitor;
        var w = info.rcWork;
        sb.AppendLine($"[{index}] {primary} rcMon=({m.Left},{m.Top},{m.Right},{m.Bottom}) {m.Right - m.Left}x{m.Bottom - m.Top} rcWork=({w.Left},{w.Top},{w.Right},{w.Bottom}) effDpi={effDpi} rawDpi={rawDpi} scale={effDpi / 96.0:0.###}");
    }

    private static void AppendTaskbarLine(StringBuilder sb, string label, nint taskbar)
    {
        if (taskbar == 0 || !GetWindowRect(taskbar, out var tb))
        {
            sb.AppendLine($"{label} : not found");
            return;
        }
        var tbDpi = GetDpiForWindow(taskbar);
        var mon = MonitorFromWindow(taskbar, MONITOR_DEFAULTTONEAREST);
        sb.AppendLine($"{label} : HWND=0x{taskbar:X} rect=({tb.Left},{tb.Top},{tb.Right},{tb.Bottom}) size={tb.Right - tb.Left}x{tb.Bottom - tb.Top} dpi={tbDpi} scale={tbDpi / 96.0:0.###} mon=0x{mon:X}");
        var tray = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        if (tray != 0 && GetWindowRect(tray, out var trr))
        {
            sb.AppendLine($"    TrayNotifyWnd rect=({trr.Left},{trr.Top},{trr.Right},{trr.Bottom}) size={trr.Right - trr.Left}x{trr.Bottom - trr.Top}");
        }
        else
        {
            sb.AppendLine("    TrayNotifyWnd : not found (positions to right edge)");
        }
    }

    /// <summary>
    /// Refreshes the positioning snapshot, builds the diagnostics report, then
    /// saves it to diagnostics.txt, copies it to the clipboard, and opens the
    /// folder. The file is written first so the data survives if the user then
    /// copies a screenshot (which overwrites the clipboard).
    /// </summary>
    internal void HandleCopyDiagnostics()
    {
        // Recompute position so the snapshot matches what's on screen right now.
        try { RepositionInTaskbar(); }
        catch (Exception ex) { Logger.Warn($"CopyDiagnostics: reposition failed: {ex.Message}"); }

        var report = BuildDiagnosticsReport();

        string? savedPath = null;
        try
        {
            savedPath = Path.Combine(Logger.Folder, "diagnostics.txt");
            File.WriteAllText(savedPath, report);
        }
        catch (Exception ex)
        {
            Logger.Warn($"CopyDiagnostics: save failed: {ex.Message}");
        }

        bool copied = false;
        try { copied = TrySetClipboardText(_hwnd, report); }
        catch (Exception ex) { Logger.Warn($"CopyDiagnostics: clipboard failed: {ex.Message}"); }

        Logger.Info($"CopyDiagnostics: copied={copied} saved={savedPath}{Environment.NewLine}{report}");
        Logger.OpenLogFolder();
    }

    /// <summary>
    /// Immutable capture of the intermediate values computed during a single
    /// PositionInTaskbar pass, for the diagnostics report.
    /// </summary>
    private readonly record struct PositioningSnapshot(
        DateTime TimestampUtc,
        RECT TaskbarRect, int TaskbarWidth, int TaskbarHeight, uint TaskbarDpi, double Scale,
        int VerticalInset, int WindowHeight, double LogicalHeight, double FontSize,
        double HorizontalPad, double MeasuredTextDip, double PaddedTextDip, double RasterScale, int ContentWidth, int ActualWidth,
        bool TrayNotifyFound, RECT TrayNotifyRect, int AnchorLeft, int XPos, int MaxX,
        RECT FinalRect);

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
    /// Writes a one-line status snapshot to the log. Called once a minute by the
    /// manager's heartbeat. Includes DWM-cloaked state and IsWindow/IsWindowVisible
    /// so we can tell "process is alive but the window was destroyed/cloaked"
    /// apart from "process is dead".
    /// </summary>
    internal void LogHeartbeat()
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
            Logger.Info($"Heartbeat[{(_isPrimary ? "primary" : "secondary")}]: window={isWindow} visible={isVisible} autoHidden={_autoHidden} cloaked=0x{cloaked:X} parent=0x{GetParent(_hwnd):X} taskbar=0x{_taskbar:X} rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})");
        }
        catch (Exception ex)
        {
            Logger.Warn($"LogHeartbeat failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
            {
                var name = msg == WM_DPICHANGED ? "WM_DPICHANGED" : "WM_DISPLAYCHANGE";
                Logger.Info($"WndProc: {name} wParam=0x{wParam:X} lParam=0x{lParam:X}");
                // DPI or display configuration changed (monitor plugged/unplugged,
                // resolution change, RDP <-> physical console). Let the manager
                // re-sync the whole set of windows and reposition them. Deferred so
                // the shell has time to finish creating/resizing taskbars first.
                SafeEnqueue("WndProc.DisplayChanged", () => _manager.OnDisplayChanged(name));
            }
            else if (msg == WM_WTSSESSION_CHANGE)
            {
                var reason = (int)wParam;
                Logger.Info($"WndProc: WM_WTSSESSION_CHANGE reason={WtsReasonName(reason)} sessionId=0x{lParam:X}");
                // Diagnosed via WER dump (0xC0000409 / FAST_FAIL_FATAL_APP_EXIT at
                // Microsoft_UI_Input!DesktopChildSiteBridge::WndProc+0xfb): the
                // WinUI 3 child-site bridge is corrupted by the session transition
                // itself and fails-fast on the next pumped message. The manager
                // relaunches into a fresh process as the only reliable mitigation.
                SafeEnqueue("WndProc.SessionChange", () => _manager.HandleSessionChange(reason));
            }
            else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
            {
                Logger.Info("WndProc: TaskbarCreated -- explorer recreated the taskbars, re-syncing");
                SafeEnqueue("WndProc.TaskbarCreated", () => _manager.OnTaskbarRecreated());
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
                Logger.Info($"WndProc: {name} received parent=0x{GetParent(hWnd):X} taskbar=0x{FindPrimaryTaskbar():X}");
            }
        }
        catch (Exception ex)
        {
            Logger.Crash($"WndProc msg=0x{msg:X4}", ex);
        }

        return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Parents this window to its taskbar, using the Deskband11 technique:
    /// change style to WS_CHILD, SetParent onto the taskbar, position to the
    /// left of the tray (or the right edge on secondary taskbars with no tray).
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
        // The primary window follows Shell_TrayWnd wherever Explorer puts it
        // (surviving an Explorer restart that hands out a new HWND). Secondary
        // windows stay bound to the specific Shell_SecondaryTrayWnd they were
        // created for; if that vanishes, the manager closes this window instead.
        if (_isPrimary)
            _taskbar = FindPrimaryTaskbar();

        var taskbar = _taskbar;
        Logger.Info($"MoveToTaskbar: hwnd=0x{_hwnd:X} primary={_isPrimary} taskbar=0x{taskbar:X} parentedFlag={_parentedToTaskbar} currentParent=0x{GetParent(_hwnd):X}");
        if (taskbar == 0)
        {
            Logger.Warn($"MoveToTaskbar: taskbar HWND not available, aborting (Win32 err={Marshal.GetLastPInvokeError()})");
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
    /// Repositions within the current taskbar parent. If the parent is no
    /// longer the expected taskbar (e.g., it was recreated), falls back to a
    /// full re-parent via <see cref="MoveToTaskbar"/>.
    /// </summary>
    private void RepositionInTaskbar()
    {
        if (_isPrimary)
        {
            var found = FindPrimaryTaskbar();
            if (found != 0) _taskbar = found;
        }
        var taskbar = _taskbar;
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
    /// Manager entry point: re-verify parenting and reposition. When
    /// <paramref name="force"/> is false (a plain reconcile tick) the window is
    /// only repositioned if its taskbar actually moved or resized, so idle ticks
    /// stay cheap.
    /// </summary>
    internal void RefreshPosition(bool force)
    {
        if (_isPrimary)
        {
            var found = FindPrimaryTaskbar();
            if (found != 0) _taskbar = found;
        }
        var taskbar = _taskbar;
        if (taskbar == 0) return;

        // Re-parent if we've drifted (Explorer recreated the taskbar, a missed
        // session switch, etc.). This subsumes the old health-check behaviour.
        if (!_parentedToTaskbar || GetParent(_hwnd) != taskbar)
        {
            Logger.Info($"RefreshPosition: re-parent needed (flag={_parentedToTaskbar} parent=0x{GetParent(_hwnd):X} taskbar=0x{taskbar:X})");
            _parentedToTaskbar = false;
            MoveToTaskbar();
            return;
        }

        if (!force && _hasLastTaskbarRect)
        {
            // Re-assert the hidden state first: a window that crept back into
            // view would otherwise stay visible until the taskbar next resized.
            if (_autoHidden && IsWindowVisible(_hwnd))
                ApplyAutoHide(true, _autoHideReason);

            // Compare under PMv2 so the rect matches the one PositionInTaskbar
            // stored (GetWindowRect is DPI-awareness sensitive after SetParent).
            var prev = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            bool unchanged;
            try { unchanged = GetWindowRect(taskbar, out var rect) && RectEquals(rect, _lastTaskbarRect); }
            finally { SetThreadDpiAwarenessContext(prev); }
            if (unchanged) return;
        }

        PositionInTaskbar(taskbar);
    }

    /// <summary>
    /// Manager entry point: force a full re-parent and reposition. Used when the
    /// user picks "Re-attach to Taskbar" or Explorer recreates the taskbars.
    /// </summary>
    internal void ForceReattach()
    {
        _parentedToTaskbar = false;
        MoveToTaskbar();
    }

    /// <summary>Shows or hides the update-available dot. Must run on the UI thread.</summary>
    internal void SetUpdateDotVisible(bool visible) =>
        UpdateDot.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private static bool RectEquals(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>
    /// Shows or hides the indicator when the taskbar is too small to share it
    /// with, so a phone-sized session isn't left with an unreachable Start
    /// button. Only acts on a transition, and the window stays parented and
    /// positioned so it reappears as soon as there is room again.
    /// </summary>
    private void ApplyAutoHide(bool hide, string? reason)
    {
        _autoHideReason = hide ? reason : null;

        // Compare against the real visibility, not just our own flag: WinUI or
        // an Explorer restart can put the window back on screen behind our back.
        if (hide == !IsWindowVisible(_hwnd))
        {
            _autoHidden = hide;
            return;
        }

        _autoHidden = hide;
        ShowWindow(_hwnd, hide ? SW_HIDE : SW_SHOWNOACTIVATE);
        Logger.Info(hide
            ? $"AutoHide: hiding indicator ({reason})"
            : "AutoHide: taskbar has room again -- showing indicator");
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

        // Remember the geometry we're positioning against so the reconcile loop
        // can skip repositioning until the taskbar actually moves or resizes.
        _lastTaskbarRect = taskbarRect;
        _hasLastTaskbarRect = true;

        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        Logger.Info($"PositionInTaskbar: taskbarRect=({taskbarRect.Left},{taskbarRect.Top},{taskbarRect.Right},{taskbarRect.Bottom}) size={taskbarWidth}x{taskbarHeight}");

        // Inset vertically so the window doesn't fill the full taskbar height.
        var taskbarDpi = GetDpiForWindow(taskbar);
        var scale = taskbarDpi / 96.0;

        // On a phone-sized taskbar there simply isn't room to share: the
        // indicator lands on top of the Start button and swallows its clicks.
        var logicalTaskbarWidth = taskbarWidth / scale;
        if (_settings.HideOnNarrowTaskbar && logicalTaskbarWidth < _settings.NarrowTaskbarWidth)
        {
            ApplyAutoHide(true, $"taskbar {logicalTaskbarWidth:0} logical px wide < {_settings.NarrowTaskbarWidth}");
            return;
        }

        var verticalInset = (int)(4 * scale);
        var windowHeight = taskbarHeight - (verticalInset * 2);

        // Font size fits the taskbar height. Because the window renders at
        // RasterizationScale 1.0 in the taskbar (see below), the monitor scale
        // cancels out and fontSize reduces to windowHeight * 0.275 in physical
        // px -- a consistent text-height-to-taskbar ratio at every DPI.
        var logicalHeight = windowHeight / scale;
        var fontSize = Math.Max(10, logicalHeight * 0.275 * scale);
        var horizontalPad = fontSize * 0.8;

        // Apply font and padding directly (not via TryEnqueue) so layout
        // updates before we measure.
        MachineNameText.FontSize = fontSize;
        LabelBorder.Padding = new Microsoft.UI.Xaml.Thickness(horizontalPad, 2, horizontalPad, 2);
        Root.UpdateLayout();

        // Size the box to the text WinUI actually renders. Measure the natural
        // text width in DIPs, then convert to physical pixels using the scale
        // WinUI is really drawing at (XamlRoot.RasterizationScale). In the
        // taskbar that scale is 1.0 because cross-process SetParent breaks DPI
        // propagation, so the monitor scale must NOT be used here -- using it
        // oversizes the box about 2x at 200% DPI.
        MachineNameText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var measuredTextDip = MachineNameText.DesiredSize.Width;

        // Widen the box as if the name were WidthPaddingChars characters longer,
        // giving every machine the same breathing room around its name. The
        // extra is expressed in character widths (via this string's own average
        // glyph width) so it stays proportional to the font at any taskbar size.
        var avgCharDip = measuredTextDip / Math.Max(1, _machineName.Length);
        var paddedTextDip = measuredTextDip + avgCharDip * WidthPaddingChars;

        var rasterScale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var contentWidth = (int)Math.Ceiling((paddedTextDip + horizontalPad * 2) * rasterScale) + (int)(4 * scale);

        // First pass: set size and a temporary position (far left) so the
        // window is created at the right height, then measure its actual width.
        var firstPass = SetWindowPos(_hwnd, 0, 0, verticalInset, contentWidth, windowHeight,
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
        RECT trayRect = default;
        bool trayFound = trayNotify != 0 && GetWindowRect(trayNotify, out trayRect);
        int anchorLeft = 0;
        int xPos;
        if (trayFound)
        {
            anchorLeft = trayRect.Left - taskbarRect.Left;
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

        // The width test above uses a fixed threshold; this one scales with the
        // actual box. Reaching past the middle of the taskbar means the
        // indicator plus the tray own most of it, which is where it starts
        // covering the (centred) Start button.
        var crowded = _settings.HideOnNarrowTaskbar && xPos < taskbarWidth / 2;
        ApplyAutoHide(crowded, crowded ? $"indicator x={xPos} reaches past taskbar midpoint {taskbarWidth / 2}" : null);

        GetWindowRect(_hwnd, out var finalRect);
        Logger.Info($"PositionInTaskbar: post-position GetWindowRect=({finalRect.Left},{finalRect.Top},{finalRect.Right},{finalRect.Bottom})");

        _lastPositioning = new PositioningSnapshot(
            TimestampUtc: DateTime.UtcNow,
            TaskbarRect: taskbarRect, TaskbarWidth: taskbarWidth, TaskbarHeight: taskbarHeight,
            TaskbarDpi: taskbarDpi, Scale: scale,
            VerticalInset: verticalInset, WindowHeight: windowHeight, LogicalHeight: logicalHeight,
            FontSize: fontSize, HorizontalPad: horizontalPad,
            MeasuredTextDip: measuredTextDip, PaddedTextDip: paddedTextDip, RasterScale: rasterScale, ContentWidth: contentWidth,
            ActualWidth: actualWidth,
            TrayNotifyFound: trayFound, TrayNotifyRect: trayRect, AnchorLeft: anchorLeft,
            XPos: xPos, MaxX: maxX, FinalRect: finalRect);
    }

    internal void ApplyColor()
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
}
