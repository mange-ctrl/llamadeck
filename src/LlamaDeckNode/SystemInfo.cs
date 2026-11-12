using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace LlamaDeckNode;

/// <summary>Systeminformation som LlamaDeck frågar noden om (port 50053).</summary>
public static class SystemInfo
{
    public static string MachineName => Environment.MachineName;

    public static double RamGb
    {
        get
        {
            try
            {
                var bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if (bytes > 0) return Math.Round(bytes / 1073741824d, 1);
            }
            catch { }
            return 0;
        }
    }

    public static string OsName => $"Windows {Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

    public static string Json(int rpcPort, string backend)
    {
        var gpus = Gpus().Select(g => new { name = g.Name, vramMb = g.VramMb });
        var payload = new
        {
            name = MachineName,
            os = OsName,
            ramGb = RamGb,
            cpus = Environment.ProcessorCount,
            rpcPort,
            backend,
            gpus,
        };
        return JsonSerializer.Serialize(payload);
    }

    public static List<(string Name, int VramMb)> Gpus()
    {
        var list = new List<(string, int)>();
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(3000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split(',');
                    if (parts.Length < 2) continue;
                    var name = parts[0].Trim();
                    if (int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb))
                        list.Add((name, mb));
                }
            }
        }
        catch { }
        return list;
    }

    public static string LocalAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    /// <summary>Enkel HTTP-server på info-porten: svarar JSON på alla GET-anrop.</summary>
    public sealed class InfoServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Func<string> _payload;
        private readonly CancellationTokenSource _cts = new();

        public InfoServer(int port, Func<string> payload)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _payload = payload;
        }

        public void Start()
        {
            _listener.Start();
            _ = Task.Run(RunAsync);
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch { break; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buffer = new byte[1024];
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return;

                    var body = Encoding.UTF8.GetBytes(_payload());
                    var head = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {body.Length}\r\nConnection: close\r\n" +
                        "Access-Control-Allow-Origin: *\r\n\r\n");
                    stream.Write(head, 0, head.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
