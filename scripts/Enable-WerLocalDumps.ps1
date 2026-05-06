# Enable-WerLocalDumps.ps1
#
# Enables Windows Error Reporting (WER) LocalDumps for WhichBox.exe so that
# fatal __fastfail crashes inside the WindowsAppRuntime / WinUI 3 input
# subsystem leave a permanent minidump in %LOCALAPPDATA%\WhichBox\Dumps
# instead of being silently cleaned up by WER.
#
# The in-process SetUnhandledExceptionFilter dumper in WhichBox cannot catch
# __fastfail because that path bypasses every user-mode handler by design.
# WER LocalDumps is the supported mechanism. It requires HKLM access, so
# this script needs to be run elevated -- it self-elevates via UAC.
#
# One-time setup. Run again to update settings; run with -Disable to remove.

[CmdletBinding()]
param(
    [switch] $Disable,
    [string] $DumpFolder = '%LOCALAPPDATA%\WhichBox\Dumps',
    [ValidateSet('Mini', 'Full')]
    [string] $DumpType = 'Mini',
    [int] $DumpCount = 5
)

$ErrorActionPreference = 'Stop'
$wbExe = 'WhichBox.exe'
$keyPath = "HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$wbExe"

# Self-elevate
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Re-launching elevated..."
    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    if ($Disable) { $childArgs += '-Disable' }
    $childArgs += @('-DumpFolder', $DumpFolder, '-DumpType', $DumpType, '-DumpCount', $DumpCount)
    Start-Process powershell.exe -Verb RunAs -ArgumentList $childArgs
    return
}

if ($Disable) {
    if (Test-Path $keyPath) {
        Remove-Item $keyPath -Force
        Write-Host "Disabled WER LocalDumps for $wbExe"
    } else {
        Write-Host "WER LocalDumps already disabled for $wbExe"
    }
    Read-Host "Press Enter to close"
    return
}

# DumpType: 1 = MiniDump, 2 = FullDump
$dumpTypeValue = if ($DumpType -eq 'Full') { 2 } else { 1 }

if (-not (Test-Path $keyPath)) {
    New-Item -Path $keyPath -Force | Out-Null
}

# DumpFolder must be REG_EXPAND_SZ so %LOCALAPPDATA% expands per crashing user
[Microsoft.Win32.Registry]::SetValue(
    "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\$wbExe",
    'DumpFolder', $DumpFolder,
    [Microsoft.Win32.RegistryValueKind]::ExpandString)

Set-ItemProperty -Path $keyPath -Name 'DumpType'  -Value $dumpTypeValue -Type DWord
Set-ItemProperty -Path $keyPath -Name 'DumpCount' -Value $DumpCount    -Type DWord

Write-Host ""
Write-Host "Enabled WER LocalDumps for $wbExe :"
Write-Host "  DumpFolder : $DumpFolder"
Write-Host "  DumpType   : $DumpType ($dumpTypeValue)"
Write-Host "  DumpCount  : $DumpCount"
Write-Host ""
Write-Host "Next __fastfail / unhandled-SEH crash will leave a .dmp here."
Write-Host "Verify with:"
Write-Host "  Get-ItemProperty '$keyPath'"
Write-Host ""
Read-Host "Press Enter to close"
