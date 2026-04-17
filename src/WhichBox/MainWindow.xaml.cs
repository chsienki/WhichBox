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

        // Register for the shell's TaskbarCreated message, broadcast when
        // explorer.exe recreates the taskbar (e.g., after a crash or DPI change).
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        // Subclass our HWND to catch WM_DPICHANGED, WM_DISPLAYCHANGE,
        // and the TaskbarCreated shell message so we can reposition/re-parent.
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
            CompositionMaskHelper.Apply(Root, LabelBorder, ContentHost, MaskHost, fadePixels: 24f);
            MoveToTaskbar();

            // Check for updates in the background (fire and forget)
            _ = _updateChecker.CheckAsync();
        };

        Closed += (_, _) => _contextMenu.Destroy();
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE)
        {
            // DPI or display configuration changed (e.g., RDP -> physical console).
            // Defer repositioning so the taskbar has time to finish resizing.
            DispatcherQueue.TryEnqueue(() => RepositionInTaskbar());
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
        if (taskbar == 0) return;

        if (!_parentedToTaskbar)
        {
            // Change style: remove WS_POPUP and all chrome bits, add WS_CHILD
            var style = GetWindowLongW(_hwnd, GWL_STYLE);
            style = (style & ~(WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD;
            SetWindowLongW(_hwnd, GWL_STYLE, style);

            SetParent(_hwnd, taskbar);
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
        if (!GetWindowRect(taskbar, out var taskbarRect)) return;
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;

        // Inset vertically so the window doesn't fill the full taskbar height.
        var scale = GetDpiForWindow(taskbar) / 96.0;
        var verticalInset = (int)(4 * scale);
        var estimatedWidth = (int)((_machineName.Length * 8 + 24) * scale);
        var windowHeight = taskbarHeight - (verticalInset * 2);

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
        SetWindowPos(_hwnd, 0, xPos, verticalInset, actualWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);
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
            case MenuAction.Update:
                _ = _updateChecker.DownloadAndInstallAsync();
                break;
            case MenuAction.Exit:
                Close();
                break;
        }
    }
}
