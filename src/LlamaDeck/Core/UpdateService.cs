using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlamaDeck.Core;

public sealed class ReleaseAsset
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public long Size { get; init; }
}

public sealed class LlamaRelease
{
    public string Tag { get; init; } = "";
    public DateTime Published { get; init; }
    public List<ReleaseAsset> Assets { get; init; } = new();

    public ReleaseAsset? VariantAsset(BackendKind kind)
    {
        Regex rx = kind switch
        {
            BackendKind.Cpu => new(@"^llama-.*-bin-win-cpu-x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.Vulkan => new(@"^llama-.*-bin-win-vulkan-x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.Cuda12 => new(@"^llama-.*-bin-win-cuda-12\.\d+-x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.Cuda13 => new(@"^llama-.*-bin-win-cuda-13\.\d+-x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.OpenCl => new(@"^llama-.*-bin-win-opencl.*x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.Rocm => new(@"^llama-.*-bin-win-rocm-[\d.]+-x64\.zip$", RegexOptions.IgnoreCase),
            BackendKind.Sycl => new(@"^llama-.*-bin-win-sycl-x64\.zip$", RegexOptions.IgnoreCase),
            _ => new(@"^$"),
        };
        return Assets.FirstOrDefault(a => rx.IsMatch(a.Name));
    }

    public ReleaseAsset? CudartAsset(BackendKind kind)
    {
        // CUDA-runtime behövs bara för CUDA-varianterna.
        if (kind is not (BackendKind.Cuda12 or BackendKind.Cuda13)) return null;

        string major = kind == BackendKind.Cuda13 ? "13" : "12";
        var rx = new Regex($@"^cudart-llama-bin-win-cuda-{major}\.\d+-x64\.zip$", RegexOptions.IgnoreCase);
        return Assets.FirstOrDefault(a => rx.IsMatch(a.Name));
    }
}

/// <summary>Kollar och hämtar uppdateringar: llama.cpp, CUDA-runtime och Vulkan.</summary>
public static class UpdateService
{
    public static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        // CUDA-runtimen är ~430 MB; på en långsam eller upptagen lina tar den lätt mer
        // än tio minuter, så den gamla gränsen avbröt nedladdningar i onödan.
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LlamaDeck/1.0 (+windows)");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static async Task<LlamaRelease?> GetLatestLlamaAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var json = await Http.GetStringAsync(
            "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=20", timeout.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            var assets = new List<ReleaseAsset>();
            if (rel.TryGetProperty("assets", out var arr))
            {
                foreach (var a in arr.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    var url = a.GetProperty("browser_download_url").GetString() ?? "";
                    long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    assets.Add(new ReleaseAsset { Name = name, Url = url, Size = size });
                }
            }

            if (!assets.Any(a => a.Name.Contains("bin-win-cpu-x64.zip", StringComparison.OrdinalIgnoreCase)))
                continue;

            DateTime.TryParse(rel.GetProperty("published_at").GetString(), out var published);
            return new LlamaRelease
            {
                Tag = rel.GetProperty("tag_name").GetString() ?? "",
                Published = published,
                Assets = assets,
            };
        }
        return null;
    }

    public static async Task<string?> GetLatestVulkanAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var json = await Http.GetStringAsync("https://vulkan.lunarg.com/sdk/latest/windows.json", timeout.Token).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("windows", out var v) ? v.GetString() : null;
    }

    public static async Task<(string? Cuda12, string? Cuda13)> GetLatestCudaToolkitAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var html = await Http.GetStringAsync("https://developer.download.nvidia.com/compute/cuda/redist/", timeout.Token).ConfigureAwait(false);
        var versions = Regex.Matches(html, @"redistrib_(\d+\.\d+\.\d+)\.json")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        string? Best(string major)
        {
            var list = versions
                .Where(v => v.StartsWith(major + ".", StringComparison.Ordinal))
                .Select(v => (v, parsed: Version.TryParse(v, out var p) ? p : null))
                .Where(x => x.parsed != null)
                .OrderByDescending(x => x.parsed)
                .ToList();
            return list.Count > 0 ? list[0].v : null;
        }

        return (Best("12"), Best("13"));
    }

    public sealed record OpenClRuntimeInfo(string? Version, int Vendors);

    /// <summary>Version på systemets OpenCL-ICD och antal registrerade plattformar.</summary>
    public static OpenClRuntimeInfo GetOpenClRuntime()
    {
        string? version = null;
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "OpenCL.dll");
            if (File.Exists(path)) version = FileVersionInfo.GetVersionInfo(path).FileVersion;
        }
        catch
        {
            // ignorera
        }

        int vendors = 0;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Khronos\OpenCL\Vendors");
            vendors = key?.ValueCount ?? 0;
        }
        catch
        {
            // ignorera
        }

        return new OpenClRuntimeInfo(version, vendors);
    }

    public static string? InstalledVulkanRuntime()
    {
        try
        {
            var path = Path.Combine(Environment.SystemDirectory, "vulkan-1.dll");
            if (!File.Exists(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion ?? info.ProductVersion;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Laddar ner en fil och rapporterar procent (0-100).</summary>
    public static async Task DownloadAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);

        var buffer = new byte[1 << 20];
        long read = 0;
        double last = -1;
        while (true)
        {
            int n;
            // Egen tidsgräns per läsning: står det helt still i två minuter är det
            // bättre att avbryta med ett begripligt fel än att hänga i 30 minuter.
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    n = await src.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException("Nedladdningen stannade – inget data kom på två minuter. Kontrollera nätverket och försök igen.");
                }
            }

            if (n <= 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0)
            {
                var pct = 100.0 * read / total;
                if (pct - last >= 1)
                {
                    last = pct;
                    progress?.Report(pct);
                }
            }
        }
        progress?.Report(100);
    }

    /// <summary>Som <see cref="DownloadAsync"/> men försöker om vid tillfälliga nätverksfel.</summary>
    public static async Task DownloadWithRetryAsync(string url, string dest, IProgress<double>? progress,
        CancellationToken ct, int attempts = 3)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadAsync(url, dest, progress, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException) when (attempt < attempts)
            {
                await Task.Delay(1500, ct);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await Task.Delay(1500, ct);
            }
        }
    }

    public static string TempFile(string name) =>
        Path.Combine(Path.GetTempPath(), "LlamaDeck", name);
}
