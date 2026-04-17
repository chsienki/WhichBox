#pragma warning disable CA1416 // Platform compatibility
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Derive repo root from this script's location
var scriptDir = AppContext.BaseDirectory;
// Walk up to find WhichBox.slnx
var repoRoot = scriptDir;
while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot, "WhichBox.slnx")))
    repoRoot = Path.GetDirectoryName(repoRoot);
if (repoRoot is null)
{
    // Fallback: assume script is in repo root
    repoRoot = Path.GetDirectoryName(Path.GetFullPath("install.cs")) ?? Environment.CurrentDirectory;
}

var projectPath = Path.Combine(repoRoot, "src", "WhichBox", "WhichBox.csproj");
var installDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "WhichBox");
var rid = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
var platform = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "ARM64" : "x64";

Console.WriteLine($"Building WhichBox ({rid}) ...");
Console.WriteLine($"Project: {projectPath}");
Console.WriteLine($"Output:  {installDir}");

// Publish with AOT. The csproj CopyXamlResourcesForAot target
// handles copying .pri/.xbf to the publish dir automatically.
var build = Process.Start(new ProcessStartInfo
{
    FileName = "dotnet",
    ArgumentList =
    {
        "publish", projectPath,
        "-c", "Release",
        "-r", rid,
        $"-p:Platform={platform}",
        "--self-contained", "true",
        "-o", installDir
    },
    UseShellExecute = false
})!;

build.WaitForExit();
if (build.ExitCode != 0)
{
    Console.Error.WriteLine("Build failed.");
    return 1;
}
Console.WriteLine("Build succeeded.");

// Register for startup via HKCU\...\Run
var exePath = Path.Combine(installDir, "WhichBox.exe");
if (!File.Exists(exePath))
{
    Console.Error.WriteLine($"ERROR: {exePath} not found after publish.");
    return 1;
}

using var runKey = Registry.CurrentUser.OpenSubKey(
    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true)!;
runKey.SetValue("WhichBox", $"\"{exePath}\"");

Console.WriteLine($"Registered startup: {exePath}");

// Kill any existing instance and launch the newly installed one
var existing = Process.GetProcessesByName("WhichBox");
foreach (var p in existing)
{
    p.Kill();
    p.WaitForExit();
}

Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
Console.WriteLine("Done! WhichBox is running and will start automatically on login.");
return 0;
