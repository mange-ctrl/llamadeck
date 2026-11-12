using System.Diagnostics;
using System.IO;

namespace LlamaDeck.Core;

/// <summary>
/// llama.cpp kan bara läsa GGUF. En mapp med <c>.safetensors</c> (Hugging Face-format) måste först
/// konverteras med llama.cpp-projektets Python-skript, och kan därefter kvantiseras med
/// <c>llama-quantize</c>. Den här klassen hittar verktygen och kör dem.
/// </summary>
public static class Converter
{
    /// <summary>Var konverteringsskripten ligger: &lt;data&gt;\converter\&lt;llama.cpp-tagg&gt;\</summary>
    public static string ToolchainRoot => Path.Combine(AppPaths.DataDir, "converter");

    /// <summary>Vilken llama.cpp-version skripten hör till – samma som den installerade backendens tagg.</summary>
    public static string PreferredTag
    {
        get
        {
            foreach (var kind in new[] { BackendKind.Cuda13, BackendKind.Cuda12, BackendKind.Vulkan, BackendKind.Cpu })
            {
                var tag = BackendManager.ReadVersion(kind)?.Tag;
                if (!string.IsNullOrWhiteSpace(tag)) return tag!;
            }
            return "b12230";
        }
    }

    public sealed class Toolchain
    {
        public string Directory { get; init; } = "";
        public string Python { get; init; } = "";
        public string Script { get; init; } = "";
        public string Tag { get; init; } = "";

        public bool Ready => !string.IsNullOrEmpty(Python) && File.Exists(Script);
        public string Describe
        {
            get
            {
                if (!File.Exists(Script))
                    return $"Konverteringsskripten saknas i {Directory}. Hämta dem från llama.cpp " +
                           $"och lägg i den mappen (convert_hf_to_gguf.py + conversion\\ + gguf-py\\).";
                if (string.IsNullOrEmpty(Python))
                    return "Hittade inget Python. Konvertering kräver Python 3 med torch, transformers " +
                           "och gguf installerade.";
                return $"Redo: {Path.GetFileName(Python)} · llama.cpp {Tag}";
            }
        }
    }

    private static Toolchain? _cached;

    /// <summary>Letar upp skripten och ett användbart Python. Resultatet cachas.</summary>
    public static Toolchain Detect(bool refresh = false)
    {
        if (_cached != null && !refresh) return _cached;

        var tag = PreferredTag;
        var dir = Path.Combine(ToolchainRoot, tag);
        var script = Path.Combine(dir, "convert_hf_to_gguf.py");
        if (!File.Exists(script))
        {
            // Fall tillbaka på valfri installerad version av skripten.
            try
            {
                if (Directory.Exists(ToolchainRoot))
                {
                    var alt = Directory.EnumerateDirectories(ToolchainRoot)
                        .FirstOrDefault(d => File.Exists(Path.Combine(d, "convert_hf_to_gguf.py")));
                    if (alt != null)
                    {
                        dir = alt;
                        script = Path.Combine(dir, "convert_hf_to_gguf.py");
                        tag = Path.GetFileName(alt);
                    }
                }
            }
            catch
            {
                // ignorera
            }
        }

        _cached = new Toolchain { Directory = dir, Script = script, Tag = tag, Python = FindPython() };
        return _cached;
    }

    /// <summary>Hittar ett Python som faktiskt kan importera torch och gguf.</summary>
    public static string FindPython()
    {
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo(candidate, "-c \"import torch, gguf; print('ok')\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) continue;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(15000);
                if (p.HasExited && p.ExitCode == 0 && output.Contains("ok")) return candidate;
            }
            catch
            {
                // prova nästa
            }
        }
        return "";
    }

    /// <summary>Söker igenom en mapp (och dess undermappar) efter .safetensors-vikter.</summary>
    public static (int Files, long Bytes) InspectSafetensors(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return (0, 0);
            var files = Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count == 0)
                files = Directory.EnumerateFiles(folder, "*.safetensors", SearchOption.AllDirectories).ToList();
            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return (files.Count, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    public sealed class Result
    {
        public bool Ok { get; set; }
        public string OutputGguf { get; set; } = "";
        public string Error { get; set; } = "";
    }

    /// <summary>
    /// Konverterar en mapp med safetensors till GGUF och kvantiserar den sedan.
    /// <paramref name="quant"/> tom eller "f16" betyder att mellanfilen behålls som den är.
    /// </summary>
    public static async Task<Result> ConvertAsync(string sourceFolder, string outDir,
        string quant, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new Result();
        var tc = Detect(refresh: true);
        if (!tc.Ready)
        {
            result.Error = tc.Describe;
            return result;
        }

        Directory.CreateDirectory(outDir);
        var name = new DirectoryInfo(sourceFolder.TrimEnd('\\', '/')).Name;
        var f16Path = Path.Combine(outDir, $"{name}-f16.gguf");

        progress?.Report($"Konverterar {name} till GGUF (f16) … det kan ta flera minuter.");
        var args = $"\"{tc.Script}\" \"{sourceFolder}\" --outtype f16 --outfile \"{f16Path}\"";
        var (code, tail) = await RunAsync(tc.Python, args, tc.Directory, progress, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(f16Path))
        {
            result.Error = $"Konverteringen misslyckades (kod {code}).\n{tail}";
            return result;
        }

        progress?.Report($"Klar: {Path.GetFileName(f16Path)} ({Fmt.Size(new FileInfo(f16Path).Length)}).");

        if (string.IsNullOrWhiteSpace(quant) || quant.Equals("f16", StringComparison.OrdinalIgnoreCase))
        {
            result.Ok = true;
            result.OutputGguf = f16Path;
            return result;
        }

        var quantizeExe = Path.Combine(BackendManager.FolderFor(BackendKind.Cuda13), "llama-quantize.exe");
        if (!File.Exists(quantizeExe))
            quantizeExe = Path.Combine(BackendManager.FolderFor(BackendKind.Cpu), "llama-quantize.exe");
        if (!File.Exists(quantizeExe))
        {
            result.Ok = true;
            result.OutputGguf = f16Path;
            result.Error = "llama-quantize.exe hittades inte – f16-filen behålls.";
            return result;
        }

        var finalPath = Path.Combine(outDir, $"{name}-{quant}.gguf");
        if (File.Exists(finalPath)) File.Delete(finalPath);
        progress?.Report($"Kvantiserar till {quant} …");
        var (qcode, qtail) = await RunAsync(quantizeExe, $"\"{f16Path}\" \"{finalPath}\" {quant}", Path.GetDirectoryName(quantizeExe)!, progress, ct).ConfigureAwait(false);
        if (qcode != 0 || !File.Exists(finalPath))
        {
            result.Ok = true;
            result.OutputGguf = f16Path;
            result.Error = $"Kvantiseringen misslyckades (kod {qcode}); f16-filen behålls.\n{qtail}";
            return result;
        }

        try { File.Delete(f16Path); } catch { }
        progress?.Report($"Klar: {Path.GetFileName(finalPath)} ({Fmt.Size(new FileInfo(finalPath).Length)}).");
        result.Ok = true;
        result.OutputGguf = finalPath;
        return result;
    }

    /// <summary>Kör ett kommando, strömmar utdata till loggen och returnerar slutkod + sista raderna.</summary>
    private static async Task<(int Code, string Tail)> RunAsync(string exe, string args, string workingDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";

        var tail = new Queue<string>();
        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void Take(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (tail)
            {
                tail.Enqueue(line);
                while (tail.Count > 12) tail.Dequeue();
            }
            progress?.Report(line);
        }

        p.OutputDataReceived += (_, e) => Take(e.Data);
        p.ErrorDataReceived += (_, e) => Take(e.Data);

        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }

        string joined;
        lock (tail) joined = string.Join(Environment.NewLine, tail);
        return (p.ExitCode, joined);
    }
}
