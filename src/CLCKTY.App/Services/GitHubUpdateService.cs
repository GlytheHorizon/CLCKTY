using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CLCKTY.App.Services;

public sealed class GitHubUpdateService
{
    private const string DefaultRepositoryOwner = "GlytheHorizon";
    private const string DefaultRepositoryName = "CLCKTY";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HttpClient HttpClient = CreateHttpClient();

    public string CurrentVersionLabel => FormatVersion(GetCurrentVersion());

    public string RepositoryDisplay
    {
        get
        {
            var (owner, name) = GetRepositoryCoordinates();
            return IsConfiguredPlaceholder(owner, name)
                ? "Not configured"
                : $"{owner}/{name}";
        }
    }

    public bool IsRepositoryConfigured
    {
        get
        {
            var (owner, name) = GetRepositoryCoordinates();
            return !IsConfiguredPlaceholder(owner, name);
        }
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        var currentVersionLabel = FormatVersion(currentVersion);
        var (owner, name) = GetRepositoryCoordinates();

        if (IsConfiguredPlaceholder(owner, name))
        {
            return new UpdateCheckResult(
                IsSuccessful: false,
                IsRepositoryConfigured: false,
                UpdateAvailable: false,
                CurrentVersionLabel: currentVersionLabel,
                LatestVersionLabel: null,
                AssetName: null,
                AssetDownloadUrl: null,
                Message: "Updater repository is not configured. Set CLCKTY_GITHUB_OWNER and CLCKTY_GITHUB_REPO env vars or edit GitHubUpdateService defaults.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{name}/releases/latest");
        ApplyTokenIfConfigured(request);

        try
        {
            using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            {
                return new UpdateCheckResult(
                    IsSuccessful: false,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: false,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: null,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "GitHub API denied access. For private repos, set CLCKTY_GITHUB_TOKEN.");
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(
                    IsSuccessful: false,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: false,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: null,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "Repository or releases endpoint not found.");
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GitHubReleaseResponse>(payload, JsonOptions);

            if (release is null)
            {
                return new UpdateCheckResult(
                    IsSuccessful: false,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: false,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: null,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "Could not parse GitHub release response.");
            }

            var releaseTag = string.IsNullOrWhiteSpace(release.TagName)
                ? release.Name
                : release.TagName;

            if (!TryParseVersion(releaseTag, out var latestVersion))
            {
                return new UpdateCheckResult(
                    IsSuccessful: false,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: false,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: releaseTag,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "Latest release tag is not a valid semantic version.");
            }

            var latestVersionLabel = FormatVersion(latestVersion);

            if (latestVersion <= currentVersion)
            {
                return new UpdateCheckResult(
                    IsSuccessful: true,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: false,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: latestVersionLabel,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "You are already on the latest version.");
            }

            var asset = SelectDownloadAsset(release.Assets);
            if (asset is null)
            {
                return new UpdateCheckResult(
                    IsSuccessful: false,
                    IsRepositoryConfigured: true,
                    UpdateAvailable: true,
                    CurrentVersionLabel: currentVersionLabel,
                    LatestVersionLabel: latestVersionLabel,
                    AssetName: null,
                    AssetDownloadUrl: null,
                    Message: "Update found, but no downloadable zip asset was found in the latest release.");
            }

            return new UpdateCheckResult(
                IsSuccessful: true,
                IsRepositoryConfigured: true,
                UpdateAvailable: true,
                CurrentVersionLabel: currentVersionLabel,
                LatestVersionLabel: latestVersionLabel,
                AssetName: asset.Name,
                AssetDownloadUrl: asset.BrowserDownloadUrl,
                Message: $"{latestVersionLabel} detected.");
        }
        catch (OperationCanceledException)
        {
            return new UpdateCheckResult(
                IsSuccessful: false,
                IsRepositoryConfigured: true,
                UpdateAvailable: false,
                CurrentVersionLabel: currentVersionLabel,
                LatestVersionLabel: null,
                AssetName: null,
                AssetDownloadUrl: null,
                Message: "Update scan canceled.");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                IsSuccessful: false,
                IsRepositoryConfigured: true,
                UpdateAvailable: false,
                CurrentVersionLabel: currentVersionLabel,
                LatestVersionLabel: null,
                AssetName: null,
                AssetDownloadUrl: null,
                Message: $"Update scan failed: {ex.Message}");
        }
    }

    public async Task<UpdatePreparationResult> DownloadAndPrepareUpdateAsync(
        UpdateCheckResult checkResult,
        CancellationToken cancellationToken = default)
    {
        if (!checkResult.UpdateAvailable
            || string.IsNullOrWhiteSpace(checkResult.AssetDownloadUrl)
            || string.IsNullOrWhiteSpace(checkResult.AssetName)
            || string.IsNullOrWhiteSpace(checkResult.LatestVersionLabel))
        {
            return new UpdatePreparationResult(
                IsSuccessful: false,
                Package: null,
                Message: "No update package available for download.");
        }

        var updateRoot = Path.Combine(
            Path.GetTempPath(),
            "CLCKTY",
            "updates",
            DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

        Directory.CreateDirectory(updateRoot);

        var packagePath = Path.Combine(updateRoot, checkResult.AssetName);

        using var request = new HttpRequestMessage(HttpMethod.Get, checkResult.AssetDownloadUrl);
        ApplyTokenIfConfigured(request);

        try
        {
            using var response = await HttpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using (var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(packagePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await network.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            if (!packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdatePreparationResult(
                    IsSuccessful: false,
                    Package: null,
                    Message: "Only zip release assets are supported for automatic install.");
            }

            var extractedRoot = Path.Combine(updateRoot, "extracted");
            ZipFile.ExtractToDirectory(packagePath, extractedRoot, true);

            var payloadDirectory = ResolvePayloadDirectory(extractedRoot);
            var package = new PreparedUpdatePackage(
                WorkingDirectory: updateRoot,
                PayloadDirectory: payloadDirectory,
                VersionLabel: checkResult.LatestVersionLabel);

            return new UpdatePreparationResult(
                IsSuccessful: true,
                Package: package,
                Message: $"Downloaded {checkResult.LatestVersionLabel}. Ready to install.");
        }
        catch (OperationCanceledException)
        {
            return new UpdatePreparationResult(
                IsSuccessful: false,
                Package: null,
                Message: "Update download canceled.");
        }
        catch (Exception ex)
        {
            return new UpdatePreparationResult(
                IsSuccessful: false,
                Package: null,
                Message: $"Download failed: {ex.Message}");
        }
    }

    public UpdateApplyResult StartPreparedUpdateAndRestart(PreparedUpdatePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.PayloadDirectory)
            || !Directory.Exists(package.PayloadDirectory))
        {
            return new UpdateApplyResult(
                IsSuccessful: false,
                Message: "Prepared update payload was not found.");
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return new UpdateApplyResult(
                IsSuccessful: false,
                Message: "Could not determine executable path for restart.");
        }

        var installDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var updaterScriptPath = Path.Combine(package.WorkingDirectory, "apply-update.ps1");

        var scriptContent = BuildUpdaterScript(
            pidToWait: Environment.ProcessId,
            payloadDirectory: package.PayloadDirectory,
            installDirectory: installDirectory,
            executablePath: processPath);

        File.WriteAllText(updaterScriptPath, scriptContent);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{updaterScriptPath}\"",
            WorkingDirectory = package.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            Process.Start(startInfo);
            return new UpdateApplyResult(
                IsSuccessful: true,
                Message: "Updater started. Restarting to install update.");
        }
        catch (Exception ex)
        {
            return new UpdateApplyResult(
                IsSuccessful: false,
                Message: $"Failed to start updater: {ex.Message}");
        }
    }

    private static string BuildUpdaterScript(int pidToWait, string payloadDirectory, string installDirectory, string executablePath)
    {
        return $@"
$ErrorActionPreference = 'Stop'
$pidToWait = {pidToWait}
$payload = '{EscapePowerShellString(payloadDirectory)}'
$target = '{EscapePowerShellString(installDirectory)}'
$exe = '{EscapePowerShellString(executablePath)}'

while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) {{
    Start-Sleep -Milliseconds 250
}}

Copy-Item -Path (Join-Path $payload '*') -Destination $target -Recurse -Force
Start-Process -FilePath $exe
";
    }

    private static GitHubAssetResponse? SelectDownloadAsset(IEnumerable<GitHubAssetResponse>? assets)
    {
        return assets?
            .Where(asset =>
                !string.IsNullOrWhiteSpace(asset.Name)
                && !string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)
                && asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.Name.Contains("win", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.Name.Contains("x64", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(asset => asset.UpdatedAt)
            .FirstOrDefault();
    }

    private static string ResolvePayloadDirectory(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot))
        {
            return extractedRoot;
        }

        var executableName = Path.GetFileName(Environment.ProcessPath);
        if (!string.IsNullOrWhiteSpace(executableName)
            && File.Exists(Path.Combine(extractedRoot, executableName)))
        {
            return extractedRoot;
        }

        foreach (var dir in Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (!string.IsNullOrWhiteSpace(executableName)
                && File.Exists(Path.Combine(dir, executableName)))
            {
                return dir;
            }
        }

        var topDirectories = Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.TopDirectoryOnly).ToList();
        var topFiles = Directory.EnumerateFiles(extractedRoot, "*", SearchOption.TopDirectoryOnly).ToList();

        if (topDirectories.Count == 1 && topFiles.Count == 0)
        {
            return topDirectories[0];
        }

        return extractedRoot;
    }

    private static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational, out var parsedInformational))
        {
            return parsedInformational;
        }

        var fileVersion = assembly.GetName().Version;
        if (fileVersion is not null)
        {
            return new Version(
                Math.Max(fileVersion.Major, 0),
                Math.Max(fileVersion.Minor, 0),
                Math.Max(fileVersion.Build, 0));
        }

        return new Version(0, 0, 0);
    }

    private static bool TryParseVersion(string? rawVersion, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var sanitized = rawVersion.Trim();
        if (sanitized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            sanitized = sanitized[1..];
        }

        var delimiterIndex = sanitized.IndexOfAny(['-', '+']);
        if (delimiterIndex >= 0)
        {
            sanitized = sanitized[..delimiterIndex];
        }

        if (Version.TryParse(sanitized, out var parsed) && parsed is not null)
        {
            version = parsed;
            return true;
        }

        return false;
    }

    private static string FormatVersion(Version version)
    {
        var build = version.Build >= 0 ? version.Build : 0;
        return $"v{version.Major}.{version.Minor}.{build}";
    }

    private static (string owner, string name) GetRepositoryCoordinates()
    {
        var owner = Environment.GetEnvironmentVariable("CLCKTY_GITHUB_OWNER") ?? DefaultRepositoryOwner;
        var name = Environment.GetEnvironmentVariable("CLCKTY_GITHUB_REPO") ?? DefaultRepositoryName;

        return (owner.Trim(), name.Trim());
    }

    private static bool IsConfiguredPlaceholder(string owner, string name)
    {
        return string.IsNullOrWhiteSpace(owner)
            || string.IsNullOrWhiteSpace(name)
            || owner.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyTokenIfConfigured(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable("CLCKTY_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CLCKTY", "2.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string EscapePowerShellString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private sealed class GitHubReleaseResponse
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetResponse>? Assets { get; init; }
    }

    private sealed class GitHubAssetResponse
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; init; }
    }
}

public sealed record UpdateCheckResult(
    bool IsSuccessful,
    bool IsRepositoryConfigured,
    bool UpdateAvailable,
    string CurrentVersionLabel,
    string? LatestVersionLabel,
    string? AssetName,
    string? AssetDownloadUrl,
    string Message);

public sealed record PreparedUpdatePackage(
    string WorkingDirectory,
    string PayloadDirectory,
    string VersionLabel);

public sealed record UpdatePreparationResult(
    bool IsSuccessful,
    PreparedUpdatePackage? Package,
    string Message);

public sealed record UpdateApplyResult(
    bool IsSuccessful,
    string Message);
