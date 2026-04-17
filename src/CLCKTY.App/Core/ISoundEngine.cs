namespace CLCKTY.App.Core;

public interface ISoundEngine : IDisposable
{
    bool IsEnabled { get; set; }

    float MasterVolume { get; set; }

    string ActiveProfileId { get; }

    IReadOnlyList<SoundProfileDescriptor> Profiles { get; }

    IReadOnlyList<SoundClipDescriptor> GetClipOptions();

    void SetActiveProfile(string profileId);

    void SetKeyMapping(int virtualKey, string? clipId);

    string? GetKeyMapping(int virtualKey);

    void ClearMappings();

    void PlayForKey(int virtualKey);
    void StartHoldForKey(int virtualKey);
    void ReleaseForKey(int virtualKey);

    Task<string?> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default);
}
