using System.IO;

namespace LlamaDeck.Core;

/// <summary>Var programmet lagrar konfiguration, backends, loggar och standardmodeller.</summary>
public static class AppPaths
{
    public static string ExeDir { get; private set; } = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string DataDir { get; private set; } = "";
    public static bool Portable { get; private set; }

    public static string BackendsDir => Path.Combine(DataDir, "backends");
    public static string LogsDir => Path.Combine(DataDir, "logs");
    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string DownloadsDir => Path.Combine(DataDir, "downloads");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    public static void Init(bool forcePortable, string? overrideDir = null)
    {
        Portable = forcePortable || File.Exists(Path.Combine(ExeDir, "portable.txt"));

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            candidates.Add(overrideDir!);
        }
        else if (Portable)
        {
            candidates.Add(Path.Combine(ExeDir, "data"));
        }
        else
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlamaDeck"));
            candidates.Add(Path.Combine(ExeDir, "data"));
        }

        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".write-test");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                DataDir = dir;
                break;
            }
            catch
            {
                // prova nästa
            }
        }

        if (string.IsNullOrEmpty(DataDir))
        {
            DataDir = Path.Combine(Path.GetTempPath(), "LlamaDeck");
            Directory.CreateDirectory(DataDir);
        }

        Directory.CreateDirectory(BackendsDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ModelsDir);
        Directory.CreateDirectory(DownloadsDir);
    }
}
