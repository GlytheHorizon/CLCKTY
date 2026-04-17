using System.Text.Json;
using Microsoft.Win32;
using System.IO;

namespace CLCKTY.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var settingsDirectory = Path.Combine(appData, "CLCKTY");
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectory();

        if (!File.Exists(_settingsPath))
        {
            var defaults = new AppSettings();
            await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        await using var stream = File.OpenRead(_settingsPath);
        var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return settings ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureDirectory();

        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public Task ApplyStartupRegistrationAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Task.CompletedTask;
        }

        using var runKey = Registry.CurrentUser.OpenSubKey(
            "Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            writable: true);

        if (runKey is null)
        {
            return Task.CompletedTask;
        }

        if (enabled)
        {
            runKey.SetValue("CLCKTY", $"\"{executablePath}\"");
        }
        else
        {
            runKey.DeleteValue("CLCKTY", throwOnMissingValue: false);
        }

        return Task.CompletedTask;
    }

    private void EnsureDirectory()
    {
        var directoryPath = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directoryPath);
    }
}
