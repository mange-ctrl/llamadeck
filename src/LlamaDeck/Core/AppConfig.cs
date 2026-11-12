using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaDeck.Core;

public enum BackendKind { Cpu, Vulkan, Cuda12, Cuda13, OpenCl, Rocm, Sycl }

public enum HostMode { Local, Network }

public enum OffloadMode { Auto, ForceRam, LockRam, CpuOnly }

public enum FlashMode { On, Off, Auto }

public enum MoEMode { Auto, CpuLayers, AllCpu }

public sealed class AppConfig
{
    // Modeller
    public string ModelFolder { get; set; } = "";
    public string LastModelPath { get; set; } = "";

    // Bildstöd (mmproj): visionsfil som laddas tillsammans med modellen
    public bool MmprojEnabled { get; set; } = false;
    public string MmprojPath { get; set; } = "";
    public int MmprojUbatch { get; set; } = 0;

    // Server
    public int Port { get; set; } = 8080;
    public HostMode HostMode { get; set; } = HostMode.Local;
    public BackendKind Backend { get; set; } = BackendKind.Vulkan;
    public int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int ContextSize { get; set; } = 8192;
    public bool GpuLayersAuto { get; set; } = true;
    public int GpuLayers { get; set; } = 99;
    public FlashMode FlashAttention { get; set; } = FlashMode.Auto;
    public string KvCacheType { get; set; } = "f16";
    public int ParallelSlots { get; set; } = 1;
    public string ApiKey { get; set; } = "";

    // Minne
    public int VramReserveMb { get; set; } = 1024;
    public int OsReserveGb { get; set; } = 4;
    public bool RamGuardEnabled { get; set; } = true;
    public OffloadMode Offload { get; set; } = OffloadMode.Auto;
    public List<int> SelectedGpus { get; set; } = new();
    public string TensorSplit { get; set; } = "";

    // Kluster (RPC-noder)
    public List<string> RpcNodes { get; set; } = new();
    public string RpcWeights { get; set; } = "";

    // MoE: lägg expertvikterna på processorn
    public MoEMode MoeMode { get; set; } = MoEMode.Auto;
    public int MoeCpuLayers { get; set; } = 0;

    /// <summary>Ställ in kontext, GPU-lager, MoE, KV-typ och inläsning automatiskt när en modell väljs.</summary>
    public bool AutoTune { get; set; } = true;

    // Logg & beteende
    public bool SaveLogToFile { get; set; } = true;
    public bool AutoScrollLog { get; set; } = true;
    public bool AutoRestart { get; set; } = false;
    public bool CheckUpdatesOnStart { get; set; } = true;
    public string ExtraArgs { get; set; } = "";

    // Fönster
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 920;
    public bool WindowMaximized { get; set; } = false;
    public bool FirstRun { get; set; } = true;

    [JsonIgnore]
    public string EffectiveModelFolder =>
        string.IsNullOrWhiteSpace(ModelFolder) ? AppPaths.ModelsDir : ModelFolder;

    /// <summary>
    /// Alla modellmappar. Flera mappar skrivs åtskilda med semikolon i ModelFolder,
    /// t.ex. <c>"G:\ai modeler ocensur;H:\gguf"</c>. Saknade mappar hoppas över.
    /// </summary>
    [JsonIgnore]
    public List<string> EffectiveModelFolders
    {
        get
        {
            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in EffectiveModelFolder.Split(';'))
            {
                var dir = part.Trim().TrimEnd('\\', '/');
                if (dir.Length == 0) continue;
                if (!seen.Add(dir)) continue;
                list.Add(dir);
            }
            return list;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFile))
            {
                var json = File.ReadAllText(AppPaths.ConfigFile);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // korrupt config – börja om med standardvärden
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOptions);
            var tmp = AppPaths.ConfigFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, AppPaths.ConfigFile, true);
        }
        catch
        {
            // kan inte skriva – ignorera tyst, appen fungerar ändå
        }
    }

    public static string BackendName(BackendKind kind) => kind switch
    {
        BackendKind.Cpu => "CPU",
        BackendKind.Vulkan => "Vulkan",
        BackendKind.Cuda12 => "CUDA 12",
        BackendKind.Cuda13 => "CUDA 13",
        BackendKind.OpenCl => "OpenCL",
        BackendKind.Rocm => "ROCm",
        BackendKind.Sycl => "SYCL",
        _ => kind.ToString(),
    };

    public static string BackendFolder(BackendKind kind) => kind switch
    {
        BackendKind.Cpu => "cpu",
        BackendKind.Vulkan => "vulkan",
        BackendKind.Cuda12 => "cuda12",
        BackendKind.Cuda13 => "cuda13",
        BackendKind.OpenCl => "opencl",
        BackendKind.Rocm => "rocm",
        BackendKind.Sycl => "sycl",
        _ => "cpu",
    };
}
