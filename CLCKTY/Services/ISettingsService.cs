namespace CLCKTY.Services;

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    Task ApplyStartupRegistrationAsync(bool enabled, CancellationToken cancellationToken = default);
}
