using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace LlamaDeckNode;

/// <summary>Startar och stoppar ggml-rpc-server som gör datorn till en LlamaDeck-nod.</summary>
public sealed class NodeService : IDisposable
{
    public event Action<string>? Log;
    public event Action<bool>? StateChanged;

    public int RpcPort { get; set; } = 50052;
    public int InfoPort { get; set; } = 50053;
    public string? BackendDir { get; set; }

    public string RpcDir { get; private set; } = "";
    public bool IsRunning => _proc is { HasExited: false };

    private Process? _proc;
    private SystemInfo.InfoServer? _info;

    public void Prepare()
    {
        if (!string.IsNullOrWhiteSpace(BackendDir) && Directory.Exists(BackendDir))
        {
            RpcDir = BackendDir;
            Log?.Invoke($"Använder backend: {RpcDir}");
            return;
        }

        RpcDir = Path.Combine(AppPaths.DataDir, "rpc");
        Directory.CreateDirectory(RpcDir);
        if (!File.Exists(Path.Combine(RpcDir, "ggml-rpc-server.exe")))
            ExtractRpc();
    }

    private void ExtractRpc()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("rpc.zip", StringComparison.OrdinalIgnoreCase));
        if (name == null) throw new InvalidOperationException("rpc.zip saknas i exe-filen.");

        using var stream = asm.GetManifestResourceStream(name)!;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var dest = Path.Combine(RpcDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, true);
        }
        Log?.Invoke($"Packade upp RPC-server i {RpcDir}");
    }

    public void Start()
    {
        if (IsRunning) return;
        Prepare();

        var exe = Path.Combine(RpcDir, "ggml-rpc-server.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("Hittar inte ggml-rpc-server.exe", exe);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"--host 0.0.0.0 --port {RpcPort}",
            WorkingDirectory = RpcDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        _proc.Exited += (_, _) =>
        {
            Log?.Invoke("RPC-servern avslutades.");
            StateChanged?.Invoke(false);
        };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        _info = new SystemInfo.InfoServer(InfoPort, () => SystemInfo.Json(RpcPort, Path.GetFileName(RpcDir)));
        _info.Start();

        Log?.Invoke($"Nod igång: RPC 0.0.0.0:{RpcPort} · info 0.0.0.0:{InfoPort}");
        StateChanged?.Invoke(true);
    }

    public void Stop()
    {
        try { _info?.Dispose(); } catch { }
        _info = null;

        try
        {
            if (_proc is { HasExited: false }) _proc.Kill(true);
        }
        catch { }
        _proc?.Dispose();
        _proc = null;

        StateChanged?.Invoke(false);
    }

    public void Dispose() => Stop();
}
