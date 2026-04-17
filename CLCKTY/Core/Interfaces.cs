namespace CLCKTY.Core;

public interface IKeyboardHookService : IDisposable
{
    event EventHandler<KeyboardInputEventArgs>? InputReceived;

    void Start();

    void Stop();
}

public interface IMouseHookService : IDisposable
{
    event EventHandler<MouseInputEventArgs>? ClickReceived;

    void Start();

    void Stop();
}

public interface ISoundEngine : IDisposable
{
    double LastDispatchLatencyMs { get; }

    bool IsMuted { get; set; }

    float MasterVolume { get; set; }

    Task InitializeAsync(AudioProfile profile, CancellationToken cancellationToken = default);

    void UpdateConfiguration(SoundEngineConfiguration configuration);

    void PlayKeyDown(int virtualKeyCode);

    void PlayKeyUp(int virtualKeyCode);

    void PlayMouseClick(MouseButtonType button);
}

public interface IAudioProfileManager
{
    Task<IReadOnlyList<AudioProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default);

    Task<AudioProfile?> GetProfileByIdAsync(string profileId, CancellationToken cancellationToken = default);

    Task<AudioProfile> ImportSoundPackAsync(string folderPath, CancellationToken cancellationToken = default);

    Task<AudioProfile> RecordClipAsync(SoundSlot slot, CancellationToken cancellationToken = default);

    Task<bool> DeleteAssetAsync(string profileId, string assetId, CancellationToken cancellationToken = default);
}
