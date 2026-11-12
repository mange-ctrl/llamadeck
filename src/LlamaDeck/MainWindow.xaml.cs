using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LlamaDeck.Core;

namespace LlamaDeck;

public sealed class LogItem
{    public string Time { get; }
    public string Message { get; }
    public LogLevel Level { get; }

    public LogItem(LogLevel level, string message)
    {
        Level = level;
        Message = message;
        Time = DateTime.Now.ToString("HH:mm:ss");
    }
}

/// <summary>En mmproj-fil (visionsprojektor) som hittats i modellmappen.</summary>
public sealed class MmprojEntry
{
    public string Path { get; }
    public string Name { get; }

    public MmprojEntry(string path, string name)
    {
        Path = path;
        Name = name;
    }

    public override string ToString() => Name;
}

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly LogBus _log;
    private readonly ServerRunner _server = new();
    private readonly SystemMonitor _monitor = new();
    private readonly ObservableCollection<ModelEntry> _models = new();
    private readonly ObservableCollection<LogItem> _logItems = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _metricsTimer;

    private List<GpuDevice> _devices = new();
    private ModelEntry? _selected;
    private ICollectionView? _logView;
    private bool _loading = true;
    private bool _backendsReady;
    private bool _metricsBusy;
    private long _lastTokens;
    private DateTime _lastMetricsAt = DateTime.MinValue;
    private double _tokPerSec;
    private int _restartAttempts;
    private string _logFilter = "";

    public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MainWindow(AppConfig cfg, LogBus log)
    {
        _cfg = cfg;
        _log = log;
        InitializeComponent();

        Title = "LlamaDeck";
        try
        {
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        }
        catch
        {
            // ikonen är valfri
        }

        _server.Log += (level, msg) => _log.Write(level, msg);
        _server.StateChanged += OnServerState;
        _server.Exited += OnServerExited;
        _monitor.Sampled += OnSample;
        _log.Line += OnLogLine;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _cfg.Save(); };

        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _metricsTimer.Tick += MetricsTick;

        _logView = CollectionViewSource.GetDefaultView(_logItems);
        _logView.Filter = o => _logFilter.Length == 0 ||
                               ((LogItem)o).Message.Contains(_logFilter, StringComparison.OrdinalIgnoreCase);
        LogList.ItemsSource = _logView;

        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.UseDarkTitleBar(hwnd);
        };
    }

    // ===================== Start =====================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Math.Max(MinWidth, _cfg.WindowWidth), Math.Max(MinWidth, work.Width - 24));
            Height = Math.Min(Math.Max(MinHeight, _cfg.WindowHeight), Math.Max(MinHeight, work.Height - 24));
            if (_cfg.WindowMaximized) WindowState = WindowState.Maximized;

            PopulateCtxCombo();
            PopulateBackendCombo();
            PopulateOffloadCombo();
            PopulateKvCombo();
            var profileCount = ModelProfiles.Reload();
            if (profileCount > 0)
                _log.Info($"{profileCount} modellprofiler inlästa från {ModelProfiles.FilePath}");
            ApplyConfigToUi();

            _log.SaveToFile = _cfg.SaveLogToFile;
            _log.Info($"LlamaDeck startad · {(AppPaths.Portable ? "portabelt läge" : "data i " + AppPaths.DataDir)}");
            _log.Info("Välj en .gguf-modell, kontrollera inställningarna och tryck Starta server.");
        }
        finally
        {
            _loading = false;
        }

        if (_cfg.FirstRun)
        {
            _cfg.FirstRun = false;
            _log.Info("Tips: Använd Uppdateringar för att hämta CUDA-, Vulkan- eller nyare llama.cpp-byggen.");
        }

        StatusDetail.Text = "Förbereder llama.cpp (första starten) …";
        _ = PrepareBackendsAsync();
        await RefreshDevicesAsync();
        await ScanModelsAsync();
        UpdateUiState();
        _monitor.Start();
        _metricsTimer.Start();

        if (_cfg.CheckUpdatesOnStart) _ = CheckUpdatesQuietAsync();

        Ready.TrySetResult(true);

        if (App.AutoStart)
        {
            await Task.Delay(400);
            StartBtn_Click(this, new RoutedEventArgs());
        }

        if (App.OpenUpdates)
        {
            await Task.Delay(500);
            UpdateBtn_Click(this, new RoutedEventArgs());
        }
        else if (App.OpenHelp)
        {
            await Task.Delay(500);
            HelpBtn_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>Om vald backend saknas (t.ex. CUDA vid första starten) – välj en som finns.</summary>
    private void EnsureUsableBackend()
    {
        if (BackendManager.IsInstalled(_cfg.Backend)) return;
        foreach (var candidate in new[] { BackendKind.Vulkan, BackendKind.Cpu })
        {
            if (!BackendManager.IsInstalled(candidate)) continue;
            _log.Info($"{AppConfig.BackendName(_cfg.Backend)} är inte installerad – använder {AppConfig.BackendName(candidate)} så länge.");
            _cfg.Backend = candidate;
            _cfg.Save();
            PopulateBackendCombo();
            return;
        }
    }

    private async Task PrepareBackendsAsync()
    {
        try
        {
            var extracted = await BackendManager.EnsureBundledAsync(m => Dispatcher.Invoke(() => _log.Info(m)), CancellationToken.None);
            EnsureUsableBackend();
            _backendsReady = true;
            if (extracted)
            {
                PopulateBackendCombo();
                await RefreshDevicesAsync();
                UpdateUiState();
            }
            if (_server.State == ServerState.Stopped && StatusDetail.Text.StartsWith("Förbereder", StringComparison.Ordinal))
                StatusDetail.Text = "Välj en modell och starta servern";
        }
        catch (Exception ex)
        {
            _backendsReady = true;
            _log.Warn($"Kunde inte packa upp inbäddade backends: {ex.Message}");
        }
    }

    // ===================== Config <-> UI =====================

    private void PopulateCtxCombo()
    {
        CtxCombo.Items.Clear();
        foreach (var v in new[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072 })
            CtxCombo.Items.Add(new ComboBoxItem { Content = v.ToString("N0"), Tag = v, Padding = new Thickness(12, 7, 12, 7) });

        if (!CtxCombo.Items.Cast<ComboBoxItem>().Any(i => (int)i.Tag == _cfg.ContextSize))
        {
            CtxCombo.Items.Add(new ComboBoxItem
            {
                Content = _cfg.ContextSize.ToString("N0"),
                Tag = _cfg.ContextSize,
                Padding = new Thickness(12, 7, 12, 7),
            });
        }
    }

    private void PopulateBackendCombo()
    {
        var keep = _cfg.Backend;
        BackendCombo.Items.Clear();
        foreach (var kind in new[] { BackendKind.Cuda12, BackendKind.Cuda13, BackendKind.Rocm, BackendKind.Sycl, BackendKind.Vulkan, BackendKind.OpenCl, BackendKind.Cpu })
        {
            var installed = BackendManager.IsInstalled(kind);
            var item = new ComboBoxItem
            {
                Content = AppConfig.BackendName(kind) + (installed ? "" : " (saknas)"),
                Tag = kind,
                Padding = new Thickness(12, 7, 12, 7),
                Opacity = installed ? 1.0 : 0.55,
            };
            BackendCombo.Items.Add(item);
            if (kind == keep) BackendCombo.SelectedItem = item;
        }
        if (BackendCombo.SelectedItem == null && BackendCombo.Items.Count > 0)
            BackendCombo.SelectedIndex = BackendCombo.Items.Count - 1;
    }

    private void PopulateOffloadCombo()
    {
        OffloadCombo.Items.Clear();
        OffloadCombo.Items.Add(new ComboBoxItem { Content = "Auto (GPU + RAM/SSD)", Tag = OffloadMode.Auto, Padding = new Thickness(12, 7, 12, 7) });
        OffloadCombo.Items.Add(new ComboBoxItem { Content = "Läs in allt i RAM", Tag = OffloadMode.ForceRam, Padding = new Thickness(12, 7, 12, 7) });
        OffloadCombo.Items.Add(new ComboBoxItem { Content = "Lås i RAM (mlock)", Tag = OffloadMode.LockRam, Padding = new Thickness(12, 7, 12, 7) });
        OffloadCombo.Items.Add(new ComboBoxItem { Content = "Endast CPU", Tag = OffloadMode.CpuOnly, Padding = new Thickness(12, 7, 12, 7) });
        foreach (ComboBoxItem item in OffloadCombo.Items)
            if ((OffloadMode)item.Tag == _cfg.Offload) OffloadCombo.SelectedItem = item;
    }

    private void PopulateKvCombo()
    {
        KvCombo.Items.Clear();
        foreach (var (label, value) in new[] { ("f16 (bäst kvalitet)", "f16"), ("q8_0 (halverar)", "q8_0"), ("q4_0 (minst)", "q4_0") })
            KvCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value, Padding = new Thickness(12, 7, 12, 7) });
        foreach (ComboBoxItem item in KvCombo.Items)
            if ((string)item.Tag == _cfg.KvCacheType) KvCombo.SelectedItem = item;
    }

    private void ApplyConfigToUi()
    {
        PortBox.Text = _cfg.Port.ToString();
        NetworkToggle.IsChecked = _cfg.HostMode == HostMode.Network;

        ThreadsSlider.Maximum = Math.Max(1, Environment.ProcessorCount);
        ThreadsSlider.Value = Math.Clamp(_cfg.Threads, 1, Math.Max(1, Environment.ProcessorCount));
        ThreadsValue.Text = $"{_cfg.Threads} av {Environment.ProcessorCount}";

        GpuAutoToggle.IsChecked = _cfg.GpuLayersAuto;
        GpuLayersSlider.Maximum = Math.Max(1, _selected?.Layers ?? 200);
        GpuLayersSlider.Value = Math.Clamp(_cfg.GpuLayers, 0, GpuLayersSlider.Maximum);

        FlashToggle.IsChecked = _cfg.FlashAttention != FlashMode.Off;

        VramReserveSlider.Value = Math.Clamp(_cfg.VramReserveMb, 0, 6144);
        VramReserveValue.Text = $"{_cfg.VramReserveMb} MB / GPU";

        OsReserveSlider.Maximum = Math.Max(1, (int)Native.GetRam().TotalGb);
        OsReserveSlider.Value = Math.Clamp(_cfg.OsReserveGb, 1, OsReserveSlider.Maximum);
        OsReserveValue.Text = $"{_cfg.OsReserveGb} GB";

        RamGuardToggle.IsChecked = _cfg.RamGuardEnabled;

        SaveLogToggle.IsChecked = _cfg.SaveLogToFile;
        AutoScrollToggle.IsChecked = _cfg.AutoScrollLog;

        ApiKeyBox.Text = _cfg.ApiKey;
        ExtraArgsBox.Text = _cfg.ExtraArgs;
        ParallelBox.Text = _cfg.ParallelSlots.ToString();

        MmprojToggle.IsChecked = _cfg.MmprojEnabled;
        MmprojUbatchBox.Text = _cfg.MmprojUbatch.ToString();
        SyncModeButtons();
        RefreshMmprojList();

        foreach (ComboBoxItem item in CtxCombo.Items)
            if ((int)item.Tag == _cfg.ContextSize) CtxCombo.SelectedItem = item;

        UpdateRamGuardHint();
        UpdateOffloadHint();
        UpdateEndpointDisplay();
        UpdateLockState();
        UpdateCommandPreview();
    }

    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ===================== Modeller =====================

    private async Task ScanModelsAsync()
    {
        var roots = _cfg.EffectiveModelFolders;
        var existing = roots.Where(Directory.Exists).ToList();
        ModelCountText.ToolTip = string.Join(Environment.NewLine, roots);

        if (existing.Count == 0)
        {
            ModelCountText.Text = roots.Count == 0
                ? "Ingen modellmapp vald."
                : $"Mappen finns inte: {string.Join(" ; ", roots)}";
            _models.Clear();
            return;
        }

        ModelCountText.Text = existing.Count > 1
            ? $"Söker i {existing.Count} mappar efter .gguf-filer …"
            : "Söker efter .gguf-filer …";
        List<ModelEntry> found;
        try
        {
            found = await ModelLibrary.ScanManyAsync(existing, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ModelCountText.Text = "Kunde inte läsa mappen.";
            _log.Warn($"Modellsökning misslyckades: {ex.Message}");
            found = new List<ModelEntry>();
        }

        _models.Clear();
        foreach (var m in found) _models.Add(m);
        ModelList.ItemsSource = _models;
        ModelCountText.Text = found.Count == 0
            ? "Inga .gguf-filer hittades i mapparna."
            : $"{found.Count} modeller hittade" + (existing.Count > 1 ? $" ({existing.Count} mappar)" : "");

        ModelEntry? pick = null;
        if (!string.IsNullOrEmpty(_cfg.LastModelPath))
            pick = _models.FirstOrDefault(m => string.Equals(m.Path, _cfg.LastModelPath, StringComparison.OrdinalIgnoreCase));
        pick ??= _models.FirstOrDefault();
        if (pick != null) ModelList.SelectedItem = pick;
        else
        {
            _selected = null;
            UpdateModelInfo();
        }

        if (found.Count > 0)
            _log.Info($"{found.Count} modeller hittade i {string.Join(" ; ", existing)}");
    }

    private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selected = ModelList.SelectedItem as ModelEntry;
        var refreshUi = false;
        if (_selected != null)
        {
            _cfg.LastModelPath = _selected.Path;
            GpuLayersSlider.Maximum = Math.Max(1, _selected.Layers);
            if (_cfg.GpuLayers > _selected.Layers) _cfg.GpuLayers = _selected.Layers;

            // 1) Testad/provad inställning för just den här modellen vinner över allt annat.
            var profile = ModelProfiles.For(_selected.Path);
            if (profile != null && ModelProfiles.Apply(profile, _cfg))
            {
                refreshUi = true;
                ShowAutoTuneHint($"{(_cfg.AutoTune ? "Profil" : "Manuell · profil")}: {DescribeSettings(_cfg)}",
                                 muted: !_cfg.AutoTune);
                _log.Info($"Profil använd för {_selected.FileName}" +
                          (string.IsNullOrWhiteSpace(profile.Note) ? "" : $": {profile.Note}"));
            }
            else if (_cfg.AutoTune)
            {
                // 2) Auto: räkna fram inställningarna ur GGUF-filen och hårdvaran.
                ApplyAutoTune(log: true);
                refreshUi = true;
            }
            else
            {
                ShowAutoTuneHint("Manuell: du ställer in allt själv. Ändringar sparas per modell.", muted: true);
            }
            ScheduleSave();
        }
        if (refreshUi) ApplyConfigToUi();
        UpdateModelInfo();
        UpdateEstimates();
        UpdateMoePanel();
        UpdateClusterSummary();
        RefreshMmprojList();
        UpdateCommandPreview();
        UpdateUiState();
    }

    // ===================== Auto-inställning =====================

    private static string DescribeSettings(AppConfig c)
    {
        var parts = new List<string> { $"kontext {c.ContextSize:N0}" };
        parts.Add(c.GpuLayersAuto
            ? "GPU-lager auto"
            : c.GpuLayers >= 99 ? "alla lager på GPU" : $"{c.GpuLayers} lager på GPU");
        if (c.MoeMode == MoEMode.AllCpu) parts.Add("experter på CPU");
        if (!string.Equals(c.KvCacheType, "f16", StringComparison.OrdinalIgnoreCase)) parts.Add($"KV {c.KvCacheType}");
        if (c.Offload == OffloadMode.ForceRam) parts.Add("allt i RAM");
        if (c.MmprojEnabled) parts.Add("bildstöd");
        return string.Join(" · ", parts);
    }

    private void ShowAutoTuneHint(string text, bool muted)
    {
        AutoTuneHint.Text = text;
        AutoTuneHint.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        AutoTuneHint.Opacity = muted ? 0.6 : 1.0;
    }

    /// <summary>Räknar fram och lägger in de inställningar som passar modellen bäst.</summary>
    private void ApplyAutoTune(bool log)
    {
        if (_selected == null) return;

        var freeMiB = _devices.Count > 0 ? _devices.Max(d => d.FreeMiB) : 0;
        var ram = Native.GetRam();
        var rotational = Native.DriveIncursSeekPenalty(_selected.Path);

        var rec = AutoTuner.Recommend(_selected, freeMiB, _cfg.VramReserveMb,
                                     Math.Max(1, _cfg.ParallelSlots), ram.TotalGb, ram.AvailableGb, rotational);

        // Kunde VRAM inte läsas av: låt llama.cpp räkna själv vid start i stället för att gissa.
        if (freeMiB <= 0)
        {
            rec.GpuLayersAuto = true;
            rec.GpuLayers = 99;
            rec.Reasons.Add("Kunde inte läsa ledigt VRAM – GPU-lagren får llama.cpp räkna ut vid start.");
        }

        _cfg.Threads = rec.Threads;
        _cfg.ContextSize = rec.ContextSize;
        _cfg.GpuLayersAuto = rec.GpuLayersAuto;
        _cfg.GpuLayers = rec.GpuLayers;
        _cfg.MoeMode = rec.MoeMode;
        _cfg.MoeCpuLayers = 0;
        _cfg.KvCacheType = rec.KvCacheType;
        _cfg.Offload = rec.Offload;
        _cfg.FlashAttention = rec.FlashAttention;

        var mmproj = AutoTuner.FindSiblingMmproj(_selected.Path);
        if (!string.IsNullOrEmpty(mmproj) && AutoTuner.SupportsVision(_selected.Arch))
        {
            rec.MmprojEnabled = true;
            rec.MmprojPath = mmproj;
            _cfg.MmprojEnabled = true;
            _cfg.MmprojPath = mmproj;
            rec.Reasons.Add($"Bildstöd hittat i {Path.GetFileName(mmproj)} och slås på automatiskt.");
        }

        ShowAutoTuneHint($"Auto: {rec.Summary}", muted: false);

        if (log)
        {
            _log.Info($"Auto-inställning för {_selected.FileName}: {rec.Summary}");
            foreach (var reason in rec.Reasons) _log.Info("   · " + reason);
        }
    }

    /// <summary>Växlar mellan Auto (LlamaDeck ställer in allt) och Manuell (du ställer in allt).</summary>
    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ToggleButton btn) return;
        SetAutoTune(string.Equals(btn.Tag as string, "Auto", StringComparison.OrdinalIgnoreCase));
    }

    private void SetAutoTune(bool auto)
    {
        _cfg.AutoTune = auto;
        SyncModeButtons();
        ScheduleSave();

        if (!auto)
        {
            ShowAutoTuneHint(_selected == null
                ? "Manuell: du ställer in allt själv."
                : "Manuell: du ställer in allt själv. Ändringar sparas per modell och används nästa gång.",
                muted: true);
            UpdateLockState();
            UpdateCommandPreview();
            _log.Info("Manuellt läge: inställningarna är fria att ändra.");
            return;
        }

        if (_selected == null)
        {
            ShowAutoTuneHint("Auto: välj en modell så ställs allt in automatiskt.", muted: true);
            UpdateLockState();
            return;
        }

        var profile = ModelProfiles.For(_selected.Path);
        if (profile != null && ModelProfiles.Apply(profile, _cfg))
        {
            ShowAutoTuneHint($"Profil: {DescribeSettings(_cfg)}", muted: false);
            _log.Info($"Profil använd för {_selected.FileName}");
        }
        else
        {
            ApplyAutoTune(log: true);
        }

        ApplyConfigToUi();
        UpdateCommandPreview();
    }

    private void SyncModeButtons()
    {
        var auto = _cfg.AutoTune;
        ModeAutoBtn.IsChecked = auto;
        ModeManualBtn.IsChecked = !auto;
    }

    private static void Lock(UIElement element, bool locked)
    {
        element.IsEnabled = !locked;
        element.Opacity = locked ? 0.42 : 1.0;
    }

    /// <summary>Gråar ut precis de inställningar som Auto-läget äger. Port, RAM-reserv, API-nyckel m.m. rörs inte.</summary>
    private void UpdateLockState()
    {
        var auto = _cfg.AutoTune;

        Lock(CtxCombo, auto);
        Lock(ThreadsSlider, auto);
        Lock(GpuAutoToggle, auto);
        Lock(GpuLayersSlider, auto || _cfg.GpuLayersAuto);
        Lock(MoeCombo, auto);
        Lock(MoeSlider, auto || _cfg.MoeMode != MoEMode.CpuLayers);
        Lock(KvCombo, auto);
        Lock(OffloadCombo, auto);
        Lock(FlashToggle, auto);
        Lock(MmprojToggle, auto);
        Lock(MmprojCombo, auto || !_cfg.MmprojEnabled);
        Lock(MmprojUbatchBox, auto || !_cfg.MmprojEnabled);

        ModeAutoBtn.ToolTip = auto
            ? "Valt: LlamaDeck läser modellen och ställer in allt själv. Inställningarna är låsta – välj Manuell för att ändra."
            : "LlamaDeck läser modellens GGUF-metadata, ledigt VRAM, RAM och disktyp och ställer in allt själv.";
    }

    // ===================== Bildstöd (mmproj) =====================

    private void RefreshMmprojList()
    {
        var previous = _cfg.MmprojPath;
        var items = new List<MmprojEntry>();
        try
        {
            foreach (var folder in _cfg.EffectiveModelFolders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.gguf", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileName(file);
                    if (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
                        items.Add(new MmprojEntry(file, name));
                }
            }
        }
        catch
        {
            // oläsbar mapp – listan blir tom, resten av appen fungerar ändå
        }

        items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        MmprojCombo.ItemsSource = items;

        // Föredra en mmproj som ligger i samma mapp som modellen, annars den sparade.
        var modelDir = _selected != null ? Path.GetDirectoryName(_selected.Path) : null;
        MmprojEntry? pick = null;
        if (!string.IsNullOrEmpty(modelDir))
            pick = items.FirstOrDefault(m => string.Equals(Path.GetDirectoryName(m.Path), modelDir, StringComparison.OrdinalIgnoreCase));
        pick ??= items.FirstOrDefault(m => string.Equals(m.Path, previous, StringComparison.OrdinalIgnoreCase));
        if (pick == null && items.Count == 1) pick = items[0];

        MmprojCombo.SelectedItem = pick;
        if (pick != null)
        {
            _cfg.MmprojPath = pick.Path;
            ScheduleSave();
        }
    }

    private void MmprojToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.MmprojEnabled = MmprojToggle.IsChecked == true;
        UpdateLockState();

        if (_cfg.MmprojEnabled && MmprojCombo.SelectedItem == null)
        {
            RefreshMmprojList();
            if (MmprojCombo.SelectedItem == null)
                _log.Warn("Bildstöd är på, men ingen mmproj-fil hittades i modellmappen.");
        }

        ScheduleSave();
        UpdateCommandPreview();
    }

    private void MmprojCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MmprojCombo.SelectedItem is not MmprojEntry entry) return;
        _cfg.MmprojPath = entry.Path;
        if (_loading) return;
        ScheduleSave();
        UpdateCommandPreview();
    }

    private void MmprojUbatchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(MmprojUbatchBox.Text, out var n) || n < 0 || n > 16384) return;
        _cfg.MmprojUbatch = n;
        ScheduleSave();
        UpdateCommandPreview();
    }

    /// <summary>
    /// Konverterar en mapp med .safetensors-vikter till GGUF. llama.cpp (och därmed LlamaDeck)
    /// kan bara läsa GGUF, så det här är vägen in för Hugging Face-modeller.
    /// </summary>
    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var tc = Converter.Detect(refresh: true);
        if (!tc.Ready)
        {
            _log.Warn(tc.Describe);
            MessageBox.Show(this,
                tc.Describe + "\n\nSkripten hämtas från llama.cpp-projektet:\n" +
                "https://github.com/ggml-org/llama.cpp/archive/refs/tags/" + tc.Tag + ".tar.gz\n\n" +
                "Packa upp convert_hf_to_gguf.py, conversion\\ och gguf-py\\ till:\n" + tc.Directory,
                "Konvertering", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var first = _cfg.EffectiveModelFolders.FirstOrDefault(Directory.Exists);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Välj mapp med .safetensors-vikter (config.json + *.safetensors)",
            InitialDirectory = first ?? AppPaths.ModelsDir,
            Multiselect = false,
        };
        if (dlg.ShowDialog(this) != true) return;

        var folder = dlg.FolderName;
        var (count, bytes) = Converter.InspectSafetensors(folder);
        if (count == 0)
        {
            _log.Warn($"Inga .safetensors-filer hittades i {folder}.");
            return;
        }
        if (!File.Exists(Path.Combine(folder, "config.json")))
        {
            _log.Warn("Mappen saknar config.json – konverteraren behöver den för att veta arkitekturen.");
            return;
        }

        var answer = MessageBox.Show(this,
            $"Hittade {count} .safetensors-filer ({Fmt.Size(bytes)}).\n\n" +
            "Kvantisera till Q4_K_M när konverteringen är klar?\n\n" +
            "Ja   = Q4_K_M (liten fil, bra kvalitet)\n" +
            "Nej  = behåll f16 (större fil, högst kvalitet)\n" +
            "Avbryt = inget händer",
            "Konvertera till GGUF", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return;

        var quant = answer == MessageBoxResult.Yes ? "Q4_K_M" : "f16";
        var outDir = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? folder;

        ConvertBtn.IsEnabled = false;
        _log.Info($"Konverterar {folder} till GGUF ({quant}). Det här kan ta allt från någon minut till en timme " +
                  "beroende på modellens storlek.");
        try
        {
            var progress = new Progress<string>(line => _log.Info("  " + line));
            var res = await Converter.ConvertAsync(folder, outDir, quant, progress, CancellationToken.None);

            if (res.Ok && !string.IsNullOrEmpty(res.OutputGguf))
            {
                _log.Info($"Klar: {res.OutputGguf}");
                if (!string.IsNullOrEmpty(res.Error)) _log.Warn(res.Error);
                await ScanModelsAsync();
                var pick = _models.FirstOrDefault(m =>
                    string.Equals(m.Path, res.OutputGguf, StringComparison.OrdinalIgnoreCase));
                if (pick != null)
                {
                    ModelList.SelectedItem = pick;
                    ModelList.ScrollIntoView(pick);
                }
                else
                {
                    _log.Info("Filen ligger utanför modellmapparna – lägg till dess mapp med Välj mapp.");
                }
            }
            else
            {
                _log.Error("Konverteringen misslyckades: " + res.Error);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Konverteringen kraschade: {ex.Message}");
        }
        finally
        {
            ConvertBtn.IsEnabled = true;
        }
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var first = _cfg.EffectiveModelFolders.FirstOrDefault(Directory.Exists);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Välj mapp med GGUF-modeller (Ctrl+klick för flera)",
            InitialDirectory = first ?? AppPaths.ModelsDir,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        var chosen = dlg.FolderNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.TrimEnd('\\', '/')).ToList();
        if (chosen.Count == 0) return;

        // Behåll de mappar som redan fungerar och lägg till de nya.
        var merged = _cfg.EffectiveModelFolders;
        foreach (var dir in chosen)
            if (!merged.Any(m => string.Equals(m, dir, StringComparison.OrdinalIgnoreCase)))
                merged.Add(dir);

        _cfg.ModelFolder = string.Join(";", merged);
        ScheduleSave();
        _log.Info($"Modellmappar: {string.Join(" ; ", merged)}");
        _ = ScanModelsAsync();
    }

    private void UpdateModelInfo()
    {
        if (_selected == null)
        {
            ModelTitle.Text = "Ingen modell vald";
            ModelMeta.Text = "Välj en .gguf-fil i listan";
            CtxHint.Text = "";
            return;
        }

        ModelTitle.Text = _selected.Title;
        ModelMeta.Text = _selected.Detail;
        CommandPreview.Text = BuildPlan().Preview;

        if (_selected.ContextLength > 0 && _cfg.ContextSize > _selected.ContextLength)
        {
            CtxHint.Text = $"Modellen är tränad för {_selected.ContextLength:N0} token – högre kontext kan ge sämre svar.";
            CtxHint.Foreground = (Brush)FindResource("AmberBrush");
            CtxHint.Visibility = Visibility.Visible;
        }
        else
        {
            CtxHint.Text = "";
            CtxHint.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== GPU =====================

    private async Task RefreshDevicesAsync()
    {
        var exe = BackendManager.ExeFor(_cfg.Backend);
        GpuPanel.Children.Clear();

        if (!BackendManager.IsInstalled(_cfg.Backend))
        {
            _devices = new List<GpuDevice>();
            GpuPanel.Children.Add(new TextBlock
            {
                Text = !_backendsReady
                    ? "Förbereder llama.cpp (första starten) …"
                    : $"{AppConfig.BackendName(_cfg.Backend)} är inte installerad än. Öppna Uppdateringar för att hämta den.",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("AmberBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            GpuSummary.Text = "—";
            UpdateGpuControlsEnabled();
            return;
        }

        try
        {
            _devices = await GpuProbe.ListDevicesAsync(exe);
        }
        catch
        {
            _devices = new List<GpuDevice>();
        }

        if (_cfg.SelectedGpus.Count == 0 && _devices.Count > 0)
            _cfg.SelectedGpus.Add(0);

        BuildGpuPanel();
        UpdateGpuControlsEnabled();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void BuildGpuPanel()
    {
        GpuPanel.Children.Clear();
        if (_devices.Count == 0)
        {
            var hint = _cfg.Backend switch
            {
                BackendKind.Cpu => "CPU-läge – ingen GPU används.",
                BackendKind.OpenCl => "OpenCL hittade ingen GPU. llama.cpp:s OpenCL-backend stöder Intel- och Adreno-grafik – på NVIDIA/AMD används CUDA eller Vulkan.",
                _ => "Inga GPU:er hittades för den här backend-varianten.",
            };
            GpuPanel.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
            });
            GpuSummary.Text = "";
            return;
        }

        foreach (var (device, index) in _devices.Select((d, i) => (d, i)))
        {
            var cb = new CheckBox
            {
                Content = $"{device.Name}  ·  {device.MemoryText}",
                Tag = index,
                IsChecked = _cfg.SelectedGpus.Contains(index),
                Margin = new Thickness(0, 0, 0, 5),
                FontSize = 11.5,
                IsEnabled = _cfg.Backend != BackendKind.Cpu,
            };
            cb.Checked += GpuCheckChanged;
            cb.Unchecked += GpuCheckChanged;
            GpuPanel.Children.Add(cb);
        }

        GpuSummary.Text = _cfg.SelectedGpus.Count > 0
            ? $"{_cfg.SelectedGpus.Count} av {_devices.Count} valda"
            : "välj minst en";
    }

    private void GpuCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var selected = new List<int>();
        foreach (var child in GpuPanel.Children)
            if (child is CheckBox { IsChecked: true, Tag: int idx }) selected.Add(idx);

        if (selected.Count > 4)
        {
            if (sender is CheckBox cb) cb.IsChecked = false;
            _log.Warn("Max 4 GPU:er kan användas samtidigt.");
            return;
        }

        _cfg.SelectedGpus = selected;
        GpuSummary.Text = $"{selected.Count} av {_devices.Count} valda";
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private List<GpuDevice> SelectedDevices()
    {
        if (_devices.Count == 0) return new List<GpuDevice>();
        var picked = _cfg.SelectedGpus.Where(i => i >= 0 && i < _devices.Count).Distinct().ToList();
        if (picked.Count == 0 && _devices.Count > 0) picked.Add(0);
        return picked.Select(i => _devices[i]).ToList();
    }

    private void UpdateGpuControlsEnabled()
    {
        var hasGpu = _cfg.Backend != BackendKind.Cpu && _devices.Count > 0;
        GpuAutoToggle.IsEnabled = hasGpu;
        GpuLayersSlider.IsEnabled = hasGpu && !_cfg.GpuLayersAuto;
        VramReserveSlider.IsEnabled = hasGpu;
    }

    private void UpdateEstimates()
    {
        if (_selected == null)
        {
            GpuEstimate.Text = "";
            return;
        }

        if (_cfg.Backend == BackendKind.Cpu || _cfg.Offload == OffloadMode.CpuOnly)
        {
            GpuEstimate.Text = "Kör på processorn. Modellen laddas från disk/RAM.";
            return;
        }

        var sel = SelectedDevices();
        if (sel.Count == 0)
        {
            GpuEstimate.Text = "Inga GPU:er valda – allt körs på CPU.";
            return;
        }

        var freeMb = sel.Sum(d => d.FreeMiB);
        if (_cfg.GpuLayersAuto)
        {
            var (layers, vram) = Planner.Suggest(_selected, freeMb, _cfg.VramReserveMb, _cfg.ContextSize,
                _cfg.KvCacheType, _cfg.ParallelSlots);
            var pct = _selected.Layers > 0 ? 100.0 * layers / _selected.Layers : 0;
            GpuEstimate.Text = $"Auto: ca {layers}/{_selected.Layers} lager på GPU ({pct:0} %) · ~{Fmt.Gb(vram)} VRAM av {Fmt.Gb(freeMb / 1024d)} ledigt.";
        }
        else
        {
            var perLayer = Planner.BytesPerLayer(_selected) / (1024d * 1024d * 1024d);
            var kv = Planner.EstimateKvGb(_selected, _cfg.ContextSize, _cfg.KvCacheType, _cfg.ParallelSlots);
            var vram = perLayer * _cfg.GpuLayers + kv + 0.45;
            GpuEstimate.Text = $"Manuellt: {_cfg.GpuLayers} lager · ~{Fmt.Gb(vram)} VRAM (varav KV {Fmt.Gb(kv)}).";
        }
    }

    // ===================== Server =====================

    private LaunchPlan BuildPlan()
    {
        var totalRamGb = Native.GetRam().TotalGb;
        long? limit = null;
        if (_cfg.RamGuardEnabled && totalRamGb > 1)
        {
            var limitGb = Math.Max(1.0, totalRamGb - _cfg.OsReserveGb);
            limit = (long)(limitGb * 1024 * 1024 * 1024);
        }

        var devices = SelectedDevices();
        var tensorSplit = _cfg.TensorSplit;
        if (string.IsNullOrWhiteSpace(tensorSplit) && devices.Count > 1)
        {
            var total = devices.Sum(d => (double)Math.Max(1, d.FreeMiB));
            tensorSplit = string.Join(",", devices.Select(d =>
                (Math.Max(1, d.FreeMiB) / total).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new LaunchPlan
        {
            Backend = _cfg.Backend,
            ExePath = BackendManager.ExeFor(_cfg.Backend),
            ModelPath = _selected?.Path ?? "",
            Host = _cfg.HostMode == HostMode.Network ? "0.0.0.0" : "127.0.0.1",
            Port = _cfg.Port,
            Threads = _cfg.Threads,
            Context = _cfg.ContextSize,
            Flash = _cfg.FlashAttention,
            KvType = _cfg.KvCacheType,
            GpuLayersAuto = _cfg.GpuLayersAuto,
            GpuLayers = _cfg.GpuLayers,
            Devices = devices.Select(d => d.Id).ToList(),
            TensorSplit = tensorSplit,
            Offload = _cfg.Offload,
            RamLimitBytes = limit,
            ApiKey = _cfg.ApiKey,
            Parallel = _cfg.ParallelSlots,
            VramReserveMb = _cfg.VramReserveMb,
            ExtraArgs = _cfg.ExtraArgs,
            MmprojPath = _cfg.MmprojEnabled ? _cfg.MmprojPath : "",
            Ubatch = _cfg.MmprojEnabled ? _cfg.MmprojUbatch : 0,
            RpcNodes = _cfg.RpcNodes.ToList(),
            RpcWeights = _cfg.RpcWeights,
            MoeMode = _cfg.MoeMode,
            MoeCpuLayers = _cfg.MoeCpuLayers,
        };
    }

    private void UpdateCommandPreview()
    {
        if (!IsLoaded) return;
        CommandPreview.Text = _selected == null ? "(välj en modell)" : BuildPlan().Preview;
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_server.IsRunning) return;
        _restartAttempts = 0;

        if (!_backendsReady)
        {
            _log.Info("Förbereder llama.cpp – vänta några sekunder och tryck igen.");
            return;
        }
        if (_selected == null)
        {
            _log.Warn("Välj en modell i listan först.");
            return;
        }
        if (!BackendManager.IsInstalled(_cfg.Backend))
        {
            _log.Warn($"{AppConfig.BackendName(_cfg.Backend)} är inte installerad. Öppna Uppdateringar och hämta den.");
            return;
        }
        if (!PortUtil.IsFree(_cfg.Port))
        {
            _log.Error($"Port {_cfg.Port} används redan av ett annat program. Välj en annan port.");
            return;
        }

        var plan = BuildPlan();
        CommandPreview.Text = plan.Preview;
        WarnIfContextTooBig(plan);
        // Kom ihåg vad som faktiskt startades, så att egna ändringar finns kvar nästa gång.
        ModelProfiles.Remember(_selected.Path, _cfg);
        StartBtn.IsEnabled = false;
        TestBtn.IsEnabled = false;

        var ok = await _server.StartAsync(plan);
        if (ok)
        {
            _monitor.SetProcess(_server.Process);
            _lastTokens = 0;
            _lastMetricsAt = DateTime.MinValue;
            _tokPerSec = 0;
        }
        else
        {
            StartBtn.IsEnabled = true;
        }
    }

    private void WarnIfContextTooBig(LaunchPlan plan)
    {
        if (_selected == null) return;
        if (plan.Backend == BackendKind.Cpu || plan.Offload == OffloadMode.CpuOnly) return;

        var sel = SelectedDevices();
        if (sel.Count == 0) return;

        var kv = Planner.EstimateKvGb(_selected, plan.Context, plan.KvType, plan.Parallel);
        if (kv <= 0) return;

        var freeGb = sel.Sum(d => d.FreeMiB) / 1024d;
        if (kv < freeGb * 0.7) return;

        _log.Warn($"Kontext {plan.Context:N0} ger ~{Fmt.Gb(kv)} GB KV-cache, men bara {freeGb:0.0} GB VRAM är ledigt. " +
                  "Modellen laddas då delvis på processorn och svarar mycket långsamt. Sänk kontexten till 8 192–32 768.");
    }

    private async void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        StopBtn.IsEnabled = false;
        await _server.StopAsync();
        _monitor.SetProcess(null);
    }

    private void OnServerState(ServerState state, string detail)
    {
        Dispatcher.BeginInvoke(() =>
        {
            StatusText.Text = state switch
            {
                ServerState.Stopped => "Stoppad",
                ServerState.Starting => "Startar",
                ServerState.Running => "Kör",
                ServerState.Stopping => "Stoppar",
                ServerState.Error => "Fel",
                _ => "Stoppad",
            };
            StatusDetail.Text = detail;
            StatusDot.Fill = (Brush)FindResource(state switch
            {
                ServerState.Running => "GreenBrush",
                ServerState.Starting or ServerState.Stopping => "AmberBrush",
                ServerState.Error => "RedBrush",
                _ => "MutedBrush",
            });

            StartBtn.IsEnabled = state is ServerState.Stopped or ServerState.Error;
            StopBtn.IsEnabled = state is ServerState.Starting or ServerState.Running;
            TestBtn.IsEnabled = state == ServerState.Running;
            UpdateEndpointDisplay();
            UpdateUiState();
        });
    }

    private void OnServerExited(int code)
    {
        Dispatcher.BeginInvoke(async () =>
        {
            _monitor.SetProcess(null);
            if (_cfg.AutoRestart && code != 0 && _restartAttempts < 3 && !IsClosing)
            {
                _restartAttempts++;
                _log.Warn($"Startar om servern om 3 sekunder (försök {_restartAttempts}/3) …");
                await Task.Delay(3000);
                if (!IsClosing) await StartServerAsyncInternal();
            }
        });
    }

    private bool IsClosing { get; set; }

    private async Task StartServerAsyncInternal()
    {
        if (_selected == null || !BackendManager.IsInstalled(_cfg.Backend)) return;
        var plan = BuildPlan();
        if (await _server.StartAsync(plan)) _monitor.SetProcess(_server.Process);
    }

    private void UpdateEndpointDisplay()
    {
        var host = _cfg.HostMode == HostMode.Network ? NetUtil.LocalIp() : "127.0.0.1";
        if (_server.IsRunning && _server.Endpoint != null)
            EndpointText.Text = _server.Endpoint;
        else
            EndpointText.Text = $"http://{host}:{_cfg.Port}";
    }

    private void UpdateUiState()
    {
        if (_selected == null)
        {
            ModelTitle.Text = "Ingen modell vald";
            ModelMeta.Text = "Välj en .gguf-fil i listan";
        }
        UpdateEndpointDisplay();
        StartBtn.IsEnabled = _backendsReady && !_server.IsRunning && _selected != null;
    }

    private void CopyEndpoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(EndpointText.Text);
            _log.Info($"Kopierade {EndpointText.Text}");
        }
        catch
        {
            _log.Warn("Kunde inte kopiera till urklipp.");
        }
    }

    private void OpenEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var url = _server.IsRunning ? EndpointText.Text : $"http://127.0.0.1:{_cfg.Port}";
        PathUtil.OpenUrl(url);
    }

    // ===================== Kluster och MoE =====================

    private void Cluster_Click(object sender, RoutedEventArgs e)
    {
        var window = new ClusterWindow(_cfg) { Owner = this };
        if (window.ShowDialog() != true) return;

        UpdateClusterSummary();
        UpdateCommandPreview();
        _log.Info(_cfg.RpcNodes.Count == 0
            ? "Kluster av: inga RPC-noder."
            : $"Kluster på: {string.Join(", ", _cfg.RpcNodes)}");
    }

    private void UpdateClusterSummary()
    {
        if (ClusterSummary == null) return;
        ClusterSummary.Text = _cfg.RpcNodes.Count == 0
            ? "Inga noder – servern kör bara på den här datorn."
            : $"{_cfg.RpcNodes.Count} nod(er): {string.Join(", ", _cfg.RpcNodes)}";
    }

    private void UpdateMoePanel()
    {
        if (MoeCard == null) return;

        var isMoe = _selected?.IsMoE == true;
        MoeCard.Visibility = isMoe ? Visibility.Visible : Visibility.Collapsed;
        if (!isMoe) return;

        MoeHint.Text = "Modellen använder experter (MoE). Läggs expertvikterna på processorn frigörs VRAM åt fler lager och större KV-cache.";
        MoeSlider.Maximum = Math.Max(1, _selected!.Layers);

        _loading = true;
        MoeCombo.SelectedIndex = _cfg.MoeMode switch
        {
            MoEMode.CpuLayers => 1,
            MoEMode.AllCpu => 2,
            _ => 0,
        };
        MoeSlider.Value = Math.Clamp(_cfg.MoeCpuLayers, 0, (int)MoeSlider.Maximum);
        _loading = false;

        UpdateMoeUi();
    }

    private void UpdateMoeUi()
    {
        if (MoeCard == null || MoeCard.Visibility != Visibility.Visible) return;

        var mode = MoeCombo.SelectedIndex;
        MoeSlider.IsEnabled = mode == 1;
        MoeValue.Text = mode switch
        {
            1 => $"{_cfg.MoeCpuLayers} av {_selected?.Layers ?? 0} lager får experterna på CPU (--n-cpu-moe).",
            2 => "Alla experter på CPU (--cpu-moe). Rekommenderas när modellen är större än VRAM.",
            _ => "Auto: llama.cpp lägger så mycket som får plats i VRAM.",
        };
    }

    private void MoeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || MoeCard == null) return;

        _cfg.MoeMode = MoeCombo.SelectedIndex switch
        {
            1 => MoEMode.CpuLayers,
            2 => MoEMode.AllCpu,
            _ => MoEMode.Auto,
        };
        ScheduleSave();
        UpdateMoeUi();
        UpdateCommandPreview();
    }

    private void MoeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || MoeCard == null) return;

        _cfg.MoeCpuLayers = (int)Math.Round(e.NewValue);
        if (_cfg.MoeMode == MoEMode.CpuLayers)
        {
            ScheduleSave();
            UpdateMoeUi();
            UpdateCommandPreview();
        }
    }

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_server.IsRunning) return;
        TestBtn.IsEnabled = false;
        _log.Info("Testar servern …");
        var answer = await ServerClient.ChatAsync($"http://127.0.0.1:{_cfg.Port}",
            "Svara på svenska med en kort mening: vad är 2+2?", _cfg.ApiKey, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(answer)) _log.Error("Inget svar från servern.");
        else _log.Success("Svar: " + answer.Trim());
        TestBtn.IsEnabled = true;
    }

    // ===================== Mätare =====================

    private void OnSample(SystemSample sample)
    {
        Dispatcher.BeginInvoke(() =>
        {
            CpuBar.Value = sample.CpuPercent;
            CpuText.Text = $"{sample.CpuPercent:0} %";

            var ramPct = sample.RamTotalGb > 0 ? 100.0 * sample.RamUsedGb / sample.RamTotalGb : 0;
            RamBar.Value = ramPct;
            RamText.Text = $"{sample.RamUsedGb:0.0} / {sample.RamTotalGb:0.0} GB";

            if (_server.IsRunning)
            {
                CpuText.Text = $"{sample.CpuPercent:0} %  ·  server {sample.ProcCpuPercent:0} %";
                RamText.Text = $"{sample.RamUsedGb:0.0} / {sample.RamTotalGb:0.0} GB  ·  {sample.ProcRamMb:0} MB";
            }
        });
    }

    private async void MetricsTick(object? sender, EventArgs e)
    {
        if (_metricsBusy) return;
        _metricsBusy = true;
        try
        {
            if (!_server.IsRunning)
            {
                TokensText.Text = "— tok/s";
                VramText.Text = "—";
                VramBar.Value = 0;
                return;
            }

            var metrics = await ServerClient.MetricsAsync($"http://127.0.0.1:{_cfg.Port}", _cfg.ApiKey, CancellationToken.None);
            if (metrics is { } m)
            {
                var now = DateTime.UtcNow;
                if (_lastMetricsAt != DateTime.MinValue)
                {
                    var dt = (now - _lastMetricsAt).TotalSeconds;
                    if (dt > 0.4 && m.Tokens >= _lastTokens)
                        _tokPerSec = (m.Tokens - _lastTokens) / dt;
                }
                _lastTokens = m.Tokens;
                _lastMetricsAt = now;
            }
            TokensText.Text = _tokPerSec > 0.05 ? $"{_tokPerSec:0.0} tok/s" : "— tok/s";

            var used = await GpuProbe.NvidiaVramUsedAsync();
            var selected = SelectedDevices();
            if (used.Count > 0 && selected.Count > 0)
            {
                long usedMb = 0, totalMb = 0;
                foreach (var d in selected)
                {
                    var idx = _devices.IndexOf(d);
                    if (idx >= 0 && used.TryGetValue(idx, out var u)) usedMb += u;
                    totalMb += d.TotalMiB;
                }
                VramBar.Value = totalMb > 0 ? Math.Clamp(100.0 * usedMb / totalMb, 0, 100) : 0;
                VramText.Text = $"{usedMb / 1024d:0.0} / {totalMb / 1024d:0.0} GB";
            }
            else
            {
                VramText.Text = "— (endast NVIDIA)";
                VramBar.Value = 0;
            }

            if (_cfg.HostMode == HostMode.Network) UpdateEndpointDisplay();
        }
        finally
        {
            _metricsBusy = false;
        }
    }

    // ===================== Logg =====================

    private void OnLogLine(LogLevel level, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _logItems.Add(new LogItem(level, message));
            while (_logItems.Count > 4000) _logItems.RemoveAt(0);
            LogCountText.Text = _logItems.Count.ToString();

            if (!string.IsNullOrEmpty(_log.CurrentFile))
                LogFileText.Text = Path.GetFileName(_log.CurrentFile);

            if (AutoScrollToggle.IsChecked == true && _logItems.Count > 0)
            {
                try { LogList.ScrollIntoView(_logItems[^1]); } catch { }
            }
        });
    }

    private void LogFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _logFilter = LogFilter.Text.Trim();
        _logView?.Refresh();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logItems.Clear();
        LogCountText.Text = "0";
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        PathUtil.OpenInExplorer(_log.CurrentFile ?? AppPaths.LogsDir);
    }

    private void SaveLogToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.SaveLogToFile = SaveLogToggle.IsChecked == true;
        _log.SaveToFile = _cfg.SaveLogToFile;
        ScheduleSave();
    }

    private void AutoScrollToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.AutoScrollLog = AutoScrollToggle.IsChecked == true;
        ScheduleSave();
    }

    // ===================== Inställningar =====================

    private void PortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535) return;
        _cfg.Port = port;
        ScheduleSave();
        UpdateEndpointDisplay();
        UpdateCommandPreview();
    }

    private void PortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            PortBox.Text = _cfg.Port.ToString();
            _log.Warn("Ogiltig port – återställde till " + _cfg.Port + ".");
        }
    }

    private void NetworkToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.HostMode = NetworkToggle.IsChecked == true ? HostMode.Network : HostMode.Local;
        if (_cfg.HostMode == HostMode.Network)
        {
            _log.Warn($"Servern blir tillgänglig på nätverket ({NetUtil.LocalIp()}). Sätt en API-nyckel om du inte litar på nätet.");
            if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
                _log.Info("Tips: API-nyckel finns under Avancerat.");
        }
        ScheduleSave();
        UpdateEndpointDisplay();
        UpdateCommandPreview();
    }

    private void ThreadsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThreadsValue == null) return;
        var v = (int)Math.Round(e.NewValue);
        ThreadsValue.Text = $"{v} av {Environment.ProcessorCount}";
        if (_loading) return;
        _cfg.Threads = v;
        ScheduleSave();
        UpdateCommandPreview();
    }

    private void GpuAutoToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.GpuLayersAuto = GpuAutoToggle.IsChecked == true;
        UpdateLockState();
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void GpuLayersSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GpuLayersValue == null) return;
        var v = (int)Math.Round(e.NewValue);
        var total = _selected?.Layers ?? 0;
        GpuLayersValue.Text = total > 0 ? $"{v} / {total}" : v.ToString();
        if (_loading) return;
        _cfg.GpuLayers = v;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void FlashToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.FlashAttention = FlashToggle.IsChecked == true ? FlashMode.Auto : FlashMode.Off;
        ScheduleSave();
        UpdateCommandPreview();
    }

    private void VramReserveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VramReserveValue == null) return;
        var v = (int)Math.Round(e.NewValue);
        VramReserveValue.Text = $"{v} MB / GPU";
        if (_loading) return;
        _cfg.VramReserveMb = v;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void OsReserveSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OsReserveValue == null) return;
        var v = (int)Math.Round(e.NewValue);
        OsReserveValue.Text = $"{v} GB";
        if (_loading) return;
        _cfg.OsReserveGb = v;
        ScheduleSave();
        UpdateRamGuardHint();
        UpdateCommandPreview();
    }

    private void RamGuardToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _cfg.RamGuardEnabled = RamGuardToggle.IsChecked == true;
        ScheduleSave();
        UpdateRamGuardHint();
    }

    private void UpdateRamGuardHint()
    {
        var total = Native.GetRam().TotalGb;
        if (_cfg.RamGuardEnabled)
        {
            var max = Math.Max(1, total - _cfg.OsReserveGb);
            RamGuardHint.Text = $"Aktiv: servern får högst {Fmt.Gb(max)} av {Fmt.Gb(total)}. Windows behåller {_cfg.OsReserveGb} GB.";
            RamGuardHint.Foreground = (Brush)FindResource("MutedBrush");
        }
        else
        {
            RamGuardHint.Text = "Av: servern får använda allt RAM. Risk för att datorn hänger sig om modellen är för stor.";
            RamGuardHint.Foreground = (Brush)FindResource("DimBrush");
        }
    }

    private void OffloadCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OffloadCombo.SelectedItem is not ComboBoxItem item) return;
        _cfg.Offload = (OffloadMode)item.Tag;
        UpdateOffloadHint();
        if (_loading) return;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void UpdateOffloadHint()
    {
        OffloadHint.Text = _cfg.Offload switch
        {
            OffloadMode.Auto => "Ligger kvar på SSD och läses in vid behov – även när modellen är större än VRAM.",
            OffloadMode.ForceRam => "Hela modellen läses in i RAM. Snabbare, men kräver plats.",
            OffloadMode.LockRam => "Låses i RAM och växlas inte ut till disk.",
            OffloadMode.CpuOnly => "Allt körs på processorn, även om du har ett grafikkort.",
            _ => "",
        };
    }

    private void CtxCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CtxCombo.SelectedItem is not ComboBoxItem item) return;
        _cfg.ContextSize = (int)item.Tag;
        UpdateModelInfo();
        if (_loading) return;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void KvCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KvCombo.SelectedItem is not ComboBoxItem item) return;
        _cfg.KvCacheType = (string)item.Tag;
        if (_loading) return;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void ParallelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (!int.TryParse(ParallelBox.Text, out var n) || n < 1 || n > 64) return;
        _cfg.ParallelSlots = n;
        ScheduleSave();
        UpdateEstimates();
        UpdateCommandPreview();
    }

    private void ApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _cfg.ApiKey = ApiKeyBox.Text;
        ScheduleSave();
        UpdateCommandPreview();
    }

    private void ExtraArgsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _cfg.ExtraArgs = ExtraArgsBox.Text;
        ScheduleSave();
        UpdateCommandPreview();
    }

    private void BackendCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || BackendCombo.SelectedItem is not ComboBoxItem item) return;
        var kind = (BackendKind)item.Tag;
        if (kind == _cfg.Backend) return;
        if (_server.IsRunning)
        {
            _log.Warn("Stoppa servern innan du byter backend.");
        }
        _cfg.Backend = kind;
        ScheduleSave();
        _log.Info($"Backend: {AppConfig.BackendName(kind)}");
        _ = RefreshDevicesAsync();
        UpdateCommandPreview();
    }

    // ===================== Uppdateringar & hjälp =====================

    private async void UpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        var win = new UpdateWindow(_cfg, _log, _server.IsRunning) { Owner = this };
        win.ShowDialog();
        _backendsReady = true;
        PopulateBackendCombo();
        await RefreshDevicesAsync();
        UpdateUiState();
    }

    private void HelpBtn_Click(object sender, RoutedEventArgs e)
    {
        new HelpWindow { Owner = this }.ShowDialog();
    }

    private async Task CheckUpdatesQuietAsync()
    {
        try
        {
            var rel = await UpdateService.GetLatestLlamaAsync(CancellationToken.None);
            if (rel == null) return;
            var installed = BackendManager.ReadVersion(BackendKind.Cpu)?.Tag
                            ?? BackendManager.ReadVersion(BackendKind.Vulkan)?.Tag;
            if (string.IsNullOrEmpty(installed)) return;
            if (!string.Equals(installed, rel.Tag, StringComparison.OrdinalIgnoreCase))
            {
                UpdateBtn.Content = "Uppdatering finns";
                _log.Info($"Ny llama.cpp-version tillgänglig: {rel.Tag} (du har {installed}). Öppna Uppdateringar.");
            }
        }
        catch
        {
            // offline – ignorera
        }
    }

    // ===================== Stängning =====================

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        IsClosing = true;
        try
        {
            var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            _cfg.WindowWidth = bounds.Width;
            _cfg.WindowHeight = bounds.Height;
            _cfg.WindowMaximized = WindowState == WindowState.Maximized;
            _cfg.Save();
        }
        catch
        {
            // ignorera
        }

        _metricsTimer.Stop();
        _monitor.Stop();
        _server.KillNow();
        _monitor.Dispose();
    }
}

