using System.Runtime.InteropServices;
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

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _settings = Settings.Load();
        _contextMenu = new NativeContextMenu();

        DebugLog.Write($"MainWindow ctor: hwnd=0x{_hwnd:X} machine={_machineName} sessionId={Environment.GetEnvironmentVariable("SESSIONNAME")}");

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
            e.Handled = true;
            GetCursorPos(out var pt);
            DispatcherQueue.TryEnqueue(() => HandleContextMenu(pt.X, pt.Y));
        };

        // Once content renders, set up the composition mask and move to taskbar
        Root.Loaded += (_, _) =>
        {
            DebugLog.Write("Root.Loaded fired");
            // Scale fade distance by DPI so the gradient looks consistent across monitors.
            // At 200% DPI, 24px fade on a ~124px-wide window ≈ 19% horizontal fade.
            // At 100% DPI, 12px fade on a ~74px-wide window ≈ 16% horizontal fade.
            var dpi = GetDpiForWindow(_hwnd);
            var fadePx = 14f * (dpi / 96f);
            CompositionMaskHelper.Apply(Root, LabelBorder, ContentHost, MaskHost, fadePixels: fadePx);
            MoveToTaskbar();

            // Check for updates in the background (fire and forget)
            _updateChecker.UpdateFound += () =>
                DispatcherQueue.TryEnqueue(() => UpdateDot.Visibility = Visibility.Visible);
            _ = _updateChecker.CheckAsync();
        };

        // Periodic health check: if our parent taskbar HWND becomes invalid
        // (e.g., after session switch, Explorer restart we somehow missed),
        // re-parent to the current taskbar.
        _healthCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthCheck.Tick += (_, _) =>
        {
            var taskbar = FindWindowW("Shell_TrayWnd", null);
            if (taskbar == 0) return;
            var currentParent = GetParent(_hwnd);
            if (!_parentedToTaskbar || currentParent != taskbar)
            {
                DebugLog.Write($"HealthCheck: re-parenting needed (flag={_parentedToTaskbar} parent=0x{currentParent:X} taskbar=0x{taskbar:X})");
                _parentedToTaskbar = false;
                MoveToTaskbar();
            }
        };
        _healthCheck.Start();

        Closed += (_, _) =>
        {
            _healthCheck.Stop();
            WTSUnRegisterSessionNotification(_hwnd);
            _contextMenu.Destroy();
        };
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
        {
            // DPI or display configuration changed (e.g., RDP -> physical console).
            // Defer repositioning so the taskbar has time to finish resizing.
            DispatcherQueue.TryEnqueue(() => RepositionInTaskbar());
        }
        else if (msg == WM_WTSSESSION_CHANGE)
        {
            var reason = (int)wParam;
            if (reason == WTS_CONSOLE_CONNECT || reason == WTS_REMOTE_CONNECT || reason == WTS_SESSION_UNLOCK)
            {
                // Session switched (e.g., RDP to physical console or vice versa).
                // The taskbar HWND may have changed, so re-parent after a short delay
                // to let the shell finish setting up.
                _parentedToTaskbar = false;
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(1000);
                    MoveToTaskbar();
                });
            }
        }
        else if (_taskbarCreatedMsg != 0 && msg == _taskbarCreatedMsg)
        {
            // Explorer recreated the taskbar -- need to re-parent entirely.
            _parentedToTaskbar = false;
            DispatcherQueue.TryEnqueue(() => MoveToTaskbar());
        }

        return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }

    /// <summary>
    /// Parents this window to the taskbar, using the Deskband11 technique:
    /// change style to WS_CHILD, SetParent onto Shell_TrayWnd, position
    /// to the left of TrayNotifyWnd.
    /// </summary>
    private void MoveToTaskbar()
    {
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        DebugLog.Write($"MoveToTaskbar: hwnd=0x{_hwnd:X} taskbar=0x{taskbar:X} parentedFlag={_parentedToTaskbar} currentParent=0x{GetParent(_hwnd):X}");
        if (taskbar == 0)
        {
            DebugLog.Write("MoveToTaskbar: Shell_TrayWnd not found, aborting");
            return;
        }

        if (!_parentedToTaskbar)
        {
            // Change style: remove WS_POPUP and all chrome bits, add WS_CHILD
            var style = GetWindowLongW(_hwnd, GWL_STYLE);
            var newStyle = (style & ~(WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD;
            SetWindowLongW(_hwnd, GWL_STYLE, newStyle);
            DebugLog.Write($"MoveToTaskbar: style 0x{style:X} -> 0x{newStyle:X}");

            var prevParent = SetParent(_hwnd, taskbar);
            var newParent = GetParent(_hwnd);
            DebugLog.Write($"MoveToTaskbar: SetParent returned prev=0x{prevParent:X}, GetParent now=0x{newParent:X} (expected 0x{taskbar:X})");

            if (newParent != taskbar)
            {
                DebugLog.Write("MoveToTaskbar: SetParent FAILED -- not setting _parentedToTaskbar flag");
                return;
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
            DebugLog.Write("PositionInTaskbar: GetWindowRect(taskbar) failed");
            return;
        }
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        DebugLog.Write($"PositionInTaskbar: taskbarRect=({taskbarRect.Left},{taskbarRect.Top},{taskbarRect.Right},{taskbarRect.Bottom}) size={taskbarWidth}x{taskbarHeight}");

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
        SetWindowPos(_hwnd, 0, 0, verticalInset, estimatedWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Read the actual window width -- WinUI may have clamped it to a minimum.
        if (!GetWindowRect(_hwnd, out var actualRect)) return;
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

        // Second pass: position using the actual measured width.
        var ok = SetWindowPos(_hwnd, 0, xPos, verticalInset, actualWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
        DebugLog.Write($"PositionInTaskbar: final SetWindowPos x={xPos} y={verticalInset} w={actualWidth} h={windowHeight} ok={ok} parent=0x{GetParent(_hwnd):X}");

        if (GetWindowRect(_hwnd, out var finalRect))
        {
            DebugLog.Write($"PositionInTaskbar: post-position GetWindowRect=({finalRect.Left},{finalRect.Top},{finalRect.Right},{finalRect.Bottom})");
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
                DebugLog.Write("ReattachToTaskbar: user requested manual re-attach");
                _parentedToTaskbar = false;
                MoveToTaskbar();
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
