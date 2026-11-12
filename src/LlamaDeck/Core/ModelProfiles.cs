using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaDeck.Core;

/// <summary>
/// Testade inställningar för en enskild modell. Fälten är valfria: bara de som är satta
/// skrivs över när modellen väljs i listan.
/// </summary>
public sealed class ModelProfile
{
    public string? Note { get; set; }
    public BackendKind? Backend { get; set; }
    public int? ContextSize { get; set; }
    public int? Threads { get; set; }
    public bool? GpuLayersAuto { get; set; }
    public int? GpuLayers { get; set; }
    public FlashMode? FlashAttention { get; set; }
    public string? KvCacheType { get; set; }
    public int? VramReserveMb { get; set; }
    public OffloadMode? Offload { get; set; }
    public MoEMode? MoeMode { get; set; }
    public int? MoeCpuLayers { get; set; }
    public int? ParallelSlots { get; set; }
    public bool? MmprojEnabled { get; set; }
    public string? MmprojPath { get; set; }
    public string? ExtraArgs { get; set; }
}

/// <summary>
/// Läser <c>profiles.json</c> ur datamappen. Nyckeln är modellens filnamn
/// (t.ex. <c>"Qwen3.6-35B-A3B-Q4_K_M.gguf"</c>), så profilen följer med filen
/// oavsett vilken mapp den ligger i.
/// </summary>
public static class ModelProfiles
{
    private static Dictionary<string, ModelProfile> _map = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath => Path.Combine(AppPaths.DataDir, "profiles.json");

    public static int Count => _map.Count;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    /// <summary>Läser om filen. Tål att filen saknas eller är trasig.</summary>
    public static int Reload()
    {
        var map = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var doc = JsonSerializer.Deserialize<Dictionary<string, ModelProfile>>(json, ReadOptions);
                if (doc != null)
                {
                    foreach (var kv in doc)
                    {
                        var key = Path.GetFileName(kv.Key.Trim());
                        if (key.Length > 0 && kv.Value != null) map[key] = kv.Value;
                    }
                }
            }
        }
        catch
        {
            // trasig eller oläsbar fil – appen kör vidare utan profiler
        }

        _map = map;
        return map.Count;
    }

    public static ModelProfile? For(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return null;
        return _map.TryGetValue(Path.GetFileName(modelPath), out var p) ? p : null;
    }

    /// <summary>
    /// Sparar de inställningar som faktiskt användes när servern startades, så att manuella
    /// ändringar finns kvar nästa gång samma modell väljs. En befintlig anteckning behålls.
    /// </summary>
    public static void Remember(string modelPath, AppConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(modelPath)) return;
        var key = Path.GetFileName(modelPath);
        var existing = _map.TryGetValue(key, out var p) ? p : new ModelProfile();

        existing.Backend = cfg.Backend;
        existing.ContextSize = cfg.ContextSize;
        existing.Threads = cfg.Threads;
        existing.GpuLayersAuto = cfg.GpuLayersAuto;
        existing.GpuLayers = cfg.GpuLayers;
        existing.FlashAttention = cfg.FlashAttention;
        existing.KvCacheType = cfg.KvCacheType;
        existing.VramReserveMb = cfg.VramReserveMb;
        existing.Offload = cfg.Offload;
        existing.MoeMode = cfg.MoeMode;
        existing.MoeCpuLayers = cfg.MoeCpuLayers;
        existing.ParallelSlots = cfg.ParallelSlots;
        existing.MmprojEnabled = cfg.MmprojEnabled;
        existing.MmprojPath = cfg.MmprojEnabled ? cfg.MmprojPath : "";
        existing.ExtraArgs = cfg.ExtraArgs;

        _map[key] = existing;
        Save();
    }

    /// <summary>Skriver hela profilsamlingen till disk.</summary>
    public static bool Save()
    {
        try
        {
            var sorted = _map
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(sorted, ReadOptions);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Skriver över profilens värden på konfigurationen. Returnerar true om något ändrades.</summary>
    public static bool Apply(ModelProfile p, AppConfig cfg)
    {
        var changed = false;
        void Set<T>(T? value, Action<T> apply) where T : struct
        {
            if (value is null) return;
            apply(value.Value);
            changed = true;
        }

        Set(p.Backend, v => cfg.Backend = v);
        Set(p.ContextSize, v => { if (v >= 256) cfg.ContextSize = v; });
        Set(p.Threads, v => { if (v >= 1) cfg.Threads = v; });
        Set(p.GpuLayersAuto, v => cfg.GpuLayersAuto = v);
        Set(p.GpuLayers, v => { if (v >= 0) cfg.GpuLayers = v; });
        Set(p.FlashAttention, v => cfg.FlashAttention = v);
        Set(p.VramReserveMb, v => { if (v >= 0) cfg.VramReserveMb = v; });
        Set(p.Offload, v => cfg.Offload = v);
        Set(p.MoeMode, v => cfg.MoeMode = v);
        Set(p.MoeCpuLayers, v => { if (v >= 0) cfg.MoeCpuLayers = v; });
        Set(p.ParallelSlots, v => { if (v >= 1) cfg.ParallelSlots = v; });
        Set(p.MmprojEnabled, v => cfg.MmprojEnabled = v);

        if (!string.IsNullOrWhiteSpace(p.KvCacheType))
        {
            cfg.KvCacheType = p.KvCacheType!.Trim().ToLowerInvariant();
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(p.MmprojPath))
        {
            cfg.MmprojPath = p.MmprojPath!.Trim();
            changed = true;
        }

        if (p.ExtraArgs != null)
        {
            cfg.ExtraArgs = p.ExtraArgs.Trim();
            changed = true;
        }

        return changed;
    }
}
