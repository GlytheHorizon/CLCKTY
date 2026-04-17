namespace CLCKTY.App.Core;

public interface ISoundEngine : IDisposable
{
    bool IsEnabled { get; set; }

    float MasterVolume { get; set; }

    // Legacy alias of keyboard active profile.
    string ActiveProfileId { get; }

    // Legacy alias of keyboard profile list.
    IReadOnlyList<SoundProfileDescriptor> Profiles { get; }

    string GetActiveProfileId(bool isMouseProfile);

    IReadOnlyList<SoundProfileDescriptor> GetProfiles(bool isMouseProfile);

    // Legacy alias for keyboard clip options.
    IReadOnlyList<SoundClipDescriptor> GetClipOptions();

    IReadOnlyList<SoundClipDescriptor> GetClipOptions(bool isMouseProfile);

    IReadOnlyList<InputMappingDescriptor> GetMappings();

    // Legacy alias that sets keyboard active profile.
    void SetActiveProfile(string profileId);

    void SetActiveProfile(string profileId, bool isMouseProfile);

    void SetKeyMapping(int virtualKey, string? clipId, KeyEventTrigger trigger = KeyEventTrigger.Down);

    string? GetKeyMapping(int virtualKey, KeyEventTrigger trigger = KeyEventTrigger.Down);

    // Legacy alias that removes from keyboard active profile.
    bool RemoveClipFromActiveProfile(string clipId);

    bool RemoveClipFromActiveProfile(string clipId, bool isMouseProfile);

    bool RemoveImportedProfile(string profileId);

    void ClearMappings();

    string GetPlaybackClipDisplayName(int inputCode, KeyEventTrigger trigger = KeyEventTrigger.Down);

    void PlayForKey(int virtualKey);
    void StartHoldForKey(int virtualKey);
    void ReleaseForKey(int virtualKey);

    // Legacy alias that imports to keyboard profile category.
    Task<string?> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default);

    Task<string?> ImportSoundPackAsync(string folderPath, bool isMouseProfile, CancellationToken cancellationToken = default);

    Task<string?> ImportAudioClipAsync(string filePath, CancellationToken cancellationToken = default);

    Task<string?> ImportAudioClipAsync(string filePath, bool isMouseProfile, CancellationToken cancellationToken = default);
}
