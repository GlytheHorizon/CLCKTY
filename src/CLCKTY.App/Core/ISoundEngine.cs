namespace CLCKTY.App.Core;

public interface ISoundEngine : IDisposable
{
    bool IsEnabled { get; set; }

    float MasterVolume { get; set; }

    string ActiveProfileId { get; }

    IReadOnlyList<SoundProfileDescriptor> Profiles { get; }

    IReadOnlyList<SoundClipDescriptor> GetClipOptions();

    IReadOnlyList<InputMappingDescriptor> GetMappings();

    void SetActiveProfile(string profileId);

    void SetKeyMapping(int virtualKey, string? clipId, KeyEventTrigger trigger = KeyEventTrigger.Down);

    string? GetKeyMapping(int virtualKey, KeyEventTrigger trigger = KeyEventTrigger.Down);

    void ClearMappings();

    void PlayForKey(int virtualKey);
    void StartHoldForKey(int virtualKey);
    void ReleaseForKey(int virtualKey);

    Task<string?> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default);

    Task<string?> ImportAudioClipAsync(string filePath, CancellationToken cancellationToken = default);
}
