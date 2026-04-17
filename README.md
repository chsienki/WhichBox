# WhichBox

A small Windows taskbar indicator that displays the current machine name. Designed for people who frequently switch between VMs or remote desktops and need a quick visual cue to know which machine they're on.

![WhichBox sits in the taskbar, just left of the system tray](docs/concept.png)

## Features

- **Taskbar-native**: Parents itself directly into the Windows taskbar using the [Deskband11](https://github.com/search?q=deskband11) technique (SetParent into Shell_TrayWnd)
- **Feathered edges**: Composition opacity mask with horizontal + vertical gradients for smooth rounded-rectangle edges
- **Color-coded**: 12 muted pastel colors with a deterministic default based on machine name hash -- right-click to pick a different color
- **Transparent background**: Uses WinUIEx's TransparentTintBackdrop so only the colored label is visible
- **NativeAOT**: Publishes as a single native executable (~15MB) with fast startup
- **Startup registration**: Installs itself to run on login via HKCU Run key

## Requirements

- Windows 10 19041+ or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (for building)

## Quick Start

### Build, install, and run

```powershell
cd path\to\WhichBox
dotnet run install.cs
```

This will:
1. Publish a NativeAOT Release build
2. Copy it to `%LOCALAPPDATA%\WhichBox\`
3. Register it to start on login (HKCU Run key)
4. Kill any previous instance and launch the new one

### Debug build

```powershell
dotnet build src\WhichBox\WhichBox.csproj -c Debug
src\WhichBox\bin\Debug\net10.0-windows10.0.26100.0\WhichBox.exe
```

## Usage

- The indicator appears in the taskbar, to the left of the system tray
- **Right-click** to open the context menu:
  - Pick a color from the palette
  - Reset to the default (hash-based) color
  - Exit the app
- Color choice is persisted to `%LOCALAPPDATA%\WhichBox\settings.json`

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

## Uninstall

1. Right-click the indicator and choose **Exit**
2. Remove the startup entry:
   ```powershell
   Remove-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "WhichBox"
   ```
3. Delete the install directory:
   ```powershell
   Remove-Item -Recurse "$env:LOCALAPPDATA\WhichBox"
   ```

## License

MIT
