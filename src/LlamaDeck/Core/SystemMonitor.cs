using System.Diagnostics;

namespace LlamaDeck.Core;

public sealed record SystemSample(
    double CpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double ProcCpuPercent,
    double ProcRamMb,
    double ProcRamPrivateMb);

/// <summary>Mäter CPU, RAM och serverns resursanvändning en gång i sekunden.</summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly object _sync = new();
    private Process? _proc;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private TimeSpan _prevProcTime;
    private DateTime _prevAt = DateTime.UtcNow;
    private bool _primed;

    public event Action<SystemSample>? Sampled;

    public SystemMonitor()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start()
    {
        lock (_sync)
        {
            var (idle, kernel, user) = Native.GetCpuTimes();
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _prevAt = DateTime.UtcNow;
            _primed = true;
        }
        _timer.Change(500, 1000);
    }

    public void Stop() => _timer.Change(Timeout.Infinite, Timeout.Infinite);

    public void SetProcess(Process? proc)
    {
        lock (_sync)
        {
            _proc = proc;
            _prevProcTime = TimeSpan.Zero;
            try
            {
                if (proc is { HasExited: false }) _prevProcTime = proc.TotalProcessorTime;
            }
            catch
            {
                // ignorera
            }
        }
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.UtcNow;
            var (idle, kernel, user) = Native.GetCpuTimes();
            double cpu = 0;
            lock (_sync)
            {
                var idleDelta = (double)(idle - _prevIdle);
                var totalDelta = (double)((kernel - _prevKernel) + (user - _prevUser));
                if (_primed && totalDelta > 0) cpu = Math.Clamp((1.0 - idleDelta / totalDelta) * 100.0, 0, 100);
                _prevIdle = idle;
                _prevKernel = kernel;
                _prevUser = user;
            }

            var (totalGb, _, usedGb) = Native.GetRam();

            double procCpu = 0, ws = 0, priv = 0;
            Process? p;
            TimeSpan pt = TimeSpan.Zero;
            lock (_sync) p = _proc;

            if (p != null)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        pt = p.TotalProcessorTime;
                        ws = p.WorkingSet64 / 1048576d;
                        priv = p.PrivateMemorySize64 / 1048576d;
                        var dt = (now - _prevAt).TotalSeconds;
                        if (dt > 0.2 && _prevProcTime > TimeSpan.Zero)
                        {
                            var used = (pt - _prevProcTime).TotalSeconds;
                            procCpu = Math.Clamp(used / (dt * Environment.ProcessorCount) * 100.0, 0, 100 * Environment.ProcessorCount);
                        }
                    }
                }
                catch
                {
                    // processen kan ha avslutats
                }
            }

            lock (_sync)
            {
                _prevProcTime = pt;
                _prevAt = now;
            }

            Sampled?.Invoke(new SystemSample(cpu, usedGb, totalGb, procCpu, ws, priv));
        }
        catch
        {
            // hoppa över ett prov
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
    }
}
