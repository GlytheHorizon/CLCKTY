using NAudio.Wave;

namespace CLCKTY.App.Core;

internal sealed class CachedSound
{
    public CachedSound(WaveFormat waveFormat, float[] audioData)
    {
        WaveFormat = waveFormat;
        AudioData = audioData;
    }

    public WaveFormat WaveFormat { get; }

    public float[] AudioData { get; }
}
