using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace CLCKTY.Core;

public sealed class SoundEngine : ISoundEngine
{
    private static readonly HashSet<int> LeftSideKeys =
    [
        0xC0, 0x31, 0x32, 0x33, 0x34, 0x35,
        0x51, 0x57, 0x45, 0x52, 0x54,
        0x41, 0x53, 0x44, 0x46, 0x47,
        0x5A, 0x58, 0x43, 0x56, 0x42,
        0x09, 0x14, 0xA0, 0xA2, 0x5B
    ];

    private static readonly HashSet<int> RightSideKeys =
    [
        0x36, 0x37, 0x38, 0x39, 0x30, 0xBD, 0xBB,
        0x59, 0x55, 0x49, 0x4F, 0x50, 0xDB, 0xDD, 0xDC,
        0x48, 0x4A, 0x4B, 0x4C, 0xBA, 0xDE, 0x0D,
        0x4E, 0x4D, 0xBC, 0xBE, 0xBF,
        0xA1, 0xA3, 0x5C
    ];

    private readonly ConcurrentQueue<PlaybackRequest> _queue = new();
    private readonly AutoResetEvent _queueSignal = new(false);
    private readonly CancellationTokenSource _shutdownTokenSource = new();
    private readonly object _mixerLock = new();

    private readonly WaveFormat _mixFormat;
    private readonly MixingSampleProvider _mixer;
    private readonly WaveOutEvent _output;
    private readonly Task _workerTask;

    private Dictionary<SoundSlot, CachedSample> _slotSamples = new();
    private SoundEngineConfiguration _configuration = new();
    private int _dequeueWaitMs = 1;
    private double _lastDispatchLatencyMs;
    private bool _disposed;

    public SoundEngine()
    {
        _mixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        _mixer = new MixingSampleProvider(_mixFormat)
        {
            ReadFully = true
        };

        _output = new WaveOutEvent
        {
            DesiredLatency = 8,
            NumberOfBuffers = 2
        };
        _output.Init(_mixer);
        _output.Volume = 0.85f;
        _output.Play();

        _workerTask = Task.Factory.StartNew(ProcessPlaybackQueue, TaskCreationOptions.LongRunning);
    }

    public double LastDispatchLatencyMs => Volatile.Read(ref _lastDispatchLatencyMs);

    public bool IsMuted { get; set; }

    public float MasterVolume
    {
        get => _output.Volume;
        set => _output.Volume = Math.Clamp(value, 0f, 1f);
    }

    public async Task InitializeAsync(AudioProfile profile, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        var loaded = await Task.Run(() =>
        {
            var map = new Dictionary<SoundSlot, CachedSample>();
            foreach (var slot in Enum.GetValues<SoundSlot>())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var asset = profile.ResolveAsset(slot);
                if (asset is null || !File.Exists(asset.FilePath))
                {
                    continue;
                }

                map[slot] = LoadCachedSample(asset.FilePath);
            }

            return map;
        }, cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _slotSamples, loaded);
    }

    public void UpdateConfiguration(SoundEngineConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        MasterVolume = configuration.MasterVolume;
        _dequeueWaitMs = configuration.LatencyMode == LatencyMode.Performance ? 1 : 4;
    }

    public void PlayKeyDown(int virtualKeyCode)
    {
        var cfg = _configuration;
        if (IsMuted || !cfg.SoundsEnabled || !cfg.KeyDownEnabled)
        {
            return;
        }

        Enqueue(SoundSlot.KeyDown, GetPan(virtualKeyCode, cfg.SpatialAudioEnabled), cfg.RandomPitchEnabled, cfg.ToneX, cfg.ToneY);
    }

    public void PlayKeyUp(int virtualKeyCode)
    {
        var cfg = _configuration;
        if (IsMuted || !cfg.SoundsEnabled || !cfg.KeyUpEnabled)
        {
            return;
        }

        Enqueue(SoundSlot.KeyUp, GetPan(virtualKeyCode, cfg.SpatialAudioEnabled), cfg.RandomPitchEnabled, cfg.ToneX, cfg.ToneY);
    }

    public void PlayMouseClick(MouseButtonType button)
    {
        var cfg = _configuration;
        if (IsMuted || !cfg.SoundsEnabled)
        {
            return;
        }

        var slot = button switch
        {
            MouseButtonType.Left when cfg.MouseLeftEnabled => SoundSlot.MouseLeft,
            MouseButtonType.Right when cfg.MouseRightEnabled => SoundSlot.MouseRight,
            MouseButtonType.Middle when cfg.MouseMiddleEnabled => SoundSlot.MouseMiddle,
            _ => (SoundSlot?)null
        };

        if (!slot.HasValue)
        {
            return;
        }

        Enqueue(slot.Value, 0f, cfg.RandomPitchEnabled, cfg.ToneX, cfg.ToneY);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _shutdownTokenSource.Cancel();
        _queueSignal.Set();

        try
        {
            _workerTask.Wait(250);
        }
        catch (AggregateException)
        {
            // Ignore cancellation exceptions during shutdown.
        }

        _output.Stop();
        _output.Dispose();
        _queueSignal.Dispose();
        _shutdownTokenSource.Dispose();

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void Enqueue(SoundSlot slot, float pan, bool randomPitchEnabled, float toneX, float toneY)
    {
        var localSamples = _slotSamples;
        if (!localSamples.TryGetValue(slot, out var sample))
        {
            return;
        }

        var pitch = randomPitchEnabled
            ? 1f + ((float)Random.Shared.NextDouble() - 0.5f) * 0.06f
            : 1f;

        _queue.Enqueue(new PlaybackRequest(sample, pan, pitch, Stopwatch.GetTimestamp(), toneX, toneY));
        _queueSignal.Set();
    }

    private void ProcessPlaybackQueue()
    {
        var token = _shutdownTokenSource.Token;
        while (!token.IsCancellationRequested)
        {
            if (!_queue.TryDequeue(out var request))
            {
                _queueSignal.WaitOne(_dequeueWaitMs);
                continue;
            }

            var provider = new OneShotSampleProvider(
                _mixFormat,
                request.Sample.Samples,
                request.Pan,
                request.Pitch,
                MasterVolume,
                request.ToneX,
                request.ToneY);

            lock (_mixerLock)
            {
                _mixer.AddMixerInput(provider);
            }

            var elapsedTicks = Stopwatch.GetTimestamp() - request.EnqueueTick;
            Volatile.Write(ref _lastDispatchLatencyMs, elapsedTicks * 1000.0 / Stopwatch.Frequency);
        }
    }

    private CachedSample LoadCachedSample(string filePath)
    {
        using var reader = new AudioFileReader(filePath);

        ISampleProvider provider = reader;
        if (provider.WaveFormat.SampleRate != _mixFormat.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, _mixFormat.SampleRate);
        }

        if (provider.WaveFormat.Channels == 1)
        {
            provider = new MonoToStereoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            provider = new StereoToMonoSampleProvider(provider);
            provider = new MonoToStereoSampleProvider(provider);
        }

        var samples = new List<float>(32_000);
        var readBuffer = new float[_mixFormat.SampleRate / 2];
        while (true)
        {
            var read = provider.Read(readBuffer, 0, readBuffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                samples.Add(readBuffer[i]);
            }
        }

        return new CachedSample(samples.ToArray());
    }

    private static float GetPan(int virtualKeyCode, bool spatialEnabled)
    {
        if (!spatialEnabled)
        {
            return 0f;
        }

        if (LeftSideKeys.Contains(virtualKeyCode))
        {
            return -0.35f;
        }

        if (RightSideKeys.Contains(virtualKeyCode))
        {
            return 0.35f;
        }

        return 0f;
    }

    private readonly record struct CachedSample(float[] Samples);

    private readonly record struct PlaybackRequest(
        CachedSample Sample,
        float Pan,
        float Pitch,
        long EnqueueTick,
        float ToneX,
        float ToneY);

    private sealed class OneShotSampleProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private readonly float _pitch;
        private readonly float _toneX;
        private readonly float _toneY;
        private readonly float _gain;
        private readonly float _pan;

        private double _positionFrames;
        private float _lpLeft;
        private float _lpRight;

        public OneShotSampleProvider(
            WaveFormat waveFormat,
            float[] samples,
            float pan,
            float pitch,
            float gain,
            float toneX,
            float toneY)
        {
            WaveFormat = waveFormat;
            _samples = samples;
            _pan = Math.Clamp(pan, -1f, 1f);
            _pitch = Math.Clamp(pitch, 0.9f, 1.1f);
            _gain = Math.Clamp(gain, 0f, 1f);
            _toneX = Math.Clamp(toneX, -1f, 1f);
            _toneY = Math.Clamp(toneY, -1f, 1f);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var outputSamples = 0;
            var framesRequested = count / 2;
            var sourceFrames = _samples.Length / 2;

            for (var frame = 0; frame < framesRequested; frame++)
            {
                if (_positionFrames >= sourceFrames - 1)
                {
                    break;
                }

                var indexFrame = (int)_positionFrames;
                var fraction = (float)(_positionFrames - indexFrame);

                var currentIndex = indexFrame * 2;
                var nextIndex = Math.Min(currentIndex + 2, _samples.Length - 2);

                var left = Lerp(_samples[currentIndex], _samples[nextIndex], fraction);
                var right = Lerp(_samples[currentIndex + 1], _samples[nextIndex + 1], fraction);

                ApplyPan(ref left, ref right, _pan);
                ApplyTone(ref left, ref right);

                buffer[offset + outputSamples++] = left * _gain;
                buffer[offset + outputSamples++] = right * _gain;

                _positionFrames += _pitch;
            }

            return outputSamples;
        }

        private void ApplyTone(ref float left, ref float right)
        {
            var clackBlend = (_toneX + 1f) * 0.5f;
            var lowpassAlpha = 0.12f + (1f - clackBlend) * 0.2f;

            _lpLeft += lowpassAlpha * (left - _lpLeft);
            _lpRight += lowpassAlpha * (right - _lpRight);

            var highLeft = left - _lpLeft;
            var highRight = right - _lpRight;

            left = (_lpLeft * (1f - clackBlend)) + (highLeft * clackBlend);
            right = (_lpRight * (1f - clackBlend)) + (highRight * clackBlend);

            var sharpnessGain = 1f + (_toneY * 0.25f);
            var softness = _toneY < 0f ? 1f + (_toneY * 0.2f) : 1f;

            left *= sharpnessGain * softness;
            right *= sharpnessGain * softness;
        }

        private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

        private static void ApplyPan(ref float left, ref float right, float pan)
        {
            var mono = (left + right) * 0.5f;
            var angle = (pan + 1f) * 0.25f * MathF.PI;
            var leftGain = MathF.Cos(angle);
            var rightGain = MathF.Sin(angle);

            left = mono * leftGain;
            right = mono * rightGain;
        }
    }
}
