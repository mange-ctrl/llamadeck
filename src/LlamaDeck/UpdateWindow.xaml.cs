using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LlamaDeck.Core;

namespace LlamaDeck;

public partial class UpdateWindow : Window
{
    private sealed class RowUi
    {
        public TextBlock Status = null!;
        public Button Button = null!;
        public ProgressBar Bar = null!;
        public TextBlock Title = null!;
    }

    private readonly AppConfig _cfg;
    private readonly LogBus _log;
    private readonly bool _serverRunning;
    private readonly Dictionary<string, RowUi> _rows = new();
    private readonly CancellationTokenSource _cts = new();

    private LlamaRelease? _release;
    private string? _vulkanLatest;
    private (string? Cuda12, string? Cuda13) _cudaLatest;
    private string? _driver;
    private string? _cudaRuntime;
    private bool _busy;

    public UpdateWindow(AppConfig cfg, LogBus log, bool serverRunning)
    {
        _cfg = cfg;
        _log = log;
        _serverRunning = serverRunning;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            try { _cts.Cancel(); } catch { }
        };
    }

    private void Write(string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        UpdateLog.AppendText(line + Environment.NewLine);
        UpdateLog.ScrollToEnd();
        _log.Info("[uppdatering] " + text);

        // Skriv alltid uppdateringsloggen till fil – annars går felet förlorat om
        // "spara logg till fil" är avstängt i huvudfönstret.
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "update.log"), line + Environment.NewLine);
        }
        catch
        {
            // kan inte skriva – strunt samma, UI-loggen finns kvar
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TimeoutException t => t.Message,
        TaskCanceledException => "Nedladdningen avbröts (tidsgräns). Nätverket kan vara långsamt – försök igen.",
        UnauthorizedAccessException u => "Åtkomst nekad: " + u.Message,
        IOException io => io.Message,
        _ => ex.Message,
    };

    private async Task RefreshAsync()
    {
        StatusText.Text = "Söker efter uppdateringar …";
        _release = null;

        try
        {
            _release = await UpdateService.GetLatestLlamaAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Write("Kunde inte hämta llama.cpp-versioner: " + ex.Message);
        }

        try
        {
            _vulkanLatest = await UpdateService.GetLatestVulkanAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Write("Kunde inte hämta Vulkan-version: " + ex.Message);
        }

        try
        {
            _cudaLatest = await UpdateService.GetLatestCudaToolkitAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Write("Kunde inte hämta CUDA-versioner: " + ex.Message);
        }

        try
        {
            var (cuda, driver) = await GpuProbe.NvidiaInfoAsync(_cts.Token);
            _cudaRuntime = cuda;
            _driver = driver;
        }
        catch
        {
            // ingen NVIDIA
        }

        BuildRows();
        StatusText.Text = _release == null && _vulkanLatest == null
            ? "Kunde inte nå uppdateringsservern (kontrollera internetanslutningen)."
            : "Klart. Välj vad du vill uppdatera.";
    }

    private void BuildRows()
    {
        RowsPanel.Children.Clear();
        _rows.Clear();

        var tags = InstalledTags();
        var current = tags.Count == 0 ? "—" : string.Join(", ", tags.Select(kv => $"{kv.Key} {kv.Value}"));
        var latest = _release?.Tag ?? "okänd";

        string FetchLabel(BackendKind kind)
        {
            var v = _release?.VariantAsset(kind);
            if (v == null) return "Hämta";
            long total = v.Size + (_release!.CudartAsset(kind)?.Size ?? 0);
            return $"Hämta ({Fmt.Size(total)})";
        }

        void DisableIfMissing(BackendKind kind, string id, string label = "Ingen build")
        {
            if (_release?.VariantAsset(kind) != null) return;
            _rows[id].Button.IsEnabled = false;
            _rows[id].Button.Content = label;
        }

        AddRow("llama.cpp", "Serverbinärer för llama.cpp (alla installerade varianter).",
            $"Installerad: {current}   ·   Senaste: {latest}",
            "Uppdatera", "llama", UpdateAllAsync);
        DisableIfMissing(BackendKind.Cpu, "llama");

        AddRow("CUDA 12-backend", "llama.cpp byggd med CUDA 12.4 + NVIDIA:s runtime (kräver NVIDIA-kort).",
            BackendStatus(BackendKind.Cuda12, _cudaLatest.Cuda12, "CUDA 12"),
            BackendManager.IsInstalled(BackendKind.Cuda12) ? "Uppdatera" : FetchLabel(BackendKind.Cuda12),
            "cuda12", id => InstallVariantAsync(BackendKind.Cuda12, id));
        DisableIfMissing(BackendKind.Cuda12, "cuda12");

        AddRow("CUDA 13-backend", "llama.cpp byggd med CUDA 13.4 + NVIDIA:s runtime (nyare drivrutin).",
            BackendStatus(BackendKind.Cuda13, _cudaLatest.Cuda13, "CUDA 13"),
            BackendManager.IsInstalled(BackendKind.Cuda13) ? "Uppdatera" : FetchLabel(BackendKind.Cuda13),
            "cuda13", id => InstallVariantAsync(BackendKind.Cuda13, id));
        DisableIfMissing(BackendKind.Cuda13, "cuda13");

        AddRow("ROCm-backend", "llama.cpp med ROCm/HIP för AMD-grafik (kräver AMD-drivrutin med HIP-stöd).",
            BackendStatus(BackendKind.Rocm, null, "ROCm"),
            BackendManager.IsInstalled(BackendKind.Rocm) ? "Uppdatera" : FetchLabel(BackendKind.Rocm),
            "rocm", id => InstallVariantAsync(BackendKind.Rocm, id));
        DisableIfMissing(BackendKind.Rocm, "rocm");

        AddRow("SYCL-backend", "llama.cpp med SYCL för Intel Arc och Intel-grafik (kräver Intel-drivrutin).",
            BackendStatus(BackendKind.Sycl, null, "SYCL"),
            BackendManager.IsInstalled(BackendKind.Sycl) ? "Uppdatera" : FetchLabel(BackendKind.Sycl),
            "sycl", id => InstallVariantAsync(BackendKind.Sycl, id));
        DisableIfMissing(BackendKind.Sycl, "sycl");

        AddRow("Vulkan-backend", "llama.cpp med Vulkan – fungerar på NVIDIA, AMD och Intel.",
            BackendStatus(BackendKind.Vulkan, null, "Vulkan"),
            BackendManager.IsInstalled(BackendKind.Vulkan) ? "Uppdatera" : FetchLabel(BackendKind.Vulkan),
            "vulkan", id => InstallVariantAsync(BackendKind.Vulkan, id));
        DisableIfMissing(BackendKind.Vulkan, "vulkan");

        var openClAsset = _release?.VariantAsset(BackendKind.OpenCl);
        AddRow("OpenCL-backend",
            "llama.cpp med OpenCL. Officiella Windows-byggen finns bara för ARM64/Adreno – x64-varianten byggs från llama.cpp-källkoden.",
            OpenClStatus(openClAsset),
            openClAsset == null ? "Ingen x64-build" : (BackendManager.IsInstalled(BackendKind.OpenCl) ? "Uppdatera" : "Hämta"),
            "opencl", id => InstallVariantAsync(BackendKind.OpenCl, id));
        if (openClAsset == null) _rows["opencl"].Button.IsEnabled = false;

        var runtime = UpdateService.InstalledVulkanRuntime();
        AddRow("Vulkan-runtime", "Systemets Vulkan-drivrutin (vulkan-1.dll). Uppdateras via LunarG.",
            $"Installerad: {runtime ?? "hittades inte"}   ·   Senaste SDK: {_vulkanLatest ?? "okänd"}",
            "Öppna LunarG", "vkrt", _ => { PathUtil.OpenUrl("https://vulkan.lunarg.com/sdk/home#windows"); return Task.CompletedTask; });

        AddRow("NVIDIA-drivrutin", "Grafikdrivrutin och CUDA-stöd. Hämtas från NVIDIA (kräver ofta omstart).",
            $"Installerad: {_driver ?? "hittades inte"}   ·   CUDA-stöd: {_cudaRuntime ?? "okänt"}   ·   Senaste toolkit: {_cudaLatest.Cuda13 ?? _cudaLatest.Cuda12 ?? "okänd"}",
            "Öppna NVIDIA", "nv", _ => { PathUtil.OpenUrl("https://www.nvidia.com/Download/index.aspx"); return Task.CompletedTask; });

        foreach (var row in _rows.Values)
            row.Bar.Visibility = Visibility.Collapsed;
    }

    private string BackendStatus(BackendKind kind, string? latestToolkit, string label, bool includeRuntime = true)
    {
        var ver = BackendManager.ReadVersion(kind);
        var installed = BackendManager.IsInstalled(kind);
        var parts = new List<string>
        {
            installed ? $"Installerad: {ver?.Tag ?? "okänd"}" : "Ej installerad",
        };
        if (includeRuntime)
            parts.Add($"LLM-bygge: {(_release?.Tag ?? "okänd")}");
        if (latestToolkit != null) parts.Add($"NVIDIA toolkit: {latestToolkit}");
        if (installed)
            parts.Add(Fmt.Size(BackendManager.FolderSize(BackendManager.FolderFor(kind))));
        return string.Join("   ·   ", parts);
    }

    private string OpenClStatus(ReleaseAsset? asset)
    {
        var ver = BackendManager.ReadVersion(BackendKind.OpenCl);
        var installed = BackendManager.IsInstalled(BackendKind.OpenCl);
        var rt = UpdateService.GetOpenClRuntime();
        var parts = new List<string>
        {
            installed ? $"Installerad: {ver?.Tag ?? "inbyggd"}" : "Ej installerad",
            $"OpenCL-runtime: {rt.Version ?? "saknas"}{(rt.Vendors > 0 ? $" ({rt.Vendors} plattformar)" : "")}",
            _release == null ? "llama.cpp: okänd" : $"llama.cpp senaste: {_release.Tag}",
        };
        if (asset == null) parts.Add("inget officiellt x64-bygge – inbyggd variant används");
        return string.Join("   ·   ", parts);
    }

    private void AddRow(string title, string description, string status, string actionLabel, string id,
        Func<string, Task> action)
    {
        var statusText = new TextBlock
        {
            Text = status,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        var bar = new ProgressBar
        {
            Style = (Style)FindResource("Meter"),
            Maximum = 100,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed,
        };

        var button = new Button
        {
            Content = actionLabel,
            Style = (Style)FindResource("SubtleButton"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(16, 0, 0, 0),
            MinWidth = 110,
        };

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(titleText);
        left.Children.Add(new TextBlock
        {
            Text = description,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        });
        left.Children.Add(statusText);

        Grid.SetColumn(button, 1);
        grid.Children.Add(left);
        grid.Children.Add(button);

        var content = new StackPanel();
        content.Children.Add(grid);
        content.Children.Add(bar);

        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(14),
        };
        card.Child = content;
        RowsPanel.Children.Add(card);

        _rows[id] = new RowUi { Status = statusText, Button = button, Bar = bar, Title = titleText };
        button.Click += async (_, _) => await Run(id, action);
    }

    private async Task Run(string id, Func<string, Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        var row = _rows[id];
        row.Bar.Visibility = Visibility.Visible;
        row.Bar.IsIndeterminate = true;
        row.Bar.Value = 0;
        try
        {
            await action(id);
        }
        catch (OperationCanceledException)
        {
            Write("Avbrutet.");
        }
        catch (Exception ex)
        {
            var msg = Describe(ex);
            Write("Fel: " + msg);
            row.Status.Text = "Fel: " + msg;
        }
        finally
        {
            row.Bar.IsIndeterminate = false;
            row.Bar.Visibility = Visibility.Collapsed;
            _busy = false;
            SetButtonsEnabled(true);
            StatusText.Text = "Klart.";
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        foreach (var row in _rows.Values) row.Button.IsEnabled = enabled;
        RefreshBtn.IsEnabled = enabled;
    }

    private Dictionary<string, string> InstalledTags()
    {
        var dict = new Dictionary<string, string>();
        foreach (var kind in new[] { BackendKind.Cpu, BackendKind.Vulkan, BackendKind.Cuda12, BackendKind.Cuda13, BackendKind.Rocm, BackendKind.Sycl, BackendKind.OpenCl })
        {
            if (!BackendManager.IsInstalled(kind)) continue;
            dict[AppConfig.BackendName(kind)] = BackendManager.ReadVersion(kind)?.Tag ?? "?";
        }
        return dict;
    }

    private async Task UpdateAllAsync(string id)
    {
        if (_release == null)
        {
            Write("Ingen llama.cpp-release hittades.");
            return;
        }

        var kinds = new[] { BackendKind.Cpu, BackendKind.Vulkan, BackendKind.Cuda12, BackendKind.Cuda13, BackendKind.Rocm, BackendKind.Sycl, BackendKind.OpenCl }
            .Where(BackendManager.IsInstalled)
            .Where(k => _release.VariantAsset(k) != null)
            .ToList();
        if (kinds.Count == 0) kinds.Add(BackendKind.Cpu);

        foreach (var kind in kinds)
        {
            await InstallVariantCoreAsync(kind, id);
        }

        BuildRows();
        Write($"llama.cpp uppdaterad till {_release.Tag}.");
    }

    private Task InstallVariantAsync(BackendKind kind, string id) => InstallVariantCoreAsync(kind, id);

    private async Task InstallVariantCoreAsync(BackendKind kind, string rowId)
    {
        if (_release == null)
        {
            Write("Ingen release hittades – kan inte ladda ner.");
            return;
        }
        if (_serverRunning)
        {
            Write("Stoppa servern i huvudfönstret innan du uppdaterar en backend som används.");
            return;
        }

        var asset = _release.VariantAsset(kind);
        if (asset == null)
        {
            Write($"Hittar ingen Windows-fil för {AppConfig.BackendName(kind)} i {_release.Tag}.");
            return;
        }

        var cudartAsset = _release.CudartAsset(kind);
        var row = _rows[rowId];
        var tmpDir = AppPaths.DownloadsDir;
        Directory.CreateDirectory(tmpDir);

        Write($"Laddar ner {asset.Name} ({Fmt.Size(asset.Size)}) …");
        row.Status.Text = $"Laddar ner {asset.Name} …";

        var mainZip = Path.Combine(tmpDir, asset.Name);
        var mainProgress = new Progress<double>(p =>
        {
            row.Bar.IsIndeterminate = false;
            row.Bar.Value = cudartAsset == null ? p : p * 0.7;
        });
        await UpdateService.DownloadWithRetryAsync(asset.Url, mainZip, mainProgress, _cts.Token);

        string? cudartZip = null;
        if (cudartAsset != null)
        {
            Write($"Laddar ner {cudartAsset.Name} ({Fmt.Size(cudartAsset.Size)}) …");
            row.Status.Text = $"Laddar ner CUDA-runtime ({Fmt.Size(cudartAsset.Size)}) …";
            cudartZip = Path.Combine(tmpDir, cudartAsset.Name);
            var cudartProgress = new Progress<double>(p =>
            {
                row.Bar.IsIndeterminate = false;
                row.Bar.Value = 70 + p * 0.2;
            });
            await UpdateService.DownloadWithRetryAsync(cudartAsset.Url, cudartZip, cudartProgress, _cts.Token);
        }

        row.Status.Text = "Installerar …";
        row.Bar.IsIndeterminate = true;

        await BackendManager.InstallFromZipAsync(kind, mainZip, cudartZip, _release.Tag, s =>
        {
            Write(s);
            row.Status.Text = s;
        }, _cts.Token);

        Write($"{AppConfig.BackendName(kind)} {_release.Tag} installerad.");
        _log.Success($"{AppConfig.BackendName(kind)} uppdaterad till {_release.Tag}.");

        try
        {
            File.Delete(mainZip);
            if (cudartZip != null) File.Delete(cudartZip);
        }
        catch
        {
            // temp-filer städas av Windows senare
        }

        BuildRows();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await RefreshAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
