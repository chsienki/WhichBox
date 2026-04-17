using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static WhichBox.NativeMethods;

namespace WhichBox;

/// <summary>
/// Encapsulates a native Win32 popup context menu with owner-drawn color swatches.
/// Creates a hidden top-level owner window for TrackPopupMenu and handles
/// WM_MEASUREITEM / WM_DRAWITEM for the color swatch items.
/// </summary>
internal sealed class NativeContextMenu
{
    private const int COLOR_SWATCH_SIZE = 14;
    private const int MENU_ITEM_PADDING = 8;

    private readonly nint _menuOwner;
    private nint _prevWndProc;
    private WndProcDelegate? _wndProcDelegate; // prevent GC

    public nint OwnerHwnd => _menuOwner;

    public NativeContextMenu()
    {
        // Create a hidden but real top-level window to own popup menus.
        // HWND_MESSAGE windows can't own popups reliably (especially over RDP).
        var hInstance = GetModuleHandleW(0);
        _menuOwner = CreateWindowExW(0, "Static", "WhichBoxMenuOwner",
            WS_OVERLAPPED,
            -100, -100, 1, 1, 0, 0, hInstance, 0);

        // Subclass the menu owner to handle WM_MEASUREITEM / WM_DRAWITEM
        _wndProcDelegate = MenuOwnerWndProc;
        _prevWndProc = SetWindowLongPtrW(_menuOwner, GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    /// <summary>
    /// Shows the context menu at the given screen coordinates and returns
    /// the user's selection.
    /// </summary>
    public MenuResult Show(int x, int y, bool startupChecked = false, string? updateVersion = null)
    {
        var hMenu = CreatePopupMenu();
        if (hMenu == 0) return MenuResult.None;

        try
        {
            // Add color items as owner-drawn (IDs 1..N)
            for (int i = 0; i < ColorPalette.Colors.Count; i++)
            {
                AppendMenuW(hMenu, MF_OWNERDRAW, (nuint)(i + 1), null);
            }

            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);

            uint nextId = (uint)ColorPalette.Colors.Count + 1;
            uint resetId = nextId++;
            uint startupId = nextId++;
            uint updateId = nextId++;
            uint exitId = nextId++;

            AppendMenuW(hMenu, MF_STRING, resetId, "Reset to Default");
            AppendMenuW(hMenu, MF_STRING | (startupChecked ? MF_CHECKED : 0), startupId, "Run at Startup");

            if (updateVersion is not null)
            {
                AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
                AppendMenuW(hMenu, MF_STRING, updateId, $"Update Available (v{updateVersion})");
            }

            AppendMenuW(hMenu, MF_SEPARATOR, 0, null);
            AppendMenuW(hMenu, MF_STRING, exitId, "Exit");

            SetForegroundWindow(_menuOwner);

            int cmd = TrackPopupMenuEx(hMenu,
                TPM_RETURNCMD | TPM_BOTTOMALIGN | TPM_RIGHTALIGN,
                x, y, _menuOwner, 0);

            // KB Q135788: post WM_NULL after TrackPopupMenu to ensure menu dismisses
            PostMessageW(_menuOwner, 0 /*WM_NULL*/, 0, 0);

            if (cmd >= 1 && cmd <= ColorPalette.Colors.Count)
                return new MenuResult(MenuAction.SelectColor, ColorPalette.Colors[cmd - 1].Color);
            else if (cmd == (int)resetId)
                return new MenuResult(MenuAction.ResetColor);
            else if (cmd == (int)startupId)
                return new MenuResult(MenuAction.ToggleStartup);
            else if (cmd == (int)updateId)
                return new MenuResult(MenuAction.Update);
            else if (cmd == (int)exitId)
                return new MenuResult(MenuAction.Exit);
            else
                return MenuResult.None;
        }
        finally
        {
            DestroyMenu(hMenu);
        }
    }

    public void Destroy()
    {
        if (_menuOwner != 0)
            DestroyWindow(_menuOwner);
    }

    private unsafe nint MenuOwnerWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_MEASUREITEM && lParam != 0)
        {
            ref var mis = ref Unsafe.AsRef<MEASUREITEMSTRUCT>((void*)lParam);
            if (mis.itemID >= 1 && mis.itemID <= (uint)ColorPalette.Colors.Count)
            {
                mis.itemHeight = (uint)GetSystemMetrics(SM_CYMENU);
                mis.itemWidth = (uint)(COLOR_SWATCH_SIZE + MENU_ITEM_PADDING * 3 + 100);
                return 1;
            }
        }
        else if (msg == WM_DRAWITEM && lParam != 0)
        {
            ref var dis = ref Unsafe.AsRef<DRAWITEMSTRUCT>((void*)lParam);
            if (dis.itemID >= 1 && dis.itemID <= (uint)ColorPalette.Colors.Count)
            {
                var entry = ColorPalette.Colors[(int)dis.itemID - 1];
                var hdc = dis.hDC;
                var rc = dis.rcItem;
                bool selected = (dis.itemState & ODS_SELECTED) != 0;

                // Draw background
                uint bgColor = selected ? 0x00D77800u : 0x00FFFFFFu;
                var bgBrush = CreateSolidBrush(bgColor);
                FillRect(hdc, ref rc, bgBrush);
                DeleteObject(bgBrush);

                // Draw color swatch
                int swatchY = rc.Top + (rc.Bottom - rc.Top - COLOR_SWATCH_SIZE) / 2;
                int swatchX = rc.Left + MENU_ITEM_PADDING;
                var swatchRect = new RECT
                {
                    Left = swatchX,
                    Top = swatchY,
                    Right = swatchX + COLOR_SWATCH_SIZE,
                    Bottom = swatchY + COLOR_SWATCH_SIZE
                };
                var colorBrush = CreateSolidBrush(ToCOLORREF(entry.Color));
                FillRect(hdc, ref swatchRect, colorBrush);
                DeleteObject(colorBrush);

                // Draw text
                SetBkMode(hdc, TRANSPARENT);
                SetTextColor(hdc, selected ? 0x00FFFFFFu : 0x00000000u);
                var textRect = new RECT
                {
                    Left = swatchX + COLOR_SWATCH_SIZE + MENU_ITEM_PADDING,
                    Top = rc.Top,
                    Right = rc.Right - MENU_ITEM_PADDING,
                    Bottom = rc.Bottom
                };
                DrawTextW(hdc, entry.Name, entry.Name.Length, ref textRect,
                    DT_LEFT | DT_VCENTER | DT_SINGLELINE);

                return 1;
            }
        }

        return CallWindowProcW(_prevWndProc, hWnd, msg, wParam, lParam);
    }
}

internal enum MenuAction { None, SelectColor, ResetColor, ToggleStartup, Update, Exit }

internal readonly record struct MenuResult(MenuAction Action, Windows.UI.Color? Color = null)
{
    public static MenuResult None => new(MenuAction.None);
}
