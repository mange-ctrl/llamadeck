using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace LlamaDeck.Core;

public sealed class BackendVersion
{
    public string Tag { get; set; } = "";
    public string Variant { get; set; } = "";
    public string InstalledAt { get; set; } = "";
    public string Source { get; set; } = "";
}

/// <summary>Hanterar llama.cpp-binärerna: inbäddade backends, installation och versioner.</summary>
public static class BackendManager
{
    public const string BundledTag = "b12110";
    private static readonly string[] BundledVariants = { "cpu", "vulkan", "opencl" };

    public static string FolderFor(BackendKind kind) => Path.Combine(AppPaths.BackendsDir, AppConfig.BackendFolder(kind));
    public static string ExeFor(BackendKind kind) => Path.Combine(FolderFor(kind), "llama-server.exe");
    public static bool IsInstalled(BackendKind kind) => File.Exists(ExeFor(kind));

    public static string VariantFolder(BackendKind kind) => AppConfig.BackendFolder(kind);

    public static BackendVersion? ReadVersion(BackendKind kind)
    {
        try
        {
            var path = Path.Combine(FolderFor(kind), "version.json");
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<BackendVersion>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void WriteVersion(BackendKind kind, string tag, string source)
    {
        try
        {
            var v = new BackendVersion
            {
                Tag = tag,
                Variant = AppConfig.BackendFolder(kind),
                InstalledAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Source = source,
            };
            File.WriteAllText(Path.Combine(FolderFor(kind), "version.json"),
                JsonSerializer.Serialize(v, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // ignorera
        }
    }

    public static long FolderSize(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Packar upp de backends som ligger inbäddade i exe-filen (CPU + Vulkan).</summary>
    public static async Task<bool> EnsureBundledAsync(Action<string> log, CancellationToken ct)
    {
        var marker = Path.Combine(AppPaths.BackendsDir, ".bundle-" + BundledTag);
        var missing = BundledVariants
            .Where(v => !File.Exists(Path.Combine(AppPaths.BackendsDir, v, "llama-server.exe")))
            .ToList();

        if (missing.Count == 0)
        {
            if (!File.Exists(marker)) TryWrite(marker, BundledTag);
            return false;
        }

        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("backends.zip", StringComparison.OrdinalIgnoreCase));
        if (resName == null)
        {
            log("Ingen inbäddad llama.cpp-backend hittades i programmet.");
            return false;
        }

        await Task.Run(() =>
        {
            using var stream = asm.GetManifestResourceStream(resName)!;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            int total = zip.Entries.Count, done = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                var top = name.Split('/')[0].ToLowerInvariant();
                done++;
                if (!missing.Contains(top)) continue;
                if (name.EndsWith('/')) continue;

                var dest = Path.Combine(AppPaths.BackendsDir, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, true);

                if (done % 40 == 0)
                    log($"Packar upp {top} … {(int)(100.0 * done / Math.Max(1, total))}%");
            }

            foreach (var v in missing)
            {
                var kind = v switch
                {
                    "cpu" => BackendKind.Cpu,
                    "vulkan" => BackendKind.Vulkan,
                    "cuda12" => BackendKind.Cuda12,
                    "cuda13" => BackendKind.Cuda13,
                    "opencl" => BackendKind.OpenCl,
                    "rocm" => BackendKind.Rocm,
                    "sycl" => BackendKind.Sycl,
                    _ => BackendKind.Cpu,
                };
                WriteVersion(kind, BundledTag, "bundle");
            }

            TryWrite(marker, BundledTag);
            log("Inbäddade backends klara.");
        }, ct);

        return true;
    }

    private static void TryWrite(string path, string text)
    {
        try { File.WriteAllText(path, text); } catch { }
    }

    /// <summary>Installerar en backend från nedladdade zip-filer (llama.cpp + ev. cudart).</summary>
    public static async Task InstallFromZipAsync(BackendKind kind, string mainZip, string? cudartZip,
        string tag, Action<string>? progress, CancellationToken ct)
    {
        var target = FolderFor(kind);
        var tmp = Path.Combine(AppPaths.BackendsDir, ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            progress?.Invoke("Packar upp nedladdade filer …");
            await Task.Run(() =>
            {
                ZipFile.ExtractToDirectory(mainZip, tmp, true);
                if (!string.IsNullOrEmpty(cudartZip) && File.Exists(cudartZip))
                    ZipFile.ExtractToDirectory(cudartZip, tmp, true);
            }, ct);

            progress?.Invoke("Installerar …");
            DeleteFolderWithRetry(target, progress);
            Directory.CreateDirectory(target);

            foreach (var file in Directory.EnumerateFiles(tmp, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(tmp, file);
                var dest = Path.Combine(target, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, true);
            }

            WriteVersion(kind, tag, "download");
            progress?.Invoke("Klart.");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
    }

    /// <summary>
    /// Raderar målmappen före installation. En kvarvarande llama-server (eller ett annat program)
    /// låser DLL-filerna, så vi försöker några gånger och säger tydligt till om det inte går.
    /// </summary>
    private static void DeleteFolderWithRetry(string target, Action<string>? progress)
    {
        if (!Directory.Exists(target)) return;

        Exception? last = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                Directory.Delete(target, true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                progress?.Invoke($"Filen används av ett annat program – försöker igen ({attempt}/6) …");
                Thread.Sleep(1500);
            }
        }

        throw new IOException(
            $"Kunde inte ersätta {target}: en eller flera filer används av ett annat program. " +
            "Stäng llama-server (och andra program som använder llama.cpp) och försök igen. " +
            $"Teknisk orsak: {last?.Message}");
    }
}
