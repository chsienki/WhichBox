#pragma warning disable CA1416 // Platform compatibility
using System.Diagnostics;
using Microsoft.Win32;

var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WhichBox");

// Kill any running instances
var existing = Process.GetProcessesByName("WhichBox");
if (existing.Length > 0)
{
    Console.WriteLine($"Stopping {existing.Length} running instance(s)...");
    foreach (var p in existing)
    {
        p.Kill();
        p.WaitForExit();
    }
}

// Remove startup registry entry
using var runKey = Registry.CurrentUser.OpenSubKey(
    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
if (runKey?.GetValue("WhichBox") is not null)
{
    runKey.DeleteValue("WhichBox");
    Console.WriteLine("Removed startup registry entry.");
}
else
{
    Console.WriteLine("No startup registry entry found.");
}

// Remove install directory
if (Directory.Exists(installDir))
{
    Directory.Delete(installDir, recursive: true);
    Console.WriteLine($"Removed {installDir}");
}
else
{
    Console.WriteLine($"Install directory not found: {installDir}");
}

Console.WriteLine("WhichBox has been uninstalled.");
return 0;
