using System.Globalization;
using System.IO;

namespace LlamaDeck.Core;

public static class Fmt
{
    public static string Size(long bytes)
    {
        double b = bytes;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1)
        {
            b /= 1024;
            i++;
        }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.#} {units[i]}";
    }

    public static string Gb(double gb) => gb.ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    public static string Invariant(double v, string fmt = "0.0") => v.ToString(fmt, CultureInfo.InvariantCulture);
}

public static class PortUtil
{
    public static bool IsFree(int port)
    {
        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public static class NetUtil
{
    public static string LocalIp()
    {
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                        !System.Net.IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch
        {
            // ignorera
        }
        return "127.0.0.1";
    }
}

public static class PathUtil
{
    public static string Ellipsis(string path, int max = 46)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Length <= max) return path;
        var name = Path.GetFileName(path);
        var root = Path.GetPathRoot(path) ?? "";
        var middle = Math.Max(4, max - name.Length - root.Length - 4);
        var dir = Path.GetDirectoryName(path) ?? "";
        var cut = dir.Length > middle ? dir[..middle] + "…" : dir;
        return Path.Combine(cut, name);
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
            // ignorera
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignorera
        }
    }
}
