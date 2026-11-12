using System.IO;

namespace LlamaDeck.Core;

/// <summary>Ett förslag på inställningar som passar en viss modell på den här datorn.</summary>
public sealed class Recommendation
{
    public int ContextSize { get; set; } = 8192;
    public bool GpuLayersAuto { get; set; }
    public int GpuLayers { get; set; }
    public int Threads { get; set; } = 8;
    public MoEMode MoeMode { get; set; } = MoEMode.Auto;
    public int MoeCpuLayers { get; set; }
    public string KvCacheType { get; set; } = "f16";
    public OffloadMode Offload { get; set; } = OffloadMode.Auto;
    public FlashMode FlashAttention { get; set; } = FlashMode.Auto;
    public bool MmprojEnabled { get; set; }
    public string MmprojPath { get; set; } = "";

    /// <summary>Korta meningar som förklarar varför förslaget ser ut som det gör.</summary>
    public List<string> Reasons { get; } = new();

    /// <summary>En rad att visa i gränssnittet.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string> { $"kontext {ContextSize:N0}" };
            parts.Add(GpuLayers >= 99 ? "alla lager på GPU" : $"{GpuLayers} lager på GPU");
            if (MoeMode == MoEMode.AllCpu) parts.Add("experter på CPU");
            if (KvCacheType != "f16") parts.Add($"KV {KvCacheType}");
            if (Offload == OffloadMode.ForceRam) parts.Add("allt i RAM");
            if (MmprojEnabled) parts.Add("bildstöd");
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Läser modellens GGUF-metadata (lager, huvuden, kontextlängd, MoE) tillsammans med ledigt VRAM,
/// ledigt RAM och disktyp, och räknar fram de inställningar som ger bäst chans att modellen både
/// startar och går fort. Allt är rena beräkningar – inget startas.
/// </summary>
public static class AutoTuner
{
    private static readonly int[] CtxSteps = { 2048, 4096, 8192, 16384, 32768, 65536, 131072 };

    private const double OverheadGb = 0.20;   // graf- och kontextbuffertar
    private const double ReserveFrac = 0.30;  // reserven är ett mål, inte en hård vägg

    /// <summary>Hur stor del av det lediga VRAM:et KV-cachen får ta innan vi väljer mindre kontext.</summary>
    private const double KvShare = 0.30;

    public static int RecommendedThreads()
    {
        var logical = Environment.ProcessorCount;
        // llama.cpp går bäst på fysiska kärnor; på SMT-maskiner är det halva antalet.
        return logical >= 8 ? logical / 2 : logical;
    }

    public static Recommendation Recommend(ModelEntry? m, long freeMiB, int reserveMb, int parallel,
                                           double ramTotalGb, double ramFreeGb, bool? rotational)
    {
        var rec = new Recommendation
        {
            Threads = RecommendedThreads(),
            ContextSize = 8192,
            GpuLayersAuto = false,
            GpuLayers = 0,
            MoeMode = MoEMode.Auto,
            KvCacheType = "f16",
            Offload = OffloadMode.Auto,
        };

        if (m == null)
        {
            rec.Reasons.Add("Ingen modell vald – standardvärden används.");
            return rec;
        }

        if (m.Layers <= 0)
        {
            rec.ContextSize = 4096;
            rec.GpuLayers = 0;
            rec.Reasons.Add("Modellen saknar lager helt – det är ingen språkmodell (t.ex. en " +
                            "bildgenererings- eller projektorfil) och llama.cpp kan inte köra den.");
            return rec;
        }

        var layers = Math.Max(1, m.Layers);
        var sizeGb = m.SizeBytes / (1024d * 1024d * 1024d);
        var usable = Math.Max(0.3, freeMiB / 1024d - Math.Max(0, reserveMb) / 1024d * ReserveFrac - OverheadGb);

        rec.Reasons.Add($"{freeMiB / 1024d:0.0} GB ledigt VRAM ger {usable:0.0} GB att räkna på " +
                        $"(efter reserv och buffertar).");

        if (m.IsMoE)
        {
            // --- MoE: experterna på processorn är det som gör stora MoE-modeller användbara på 8 GB VRAM.
            var kvBudget = Math.Max(0.4, usable * KvShare);
            var ctx = PickCtx(m, kvBudget, parallel, "f16");
            if (ctx == 0)
            {
                ctx = m.ContextLength > 0 ? Math.Min(4096, m.ContextLength) : 4096;
                rec.Reasons.Add($"KV-cachen är dyr för den här modellen – {ctx:N0} i kontext för att den ska rymmas.");
            }
            rec.ContextSize = ctx;
            rec.MoeMode = MoEMode.AllCpu;
            rec.GpuLayers = 99;
            rec.Reasons.Add("Modellen är MoE: expertvikterna läggs på processorn (`--cpu-moe`) och bara " +
                            "uppmärksamhetslagren på grafikkortet, vilket är det som får plats i VRAM.");
        }
        else
        {
            var perLayerGb = sizeGb / (layers + 2);

            // 1) Får alla lager plats? Välj då den största kontexten som fortfarande rymmer dem.
            var bestAll = 0;
            foreach (var c in CtxSteps)
            {
                if (m.ContextLength > 0 && c > m.ContextLength) break;
                var kvAll = Planner.EstimateKvGb(m, c, "f16", parallel);
                if (sizeGb + kvAll <= usable) bestAll = c;
            }

            if (bestAll > 0)
            {
                rec.ContextSize = bestAll;
                rec.GpuLayers = 99;
                rec.Reasons.Add($"Alla {layers} lager får plats på GPU vid {bestAll:N0} i kontext " +
                                $"({sizeGb:0.0} GB vikter + KV).");
            }
            else
            {
                // 2) Annars: håll KV-cachen liten och lägg så många lager som möjligt på GPU.
                var kvBudget = Math.Max(0.4, usable * KvShare);
                var ctx = PickCtx(m, kvBudget, parallel, "f16");
                if (ctx == 0) ctx = m.ContextLength > 0 ? Math.Min(4096, m.ContextLength) : 4096;
                rec.ContextSize = ctx;

                var kv = Planner.EstimateKvGb(m, ctx, "f16", parallel);
                var fit = (int)Math.Floor((usable - kv) / Math.Max(perLayerGb, 1e-6));
                rec.GpuLayers = Math.Clamp(fit, 0, layers);
                if (rec.GpuLayers <= 0)
                    rec.Reasons.Add("Modellen är för stor för VRAM – den körs på processorn. Räkna med låg hastighet.");
                else
                    rec.Reasons.Add($"{rec.GpuLayers} av {layers} lager får plats på GPU vid {ctx:N0} i kontext " +
                                    $"(~{perLayerGb * 1024:0} MB per lager); resten räknas på processorn.");
            }
        }

        // --- KV-cache: byt till q8_0 om cachen annars tar för stor del av VRAM.
        var kvF16 = Planner.EstimateKvGb(m, rec.ContextSize, "f16", parallel);
        if (kvF16 > usable * 0.30 && rec.ContextSize > 4096)
        {
            rec.KvCacheType = "q8_0";
            rec.Reasons.Add($"KV-cachen skulle ta {kvF16:0.0} GB i f16 – q8_0 halverar den.");
        }

        // --- Inläsning: mekanisk disk + modellen får plats i RAM = läs in allt i RAM.
        if (rotational == true)
        {
            if (sizeGb <= ramFreeGb * 0.85 && sizeGb <= ramTotalGb * 0.60)
            {
                rec.Offload = OffloadMode.ForceRam;
                rec.Reasons.Add($"Modellen ligger på en mekanisk disk men är bara {sizeGb:0.0} GB, så den läses " +
                                $"in i RAM i stället för att läsas från disken vid varje token.");
            }
            else
            {
                rec.Reasons.Add($"Modellen ligger på en mekanisk disk och är {sizeGb:0.0} GB – den får inte plats " +
                                $"i RAM ({ramFreeGb:0.0} GB ledigt). Flytta filen till en SSD för bättre hastighet.");
            }
        }
        else if (rotational == false && sizeGb > 12 && sizeGb <= ramFreeGb * 0.8)
        {
            rec.Reasons.Add("Modellen ligger på en SSD – mmap fungerar bra, så den läses inte in i RAM i onödan.");
        }

        if (m.ContextLength > 0 && rec.ContextSize > m.ContextLength)
            rec.ContextSize = m.ContextLength;

        return rec;
    }

    /// <summary>Största kontextsteget där KV-cachen ryms i budgeten, annars 0.</summary>
    private static int PickCtx(ModelEntry m, double budgetGb, int parallel, string kvType)
    {
        var best = 0;
        foreach (var ctx in CtxSteps)
        {
            if (m.ContextLength > 0 && ctx > m.ContextLength) break;
            var kv = Planner.EstimateKvGb(m, ctx, kvType, parallel);
            if (kv <= budgetGb) best = ctx;
        }
        return best;
    }

    /// <summary>Letar upp en mmproj-fil som hör ihop med modellen (samma mapp, samma basnamn).</summary>
    public static string FindSiblingMmproj(string modelPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return "";
            var modelBase = Path.GetFileNameWithoutExtension(modelPath);
            var candidates = Directory.EnumerateFiles(dir, "*mmproj*.gguf", SearchOption.TopDirectoryOnly)
                .Select(f => (File: f, Name: Path.GetFileNameWithoutExtension(f)))
                .ToList();
            if (candidates.Count == 0) return "";

            // Helst en mmproj vars namn liknar modellens, annars den enda som finns.
            var byName = candidates.FirstOrDefault(c =>
                modelBase.Contains(Token(c.Name), StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(Token(modelBase), StringComparison.OrdinalIgnoreCase));
            if (byName.File != null) return byName.File;
            return candidates.Count == 1 ? candidates[0].File : "";
        }
        catch
        {
            return "";
        }
    }

    private static string Token(string name)
    {
        // Grovt jämförelsemarkör: första meningsfulla ordet i filnamnet.
        var cleaned = name.Replace("mmproj-", "", StringComparison.OrdinalIgnoreCase);
        var parts = cleaned.Split('-', '_', '.');
        return parts.FirstOrDefault(p => p.Length >= 4) ?? cleaned;
    }

    /// <summary>Talar om ifall arkitekturen har bildstöd som llama.cpp kan använda.</summary>
    public static bool SupportsVision(string arch) =>
        arch is "gemma3" or "gemma4" or "qwen2vl" or "qwen2.5vl" or "qwen35" or "qwen35moe"
             or "mistral3" or "llama4" or "mllama" or "pixtral" or "internvl" or "minicpmv" or "lfm2";
}
