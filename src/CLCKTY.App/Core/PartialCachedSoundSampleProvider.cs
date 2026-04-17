using NAudio.Wave;

namespace CLCKTY.App.Core;

internal sealed class PartialCachedSoundSampleProvider : ISampleProvider
{
    private readonly CachedSound _cachedSound;
    private readonly int _startIndex;
    private readonly int _endIndex;
    private int _position;

    public PartialCachedSoundSampleProvider(CachedSound cachedSound, int startIndex, int length)
    {
        _cachedSound = cachedSound;
        _startIndex = Math.Clamp(startIndex, 0, _cachedSound.AudioData.Length);
        _endIndex = Math.Clamp(_startIndex + Math.Max(0, length), 0, _cachedSound.AudioData.Length);
        _position = _startIndex;
    }

    public WaveFormat WaveFormat => _cachedSound.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        var available = _endIndex - _position;
        if (available <= 0)
        {
            return 0;
        }

        var toCopy = Math.Min(available, count);
        Array.Copy(_cachedSound.AudioData, _position, buffer, offset, toCopy);
        _position += toCopy;
        return toCopy;
    }
}
