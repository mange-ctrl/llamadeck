using System.Text;

namespace LlamaDeck.Core;

public sealed class LaunchPlan
{
    public BackendKind Backend { get; set; } = BackendKind.Cpu;
    public string ExePath { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8080;
    public int Threads { get; set; } = 4;
    public int Context { get; set; } = 8192;
    public FlashMode Flash { get; set; } = FlashMode.Auto;
    public string KvType { get; set; } = "f16";
    public bool GpuLayersAuto { get; set; } = true;
    public int GpuLayers { get; set; }
    public List<string> Devices { get; set; } = new();
    public string TensorSplit { get; set; } = "";
    public OffloadMode Offload { get; set; } = OffloadMode.Auto;
    public long? RamLimitBytes { get; set; }
    public string ApiKey { get; set; } = "";
    public int Parallel { get; set; } = 1;
    public int VramReserveMb { get; set; } = 1024;
    public string ExtraArgs { get; set; } = "";
    public string MmprojPath { get; set; } = "";
    public int Ubatch { get; set; }
    public List<string> RpcNodes { get; set; } = new();
    public string RpcWeights { get; set; } = "";
    public MoEMode MoeMode { get; set; } = MoEMode.Auto;
    public int MoeCpuLayers { get; set; }

    public List<string> BuildArgs()
    {
        var a = new List<string>
        {
            "-m", ModelPath,
            "--host", Host,
            "--port", Port.ToString(),
            "-t", Threads.ToString(),
            "-c", Context.ToString(),
            "--jinja",
            "--metrics",
        };

        if (RpcNodes.Count > 0)
        {
            a.Add("--rpc");
            a.Add(string.Join(",", RpcNodes));
        }

        if (MoeMode == MoEMode.AllCpu)
        {
            a.Add("--cpu-moe");
        }
        else if (MoeMode == MoEMode.CpuLayers && MoeCpuLayers > 0)
        {
            a.Add("--n-cpu-moe");
            a.Add(MoeCpuLayers.ToString());
        }

        if (Backend != BackendKind.Cpu)
        {
            if (Offload == OffloadMode.CpuOnly)
            {
                a.Add("-ngl"); a.Add("0");
                a.Add("--fit"); a.Add("off");
            }
            else if (GpuLayersAuto)
            {
                if (RpcNodes.Count > 0)
                {
                    // Kluster: låt llama.cpp fördela alla lager över lokala kort + RPC-noder.
                    a.Add("--fit"); a.Add("off");
                    a.Add("-ngl"); a.Add("99");
                }
                else
                {
                    a.Add("--fit"); a.Add("on");
                    a.Add("--fit-target"); a.Add(Math.Max(0, VramReserveMb).ToString());
                }
            }
            else
            {
                a.Add("--fit"); a.Add("off");
                a.Add("-ngl"); a.Add(GpuLayers.ToString());
            }

            if (Devices.Count > 0)
            {
                a.Add("--device"); a.Add(string.Join(",", Devices));
            }

            var split = !string.IsNullOrWhiteSpace(TensorSplit) ? TensorSplit.Trim() : RpcWeights.Trim();
            if (!string.IsNullOrWhiteSpace(split))
            {
                a.Add("-ts"); a.Add(split);
            }
        }

        a.Add("-fa");
        a.Add(Flash switch { FlashMode.On => "on", FlashMode.Off => "off", _ => "auto" });

        if (!string.Equals(KvType, "f16", StringComparison.OrdinalIgnoreCase))
        {
            a.Add("-ctk"); a.Add(KvType);
            a.Add("-ctv"); a.Add(KvType);
        }

        switch (Offload)
        {
            case OffloadMode.ForceRam:
                a.Add("-lm"); a.Add("none");
                break;
            case OffloadMode.LockRam:
                a.Add("-lm"); a.Add("mmap+mlock");
                break;
        }

        if (Parallel > 0)
        {
            a.Add("--parallel"); a.Add(Parallel.ToString());
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            a.Add("--api-key"); a.Add(ApiKey.Trim());
        }

        if (!string.IsNullOrWhiteSpace(MmprojPath))
        {
            a.Add("--mmproj");
            a.Add(MmprojPath);
        }

        if (Ubatch > 0)
        {
            a.Add("-ub");
            a.Add(Ubatch.ToString());
        }

        foreach (var extra in SplitArgs(ExtraArgs))
            a.Add(extra);

        return a;
    }

    public string Preview
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append('"').Append(ExePath).Append('"');
            foreach (var arg in BuildArgs())
            {
                sb.Append(' ');
                if (arg.Contains(' ') || arg.Contains('"'))
                    sb.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
                else
                    sb.Append(arg);
            }
            return sb.ToString();
        }
    }

    private static IEnumerable<string> SplitArgs(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;
        var current = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in input)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}

/// <summary>Uppskattar VRAM-behov så att användaren ser hur många lager som får plats.</summary>
public static class Planner
{
    public static double BytesPerKvElement(string kvType) => kvType.ToLowerInvariant() switch
    {
        "q8_0" => 1.0625,
        "q4_0" or "q4_1" or "iq4_nl" => 0.5625,
        "q5_0" or "q5_1" => 0.6875,
        _ => 2.0,
    };

    public static double EstimateKvGb(ModelEntry? m, int ctx, string kvType, int parallel = 1)
    {
        if (m == null || m.Layers <= 0 || m.Heads <= 0) return 0;
        var headsKv = Math.Max(1, m.HeadsKv);
        var dim = Math.Max(1, m.Embedding / Math.Max(1, m.Heads));
        var kvDim = dim * headsKv;
        var bytes = 2.0 * m.Layers * ctx * kvDim * BytesPerKvElement(kvType) * Math.Max(1, parallel);
        return bytes / (1024d * 1024d * 1024d);
    }

    public static double BytesPerLayer(ModelEntry m)
    {
        var layers = m.Layers > 0 ? m.Layers : 32;
        return (double)m.SizeBytes / (layers + 2);
    }

    /// <summary>Föreslår antal GPU-lager givet ledigt VRAM (MiB) och önskad reserv.</summary>
    public static (int Layers, double VramGb) Suggest(ModelEntry? m, long freeMiB, int reserveMb, int ctx,
        string kvType, int parallel = 1)
    {
        if (m == null || m.Layers <= 0 || freeMiB <= 0) return (0, 0);

        var kvGb = EstimateKvGb(m, ctx, kvType, parallel);
        const double overheadGb = 0.45; // kontextbuffertar, grafbuffertar m.m.
        var budgetGb = (freeMiB - reserveMb) / 1024d - kvGb - overheadGb;
        if (budgetGb <= 0) return (0, kvGb + overheadGb);

        var perLayerGb = BytesPerLayer(m) / (1024d * 1024d * 1024d);
        var layers = (int)Math.Floor(budgetGb / Math.Max(perLayerGb, 1e-6));
        layers = Math.Clamp(layers, 0, m.Layers);

        var vram = layers * perLayerGb + kvGb + overheadGb;
        return (layers, vram);
    }
}
