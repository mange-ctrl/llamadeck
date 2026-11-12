using System.IO;
using System.Text.RegularExpressions;

namespace LlamaDeck.Core;

public sealed class ModelEntry
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public long SizeBytes { get; init; }
    public string Quant { get; init; } = "";
    public string Arch { get; init; } = "";
    public int Layers { get; init; }
    public int ContextLength { get; init; }
    public int Embedding { get; init; }
    public int HeadsKv { get; init; }
    public int Heads { get; init; }
    public bool IsMoE { get; init; }
    public DateTime Modified { get; init; }

    public string Title => System.IO.Path.GetFileNameWithoutExtension(FileName);
    public string SizeText => Fmt.Size(SizeBytes);
    public string Badge => string.IsNullOrEmpty(Quant) ? "GGUF" : Quant;

    public string SubText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Arch)) parts.Add(Arch);
            if (Layers > 0) parts.Add($"{Layers} lager");
            parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }

    public string Detail
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Arch)) parts.Add(Arch);
            if (Layers > 0) parts.Add($"{Layers} lager");
            if (ContextLength > 0) parts.Add($"kontext {ContextLength}");
            if (IsMoE) parts.Add("MoE");
            if (!string.IsNullOrEmpty(Quant)) parts.Add(Quant);
            parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }
}

public static class ModelLibrary
{
    private static readonly Regex QuantRx = new(
        @"(?i)(IQ\d_[A-Z0-9]+|Q\d+_K_[SML]|Q\d+_K|Q\d+_\d|Q\d+|BF16|F16|FP16|F32|MXFP4)",
        RegexOptions.Compiled);

    private static readonly Regex SplitRx = new(@"-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Task<List<ModelEntry>> ScanAsync(string root, CancellationToken ct)
        => Task.Run(() => Scan(root, ct), ct);

    /// <summary>Söker igenom flera mappar (olika diskar) och tar bort dubbletter.</summary>
    public static Task<List<ModelEntry>> ScanManyAsync(IEnumerable<string> roots, CancellationToken ct)
        => Task.Run(() => ScanMany(roots, ct), ct);

    public static List<ModelEntry> ScanMany(IEnumerable<string> roots, CancellationToken ct)
    {
        var all = new List<ModelEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            foreach (var m in Scan(root, ct))
                if (seen.Add(m.Path)) all.Add(m);
        }

        return all
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<ModelEntry> Scan(string root, CancellationToken ct)
    {
        var result = new List<ModelEntry>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;

        var stack = new Stack<string>();
        stack.Push(root);
        int visitedDirs = 0;

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            if (++visitedDirs > 400) break;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith('.')) continue;
                    stack.Push(sub);
                }
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.gguf", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(file);
                    // mmproj-filer är visionsprojektorer, inte modeller – de listas separat i Bildstöd.
                    if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase)) continue;
                    var split = SplitRx.Match(name);
                    if (split.Success && split.Groups[1].Value != "00001") continue;

                    long size;
                    DateTime modified;
                    try
                    {
                        var fi = new FileInfo(file);
                        size = fi.Length;
                        modified = fi.LastWriteTime;
                    }
                    catch
                    {
                        continue;
                    }

                    if (size < 1024) continue;

                    var meta = GgufReader.Read(file);
                    var arch = meta?.Architecture ?? "";
                    // Visionsprojektorer (clip) och draft-modeller (t.ex. *-assistant) går inte att
                    // starta som vanliga modeller – hoppa över dem i listan.
                    if (arch.Equals("clip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (arch.EndsWith("assistant", StringComparison.OrdinalIgnoreCase)) continue;
                    var quant = QuantRx.Match(name) is { Success: true } m ? m.Value.ToUpperInvariant() : "";

                    result.Add(new ModelEntry
                    {
                        Path = file,
                        FileName = name,
                        SizeBytes = size,
                        Modified = modified,
                        Quant = quant,
                        Arch = arch,
                        Layers = meta?.BlockCount ?? 0,
                        ContextLength = meta?.ContextLength ?? 0,
                        Embedding = meta?.EmbeddingLength ?? 0,
                        HeadsKv = meta?.HeadCountKv ?? 0,
                        Heads = meta?.HeadCount ?? 0,
                        IsMoE = meta?.IsMoE ?? false,
                    });
                }
            }
            catch
            {
                // hoppa över mapp vi inte får läsa
            }
        }

        return result
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
