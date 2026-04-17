# WhichBox -- Copilot Instructions

## Project Overview

WhichBox is a WinUI 3 (Windows App SDK) application that displays the computer name in the Windows taskbar. It parents itself directly into the taskbar using the Deskband11 technique (SetParent into Shell_TrayWnd). The app is unpackaged (no MSIX), uses NativeAOT publishing, and is designed to be a lightweight always-on indicator for VM/RDP users.

## Architecture & Key Decisions

### Why WinUI 3?

We tried multiple frameworks before settling on WinUI 3:
- **WPF**: Cannot survive SetParent into the taskbar -- DirectX rendering breaks when reparented cross-process.
- **WinForms**: Attempted but abandoned.
- **WinUI 3**: Works because the Windows compositor handles cross-process parenting correctly (same approach as Deskband11).

### Unpackaged WinUI 3

The app runs as an unpackaged WinUI 3 app:
- `WindowsPackageType=None` in the csproj
- `DISABLE_XAML_GENERATED_MAIN` defined, with a custom `Program.cs` that calls `ComWrappersSupport.InitializeComWrappers()` before `Application.Start()`
- No MSIX packaging, no app identity

### Taskbar Parenting (MoveToTaskbar)

The Deskband11 technique:
1. Change window style: remove `WS_POPUP` and all chrome bits (`WS_CAPTION`, `WS_SYSMENU`, `WS_THICKFRAME`, `WS_MINIMIZEBOX`, `WS_MAXIMIZEBOX`), add `WS_CHILD`
2. `SetParent(hwnd, Shell_TrayWnd)`
3. Position to the left of `TrayNotifyWnd`

**DPI handling is critical**: After `SetParent`, the child window may inherit a different DPI awareness context, causing `GetWindowRect` to return logical (virtualized) coordinates on some machines but physical on others. The fix is to call `SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` before any `GetWindowRect`/`SetWindowPos` calls in `PositionInTaskbar`, ensuring coordinates are always physical pixels. Hardcoded constants (vertical inset, per-character width estimates, gaps) must still be scaled by `GetDpiForWindow(taskbar) / 96.0` since they're defined in logical units. **Do NOT simply multiply all `GetWindowRect` results by the DPI scale factor** -- that double-scales on machines where the coords are already physical.

**WinUI minimum window size**: WinUI enforces a minimum of ~129 logical pixels width. We use a two-pass sizing approach: request a small size, measure the actual clamped width, then position based on the actual width.

**Vertical inset**: 4 * scale pixels on top and bottom so the indicator doesn't fill the full taskbar height.

**Resilience to DPI/display changes**: The main HWND is subclassed to catch `WM_DPICHANGED`, `WM_DISPLAYCHANGE`, and the registered `TaskbarCreated` shell message. On DPI/display changes, the window repositions within its current parent. On `TaskbarCreated` (explorer recreated the taskbar), it re-parents entirely. Positioning logic is split: `MoveToTaskbar()` handles initial parenting + position, `RepositionInTaskbar()` just recalculates position and verifies the parent is still valid.

### Transparency

- `SetLayeredWindowAttributes` does NOT work with WinUI 3 (it uses DirectComposition, not GDI)
- `DwmExtendFrameIntoClientArea` doesn't work for taskbar child windows
- **WinUIEx's `TransparentTintBackdrop`** is what works. Must be set BEFORE `SetParent`. Re-applying `SystemBackdrop` after `SetParent` breaks rendering entirely.

### Composition Opacity Mask (CompositionMaskHelper)

Creates feathered/rounded edges using composition API:
- Two `CompositionLinearGradientBrush` (horizontal + vertical), each fading from transparent to white at the edges
- Combined via `CompositionMaskBrush` -- but **MaskBrush cannot be nested** (Source and Mask must be primitive brushes)
- Workaround: render the H+V mask combination to an intermediate `SpriteVisual`, capture via `CompositionVisualSurface`, then use as the mask for the final content
- **`VisualSurface` cannot capture from `IsVisible=false` visuals** -- must hide the CONTAINER element, not the source visual itself (following DevWinUI's pattern)
- `SetupCompositionMask` must run in the `Loaded` handler, not the constructor (visual tree not ready before that)
- Gradient stops use relative mapping (0-1) with proportional ratios computed from actual pixel dimensions so all edges fade the same physical distance (currently 24px)

### Context Menu (NativeContextMenu)

WinUI's `MenuFlyout` positioning is broken after `SetParent`, so we use native Win32:
- `TrackPopupMenu` with a real offscreen top-level window as owner (not `HWND_MESSAGE` -- those don't work reliably over RDP)
- Must use `RightTapped` event (not `PointerPressed`) to avoid mouse-up dismissing the popup
- Must defer via `DispatcherQueue.TryEnqueue` so it runs after WinUI finishes processing the pointer event chain
- Owner-drawn items: subclass the owner HWND with `SetWindowLongPtrW` for `WM_MEASUREITEM`/`WM_DRAWITEM`
- Must call `SetForegroundWindow` before `TrackPopupMenu` + `PostMessage(WM_NULL)` after (KB Q135788)

### NativeAOT Publishing

- AOT publish omits `.pri` (XAML resources) and `.xbf` files from the publish output
- **Fix**: `CopyXamlResourcesForAot` MSBuild target in the csproj (from Ghostty/wintty project) copies them from `$(OutputPath)` to `$(PublishDir)`
- JSON serialization uses source-generated `JsonSerializerContext` (`SettingsJsonContext`) to avoid reflection
- The `WndProcDelegate` must be stored as a field to prevent GC collection of the delegate

### install.cs (C# File-Based App)

A .NET 10 file-based application (`dotnet run install.cs`) that:
1. Publishes the AOT build with `dotnet publish`
2. Copies output to `%LOCALAPPDATA%\WhichBox\`
3. Registers for startup via `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`
4. Kills any existing WhichBox process and launches the new one

**Important**: Cannot use `RedirectStandardOutput` with `dotnet publish` -- the progress UI fills the pipe buffer causing a deadlock. Use `UseShellExecute = false` without output redirection.

## Project Structure

```
WhichBox/
  WhichBox.slnx              # Solution file
  install.cs                  # C# file-based app: build + install + startup
  src/WhichBox/
    Program.cs                # Custom Main (WinRT ComWrappersSupport init)
    App.xaml(.cs)             # WinUI 3 Application boilerplate
    MainWindow.xaml(.cs)      # Window setup, taskbar parenting, event wiring
    NativeMethods.cs          # All Win32 P/Invoke declarations and constants
    NativeContextMenu.cs      # Owner-drawn popup menu with color swatches
    CompositionMaskHelper.cs  # Composition opacity mask for feathered edges
    ColorPalette.cs           # 12 muted colors, hash-based default, contrast calc
    Settings.cs               # JSON persistence (AOT-safe with source generators)
    WhichBox.csproj           # Project file with AOT + XAML resource workaround
```

## File Guide

| File | Purpose |
|------|---------|
| `Program.cs` | Custom Main entry point with WinRT ComWrappersSupport init |
| `App.xaml(.cs)` | Standard WinUI 3 Application boilerplate |
| `MainWindow.xaml(.cs)` | Window setup, taskbar parenting via MoveToTaskbar(), color application, event wiring |
| `NativeMethods.cs` | All Win32 P/Invoke declarations, structs, constants. Use `using static WhichBox.NativeMethods;` |
| `NativeContextMenu.cs` | Encapsulates popup menu creation, display, owner-drawn painting. Returns `MenuResult` |
| `CompositionMaskHelper.cs` | Static `Apply()` method sets up the composition opacity mask. Self-contained with size tracking |
| `ColorPalette.cs` | 12 muted pastel colors, deterministic hash-based default, contrast foreground calculation |
| `Settings.cs` | JSON persistence with `SettingsJsonContext` (source-generated for AOT). Stores chosen color |
| `WhichBox.csproj` | Project config: AOT, unpackaged WinUI 3, `CopyXamlResourcesForAot` target |
| `install.cs` | File-based app for build + install + startup registration |

## Build & Test

```powershell
# Debug build and run
dotnet build src\WhichBox\WhichBox.csproj -c Debug
src\WhichBox\bin\Debug\net10.0-windows10.0.26100.0\WhichBox.exe

# Full AOT install (build + install + startup + launch)
dotnet run install.cs
```

## Dependencies

- **Microsoft.WindowsAppSDK 1.8** -- WinUI 3 framework
- **Microsoft.Windows.SDK.BuildTools 10.0.26100** -- Windows SDK build tools
- **WinUIEx 2.9.0** -- TransparentTintBackdrop for window transparency

## Common Pitfalls

1. **Transparency breaks after SetParent**: SystemBackdrop must be set BEFORE calling SetParent. Don't re-apply it after.
2. **CompositionMaskBrush nesting**: You cannot use a MaskBrush as the Source or Mask of another MaskBrush. Use an intermediate VisualSurface.
3. **VisualSurface + IsVisible**: VisualSurface cannot capture from a visual with IsVisible=false. Hide the container, not the source.
4. **AOT + XAML resources**: The .pri and .xbf files must be copied to the publish directory. The CopyXamlResourcesForAot target handles this.
5. **Menu popup over RDP**: HWND_MESSAGE windows don't work as popup owners over RDP. Use a real (offscreen) top-level window.
6. **DPI after SetParent**: Do NOT blindly scale `GetWindowRect` results -- the DPI awareness context varies by machine. Instead, wrap positioning code in `SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)` so all APIs use consistent physical-pixel coordinates. Only scale hardcoded constants (insets, gaps).
7. **WndProcDelegate GC**: Store the delegate in a field so it isn't garbage collected while the native subclass is active.
8. **dotnet publish pipe deadlock**: Don't redirect stdout when running dotnet publish -- the progress UI fills the buffer.
