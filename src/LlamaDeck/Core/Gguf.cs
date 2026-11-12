using System.IO;
using System.Text;

namespace LlamaDeck.Core;

public sealed class GgufMeta
{
    public string? Name { get; set; }
    public string? Architecture { get; set; }
    public string? SizeLabel { get; set; }
    public int BlockCount { get; set; }
    public int EmbeddingLength { get; set; }
    public int HeadCount { get; set; }
    public int HeadCountKv { get; set; }
    public int ContextLength { get; set; }
    public int ExpertCount { get; set; }
    public long TensorCount { get; set; }
    public bool IsMoE => ExpertCount > 0;

    public string Describe()
    {
        var arch = string.IsNullOrWhiteSpace(Architecture) ? "okänd" : Architecture;
        return $"{arch} · {BlockCount} lager · {EmbeddingLength} dim · ctx {ContextLength}";
    }
}

/// <summary>Minimal läsare av GGUF-metadata (räcker för att räkna ut lager och KV-cache).</summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"

    public static GgufMeta? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, false);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);
            if (br.ReadUInt32() != Magic) return null;
            uint version = br.ReadUInt32();
            if (version is < 2 or > 4) return null;

            var meta = new GgufMeta { TensorCount = (long)br.ReadUInt64() };
            ulong kvCount = br.ReadUInt64();
            if (kvCount > 2_000_000) return null;

            for (ulong i = 0; i < kvCount; i++)
            {
                if (fs.Position >= fs.Length) break;
                string key = ReadString(br);
                uint type = br.ReadUInt32();

                if (type == 9)
                {
                    uint elemType = br.ReadUInt32();
                    ulong count = br.ReadUInt64();
                    if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal) && count > 0 && count < 4096)
                    {
                        int max = 0;
                        for (ulong j = 0; j < count; j++)
                        {
                            int v = (int)ReadInt(br, fs, elemType);
                            if (v > max) max = v;
                        }
                        meta.HeadCountKv = max;
                    }
                    else
                    {
                        SkipArray(br, fs, elemType, count);
                    }
                    continue;
                }

                switch (key)
                {
                    case "general.name":
                        meta.Name = type == 8 ? ReadString(br) : SkipAndNull(br, fs, type);
                        break;
                    case "general.architecture":
                        meta.Architecture = type == 8 ? ReadString(br) : SkipAndNull(br, fs, type);
                        break;
                    case "general.size_label":
                        meta.SizeLabel = type == 8 ? ReadString(br) : SkipAndNull(br, fs, type);
                        break;
                    default:
                        if (key.EndsWith(".block_count", StringComparison.Ordinal))
                            meta.BlockCount = (int)ReadInt(br, fs, type);
                        else if (key.EndsWith(".embedding_length", StringComparison.Ordinal))
                            meta.EmbeddingLength = (int)ReadInt(br, fs, type);
                        else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal))
                            meta.HeadCount = (int)ReadInt(br, fs, type);
                        else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
                            meta.HeadCountKv = (int)ReadInt(br, fs, type);
                        else if (key.EndsWith(".context_length", StringComparison.Ordinal))
                            meta.ContextLength = (int)ReadInt(br, fs, type);
                        else if (key.EndsWith(".expert_count", StringComparison.Ordinal))
                            meta.ExpertCount = (int)ReadInt(br, fs, type);
                        else
                            Skip(br, fs, type);
                        break;
                }
            }

            if (meta.HeadCountKv <= 0) meta.HeadCountKv = meta.HeadCount;
            return meta;
        }
        catch
        {
            return null;
        }
    }

    private static string SkipAndNull(BinaryReader br, FileStream fs, uint type)
    {
        Skip(br, fs, type);
        return "";
    }

    private static string ReadString(BinaryReader br)
    {
        ulong len = br.ReadUInt64();
        if (len > 32_000_000) throw new InvalidDataException("orimlig stränglängd");
        var bytes = br.ReadBytes((int)len);
        return Encoding.UTF8.GetString(bytes);
    }

    private static long ReadInt(BinaryReader br, FileStream fs, uint type) => type switch
    {
        0 => br.ReadByte(),
        1 => br.ReadSByte(),
        2 => br.ReadUInt16(),
        3 => br.ReadInt16(),
        4 => br.ReadUInt32(),
        5 => br.ReadInt32(),
        10 => (long)br.ReadUInt64(),
        11 => br.ReadInt64(),
        6 => (long)br.ReadSingle(),
        12 => (long)br.ReadDouble(),
        7 => br.ReadByte(),
        _ => SkipAndZero(br, fs, type),
    };

    private static long SkipAndZero(BinaryReader br, FileStream fs, uint type)
    {
        Skip(br, fs, type);
        return 0;
    }

    private static void Skip(BinaryReader br, FileStream fs, uint type)
    {
        switch (type)
        {
            case 8:
                {
                    ulong len = br.ReadUInt64();
                    if (len > (ulong)Math.Max(0, fs.Length - fs.Position)) throw new InvalidDataException();
                    fs.Seek((long)len, SeekOrigin.Current);
                    break;
                }
            case 9:
                {
                    uint elemType = br.ReadUInt32();
                    ulong count = br.ReadUInt64();
                    SkipArray(br, fs, elemType, count);
                    break;
                }
            default:
                int size = TypeSize(type);
                if (size <= 0) throw new InvalidDataException("okänd typ");
                if ((ulong)size > (ulong)Math.Max(0, fs.Length - fs.Position)) throw new InvalidDataException();
                fs.Seek(size, SeekOrigin.Current);
                break;
        }
    }

    private static void SkipArray(BinaryReader br, FileStream fs, uint elemType, ulong count)
    {
        if (count > 50_000_000) throw new InvalidDataException("orimlig array");
        if (elemType == 8)
        {
            for (ulong i = 0; i < count; i++)
            {
                ulong len = br.ReadUInt64();
                if (len > (ulong)Math.Max(0, fs.Length - fs.Position)) throw new InvalidDataException();
                fs.Seek((long)len, SeekOrigin.Current);
            }
            return;
        }
        if (elemType == 9)
        {
            for (ulong i = 0; i < count; i++)
            {
                uint inner = br.ReadUInt32();
                ulong innerCount = br.ReadUInt64();
                SkipArray(br, fs, inner, innerCount);
            }
            return;
        }

        int size = TypeSize(elemType);
        if (size <= 0) throw new InvalidDataException("okänd arraytyp");
        long total = (long)count * size;
        if (total < 0 || total > Math.Max(0, fs.Length - fs.Position)) throw new InvalidDataException();
        fs.Seek(total, SeekOrigin.Current);
    }

    private static int TypeSize(uint type) => type switch
    {
        0 or 1 or 7 => 1,
        2 or 3 => 2,
        4 or 5 or 6 => 4,
        10 or 11 or 12 => 8,
        _ => 0,
    };
}
