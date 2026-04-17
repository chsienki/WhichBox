# WhichBox

A small Windows taskbar indicator that displays the current machine name. Helpful when you frequently switch between VMs or remote desktops and need a quick visual cue for which machine you're on.

![WhichBox in the taskbar](docs/screenshot.png)

## Features

- Sits directly in the taskbar, just left of the system tray
- Color-coded background with 12 muted pastel colors -- each machine gets a deterministic default based on its name
- Right-click to pick a different color, toggle startup, or exit
- Remembers your color choice across restarts
- Automatic update checking via GitHub Releases
- Single native executable with fast startup (NativeAOT)

## Install

Download the latest installer from [GitHub Releases](https://github.com/chsienki/WhichBox/releases):

- **WhichBox-x64-Setup.exe** for Intel/AMD machines
- **WhichBox-arm64-Setup.exe** for ARM devices

Run the installer -- no admin rights required. It installs to `%LOCALAPPDATA%\WhichBox` and optionally registers for auto-start on login.

## Usage

- The indicator appears in the taskbar to the left of the system tray
- **Right-click** to open the context menu:
  - Pick a color from the palette
  - Reset to the default color
  - Toggle **Run at Startup**
  - **Update Available** (shown when a newer version is found)
  - Exit

## Uninstall

Use **Add or Remove Programs** in Windows Settings, or run the uninstaller from the Start Menu.

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and Windows 10 19041+.

```powershell
git clone https://github.com/chsienki/WhichBox.git
cd WhichBox

# Debug build
dotnet build src\WhichBox\WhichBox.csproj -c Debug

# AOT publish
dotnet publish src\WhichBox\WhichBox.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained -o publish
```

## License

MIT
