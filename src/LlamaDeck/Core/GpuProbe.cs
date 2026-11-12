using System.Diagnostics;
using System.IO;
using System.Text;

namespace LlamaDeck.Core;

public static class ProcRunner
{
    public static async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            return (-2, "", "timeout");
        }

        return (proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }
}

public sealed record GpuDevice(string Id, string Name, long TotalMiB, long FreeMiB)
{
    public string MemoryText => $"{Fmt.Gb(TotalMiB / 1024d)} totalt · {Fmt.Gb(FreeMiB / 1024d)} ledigt";
}

public static class GpuProbe
{
    private static readonly System.Text.RegularExpressions.Regex DeviceRx = new(
        @"^\s*(?<id>[A-Za-z]+\d+):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB,\s*(?<free>\d+)\s*MiB free\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static async Task<List<GpuDevice>> ListDevicesAsync(string llamaServerExe, CancellationToken ct = default)
    {
        var list = new List<GpuDevice>();
        if (!File.Exists(llamaServerExe)) return list;

        var (code, stdout, stderr) = await ProcRunner.RunAsync(llamaServerExe, new[] { "--list-devices" },
            TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var text = stdout + "\n" + stderr;
        foreach (var raw in text.Split('\n'))
        {
            var m = DeviceRx.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            list.Add(new GpuDevice(
                m.Groups["id"].Value,
                m.Groups["name"].Value.Trim(),
                long.Parse(m.Groups["total"].Value),
                long.Parse(m.Groups["free"].Value)));
        }
        return list;
    }

    public static async Task<(string? CudaVersion, string? DriverVersion)> NvidiaInfoAsync(CancellationToken ct = default)
    {
        var smi = FindSmi();
        if (smi == null) return (null, null);
        var (_, stdout, stderr) = await ProcRunner.RunAsync(smi, Array.Empty<string>(), TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var text = stdout + "\n" + stderr;
        string? cuda = null, driver = null;
        var mc = System.Text.RegularExpressions.Regex.Match(text, @"CUDA Version:\s*([\d.]+)");
        if (mc.Success) cuda = mc.Groups[1].Value;
        var md = System.Text.RegularExpressions.Regex.Match(text, @"Driver Version:\s*([\d.]+)");
        if (md.Success) driver = md.Groups[1].Value;
        return (cuda, driver);
    }

    /// <summary>Använt VRAM per GPU-index (endast NVIDIA).</summary>
    public static async Task<Dictionary<int, long>> NvidiaVramUsedAsync(CancellationToken ct = default)
    {
        var dict = new Dictionary<int, long>();
        var smi = FindSmi();
        if (smi == null) return dict;
        var (_, stdout, _) = await ProcRunner.RunAsync(smi,
            new[] { "--query-gpu=index,memory.used", "--format=csv,noheader,nounits" },
            TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        foreach (var line in stdout.Split('\n'))
        {
            var parts = line.Trim().Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) continue;
            if (int.TryParse(parts[0], out var idx) && long.TryParse(parts[1], out var used))
                dict[idx] = used;
        }
        return dict;
    }

    private static string? FindSmi()
    {
        foreach (var p in new[]
                 {
                     Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                     @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
                 })
        {
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

