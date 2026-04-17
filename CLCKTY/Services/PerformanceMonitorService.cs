using System.Diagnostics;

namespace CLCKTY.Services;

public sealed class PerformanceMonitorService : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly Process _process;
    private TimeSpan _lastTotalProcessorTime;
    private DateTime _lastSampleUtc;

    public PerformanceMonitorService()
    {
        _process = Process.GetCurrentProcess();
        _lastTotalProcessorTime = _process.TotalProcessorTime;
        _lastSampleUtc = DateTime.UtcNow;
        _timer = new System.Threading.Timer(Sample, null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<double>? CpuSampled;

    public void Start()
    {
        _lastTotalProcessorTime = _process.TotalProcessorTime;
        _lastSampleUtc = DateTime.UtcNow;
        _timer.Change(1000, 1000);
    }

    public void Stop()
    {
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _process.Dispose();
        GC.SuppressFinalize(this);
    }

    private void Sample(object? _)
    {
        var now = DateTime.UtcNow;
        var total = _process.TotalProcessorTime;

        var cpuDeltaMs = (total - _lastTotalProcessorTime).TotalMilliseconds;
        var timeDeltaMs = (now - _lastSampleUtc).TotalMilliseconds;
        if (timeDeltaMs <= 0)
        {
            return;
        }

        var cpuUsage = (cpuDeltaMs / (timeDeltaMs * Environment.ProcessorCount)) * 100.0;
        cpuUsage = Math.Clamp(cpuUsage, 0.0, 100.0);

        _lastTotalProcessorTime = total;
        _lastSampleUtc = now;

        CpuSampled?.Invoke(this, cpuUsage);
    }
}
