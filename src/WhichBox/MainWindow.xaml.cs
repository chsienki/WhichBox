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

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _settings = Settings.Load();
        _contextMenu = new NativeContextMenu();

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
        };

        Closed += (_, _) => _contextMenu.Destroy();
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

        // Change style: remove WS_POPUP and all chrome bits, add WS_CHILD
        var style = GetWindowLongW(_hwnd, GWL_STYLE);
        style = (style & ~(WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_CHILD;
        SetWindowLongW(_hwnd, GWL_STYLE, style);

        // Parent to the taskbar
        SetParent(_hwnd, taskbar);

        // GetWindowRect returns logical (DPI-virtualized) coordinates, but after
        // SetParent the child window's SetWindowPos operates in the taskbar's
        // physical pixel coordinate space. We must scale by the DPI factor.
        var dpi = GetDpiForWindow(taskbar);
        var scale = dpi / 96.0;

        if (!GetWindowRect(taskbar, out var taskbarRect)) return;
        var taskbarHeight = (int)((taskbarRect.Bottom - taskbarRect.Top) * scale);
        var taskbarWidth = (int)((taskbarRect.Right - taskbarRect.Left) * scale);

        // Initial size estimate -- WinUI may enforce a minimum larger than this.
        // Inset vertically so the window doesn't fill the full taskbar height.
        var verticalInset = (int)(4 * scale);
        var estimatedWidth = (int)((_machineName.Length * 8 + 24) * scale);
        var windowHeight = taskbarHeight - (verticalInset * 2);

        // First pass: set size and a temporary position (far left) so the
        // window is created at the right height, then measure its actual width.
        SetWindowPos(_hwnd, 0, 0, verticalInset, estimatedWidth, windowHeight,
            SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Read the actual window width -- WinUI may have clamped it to a minimum.
        if (!GetWindowRect(_hwnd, out var actualRect)) return;
        var actualWidth = (int)((actualRect.Right - actualRect.Left) * scale);

        // Find the anchor point: TrayNotifyWnd left edge (includes the chevron).
        var trayNotify = FindWindowExW(taskbar, 0, "TrayNotifyWnd", null);
        int xPos;
        if (trayNotify != 0 && GetWindowRect(trayNotify, out var trayRect))
        {
            var anchorLeft = (int)((trayRect.Left - taskbarRect.Left) * scale);
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
        var result = _contextMenu.Show(x, y);
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
            case MenuAction.Exit:
                Close();
                break;
        }
    }
}
