using System.Runtime.InteropServices;

namespace WhichBox;

/// <summary>
/// Win32 P/Invoke declarations, structs, and constants used throughout the app.
/// </summary>
internal static partial class NativeMethods
{
    // Window management
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint FindWindowW(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint FindWindowExW(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int GetWindowLongW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetWindowLongW(nint hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetParent(nint hWndChild, nint hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    // DPI_AWARENESS_CONTEXT values (pseudo-handle constants)
    internal static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int X, int Y, int nWidth, int nHeight,
        nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hWnd, uint Msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint CallWindowProcW(nint lpPrevWndFunc, nint hWnd, uint Msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial nint GetParent(nint hWnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessageW(string lpString);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetModuleHandleW(nint lpModuleName);

    // Menu
    [LibraryImport("user32.dll")]
    internal static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [LibraryImport("user32.dll")]
    internal static partial int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyMenu(nint hMenu);

    // GDI drawing
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint crColor);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint hObject);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint hDC, ref RECT lprc, nint hbr);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(nint hdc, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(nint hdc, uint color);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawTextW(nint hdc, string lpchText, int cchText, ref RECT lprc, uint format);

    // Delegate for window procedure subclassing
    internal delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    // Structs
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEASUREITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemWidth;
        public uint itemHeight;
        public nint itemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DRAWITEMSTRUCT
    {
        public uint CtlType;
        public uint CtlID;
        public uint itemID;
        public uint itemAction;
        public uint itemState;
        public nint hwndItem;
        public nint hDC;
        public RECT rcItem;
        public nint itemData;
    }

    // Window style constants
    internal const int GWL_STYLE = -16;
    internal const int GWLP_WNDPROC = -4;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_CAPTION = 0x00C00000;
    internal const int WS_SYSMENU = 0x00080000;
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_MINIMIZEBOX = 0x00020000;
    internal const int WS_MAXIMIZEBOX = 0x00010000;
    internal const uint WS_OVERLAPPED = 0x00000000;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    // Menu constants
    internal const uint MF_STRING = 0x0000;
    internal const uint MF_SEPARATOR = 0x0800;
    internal const uint MF_OWNERDRAW = 0x0100;
    internal const uint MF_CHECKED = 0x0008;
    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_BOTTOMALIGN = 0x0020;
    internal const uint TPM_RIGHTALIGN = 0x0008;

    // Message constants
    internal const uint WM_MEASUREITEM = 0x002C;
    internal const uint WM_DRAWITEM = 0x002B;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_WTSSESSION_CHANGE = 0x02B1;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_NCDESTROY = 0x0082;

    // WTS session change reasons
    internal const int WTS_CONSOLE_CONNECT = 0x1;
    internal const int WTS_CONSOLE_DISCONNECT = 0x2;
    internal const int WTS_REMOTE_CONNECT = 0x3;
    internal const int WTS_REMOTE_DISCONNECT = 0x4;
    internal const int WTS_SESSION_LOGON = 0x5;
    internal const int WTS_SESSION_LOGOFF = 0x6;
    internal const int WTS_SESSION_LOCK = 0x7;
    internal const int WTS_SESSION_UNLOCK = 0x8;
    internal const int WTS_SESSION_REMOTE_CONTROL = 0x9;
    internal const int WTS_SESSION_CREATE = 0xA;
    internal const int WTS_SESSION_TERMINATE = 0xB;

    internal static string WtsReasonName(int reason) => reason switch
    {
        WTS_CONSOLE_CONNECT => "CONSOLE_CONNECT",
        WTS_CONSOLE_DISCONNECT => "CONSOLE_DISCONNECT",
        WTS_REMOTE_CONNECT => "REMOTE_CONNECT",
        WTS_REMOTE_DISCONNECT => "REMOTE_DISCONNECT",
        WTS_SESSION_LOGON => "SESSION_LOGON",
        WTS_SESSION_LOGOFF => "SESSION_LOGOFF",
        WTS_SESSION_LOCK => "SESSION_LOCK",
        WTS_SESSION_UNLOCK => "SESSION_UNLOCK",
        WTS_SESSION_REMOTE_CONTROL => "SESSION_REMOTE_CONTROL",
        WTS_SESSION_CREATE => "SESSION_CREATE",
        WTS_SESSION_TERMINATE => "SESSION_TERMINATE",
        _ => $"UNKNOWN(0x{reason:X})"
    };

    internal const int NOTIFY_FOR_THIS_SESSION = 0;

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSRegisterSessionNotification(nint hWnd, int dwFlags);

    [DllImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WTSUnRegisterSessionNotification(nint hWnd);

    // Drawing constants
    internal const uint ODS_SELECTED = 0x0001;
    internal const int TRANSPARENT = 1;
    internal const uint DT_LEFT = 0x0000;
    internal const uint DT_VCENTER = 0x0004;
    internal const uint DT_SINGLELINE = 0x0020;
    internal const int SM_CXMENUCHECK = 71;
    internal const int SM_CYMENU = 15;
    internal const int SM_REMOTESESSION = 0x1000;

    internal static uint ToCOLORREF(Windows.UI.Color c) =>
        (uint)(c.R | (c.G << 8) | (c.B << 16));
}
