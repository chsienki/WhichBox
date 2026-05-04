using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhichBox;

/// <summary>
/// Checks for new releases on GitHub and downloads the installer.
/// </summary>
internal sealed class UpdateChecker
{
    private static readonly HttpClient s_http = new()
    {
        DefaultRequestHeaders =
        {
            { "User-Agent", "WhichBox" },
            { "Accept", "application/vnd.github+json" }
        }
    };

    private const string ReleasesApiUrl = "https://api.github.com/repos/chsienki/WhichBox/releases/latest";

    /// <summary>
    /// The latest version available, or null if no update or check hasn't run.
    /// </summary>
    public string? LatestVersion { get; private set; }

    /// <summary>
    /// The download URL for the installer matching the current architecture.
    /// </summary>
    public string? InstallerUrl { get; private set; }

    /// <summary>
    /// True if a newer version is available.
    /// </summary>
    public bool IsUpdateAvailable => LatestVersion is not null;

    /// <summary>
    /// Raised on the calling SynchronizationContext when a new version is found.
    /// </summary>
    public event Action? UpdateFound;

    /// <summary>
    /// Checks GitHub for a newer release. Safe to call from any thread.
    /// </summary>
    public async Task CheckAsync()
    {
        try
        {
            var currentVersion = GetCurrentVersion();
            if (currentVersion is null) return;

            var json = await s_http.GetFromJsonAsync(ReleasesApiUrl, UpdateJsonContext.Default.GitHubRelease);
            if (json?.TagName is null) return;

            var remoteVersion = json.TagName.TrimStart('v');
            if (!Version.TryParse(remoteVersion, out var remote)) return;
            if (!Version.TryParse(currentVersion, out var current)) return;

            if (remote <= current) return;

            // Find the installer asset for our architecture
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            var expectedName = $"WhichBox-{arch}-Setup.exe";
            var asset = json.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, expectedName, StringComparison.OrdinalIgnoreCase));

            if (asset?.BrowserDownloadUrl is null) return;

            LatestVersion = remoteVersion;
            InstallerUrl = asset.BrowserDownloadUrl;
            UpdateFound?.Invoke();
        }
        catch (Exception ex)
        {
            // Network errors, rate limits, etc. -- silently ignore for the user
            // but log so we can diagnose if updates are persistently failing.
            Logger.Warn($"UpdateChecker.CheckAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads the installer to a temp file and runs it.
    /// </summary>
    public async Task DownloadAndInstallAsync()
    {
        if (InstallerUrl is null) return;

        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"WhichBox-Setup-{LatestVersion}.exe");
            using (var response = await s_http.GetAsync(InstallerUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempPath);
                await response.Content.CopyToAsync(fs);
            }

            // Run the installer silently -- it will kill the running instance
            Process.Start(new ProcessStartInfo
            {
                FileName = tempPath,
                Arguments = "/SILENT",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            // Best effort -- if download fails, user can manually download
            Logger.Warn($"UpdateChecker.DownloadAndInstallAsync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? GetCurrentVersion()
    {
        var attr = typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        var version = attr?.InformationalVersion;
        // Strip +hash suffix if present
        if (version is not null)
        {
            var plusIndex = version.IndexOf('+');
            if (plusIndex >= 0) version = version[..plusIndex];
        }
        return version;
    }
}

// Minimal GitHub API models for source-generated JSON
internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string? TagName { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
