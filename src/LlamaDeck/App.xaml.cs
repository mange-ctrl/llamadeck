using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LlamaDeck.Core;

namespace LlamaDeck;

public partial class App : Application
{
    public static AppConfig Config { get; private set; } = new();
    public static LogBus Log { get; } = new();
    public static bool AutoStart { get; private set; }
    public static bool OpenUpdates { get; private set; }
    public static bool OpenHelp { get; private set; }

    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            try { Log.Error("Oväntat fel: " + args.Exception.Message); } catch { }
            MessageBox.Show(args.Exception.Message, "LlamaDeck – oväntat fel", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var args = e.Args;
        bool portable = args.Contains("--portable");
        var dataDir = ArgValue(args, "--data");
        var models = ArgValue(args, "--models");
        var screenshot = ArgValue(args, "--screenshot");
        var selftest = ArgValue(args, "--selftest");
        var smoke = ArgValue(args, "--smoke");
        var convertFolder = ArgValue(args, "--convert");
        AutoStart = args.Contains("--autostart");
        OpenUpdates = args.Contains("--updates");
        OpenHelp = args.Contains("--help");

        AppPaths.Init(portable, dataDir);
        Config = AppConfig.Load();
        if (!string.IsNullOrWhiteSpace(models))
        {
            Config.ModelFolder = models!;
            Config.LastModelPath = "";
        }

        if (screenshot != null || selftest != null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (selftest != null) { RunSelfTest(selftest, smoke, convertFolder); return; }
            RunScreenshot(screenshot!);
            return;
        }

        _mutex = new Mutex(true, "Local\\LlamaDeck.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("LlamaDeck körs redan.", "LlamaDeck", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        Log.SaveToFile = Config.SaveLogToFile;

        var window = new MainWindow(Config, Log);
        MainWindow = window;
        window.Show();
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : "";
        }
        return null;
    }

    // ===================== Skärmbild (för dokumentation/test) =====================

    private void RunScreenshot(string path)
    {
        try
        {
            var win = new MainWindow(Config, Log)
            {
                Width = 1280,
                Height = 880,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000,
                Top = -20000,
                ShowInTaskbar = false,
            };
            win.Show();
            // Vänta på att fönstret är klart (modellsökningen är asynkron) medan vi pumpar
            // dispatchern – annars blockerar vi de continuations vi väntar på.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!win.Ready.Task.IsCompleted && DateTime.UtcNow < deadline)
            {
                win.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Thread.Sleep(60);
            }
            Thread.Sleep(600);
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(700);
            win.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var dpi = VisualTreeHelper.GetDpi(win);
            int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth * dpi.DpiScaleX));
            int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight * dpi.DpiScaleY));
            var rtb = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            rtb.Render(win);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var fs = File.Create(path)) encoder.Save(fs);

            win.Close();

            // Med --updates: fånga även uppdateringsfönstret, så att dess texter kan granskas.
            if (OpenUpdates)
            {
                var upd = new UpdateWindow(Config, Log, false)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                };
                upd.Show();
                var updDeadline = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < updDeadline)
                {
                    upd.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                    Thread.Sleep(80);
                }
                Thread.Sleep(400);
                upd.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var udpi = VisualTreeHelper.GetDpi(upd);
                int uw = Math.Max(1, (int)Math.Ceiling(upd.ActualWidth * udpi.DpiScaleX));
                int uh = Math.Max(1, (int)Math.Ceiling(upd.ActualHeight * udpi.DpiScaleY));
                var urtb = new RenderTargetBitmap(uw, uh, 96 * udpi.DpiScaleX, 96 * udpi.DpiScaleY, PixelFormats.Pbgra32);
                urtb.Render(upd);
                var uenc = new PngBitmapEncoder();
                uenc.Frames.Add(BitmapFrame.Create(urtb));
                var updPath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-update.png");
                using (var ufs = File.Create(updPath)) uenc.Save(ufs);
                upd.Close();
            }
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error.txt", ex.ToString()); } catch { }
        }
        Shutdown(0);
    }

    // ===================== Självtest =====================

    private void RunSelfTest(string outPath, string? smokeModel, string? convertFolder = null)
    {
        Native.AttachConsoleForOutput();
        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // ingen konsol – skriv till fil ändå
        }

        try { File.WriteAllText(outPath, ""); } catch { }

        var sb = new StringBuilder();
        void W(string s)
        {
            sb.AppendLine(s);
            try { File.AppendAllText(outPath, s + Environment.NewLine); } catch { }
            try { Console.WriteLine(s); } catch { }
        }

        try
        {
            W($"LlamaDeck selftest {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            W($"DataDir: {AppPaths.DataDir} (portable={AppPaths.Portable})");

            foreach (var kind in new[] { BackendKind.Cpu, BackendKind.Vulkan, BackendKind.Cuda12, BackendKind.Cuda13 })
            {
                var exe = BackendManager.ExeFor(kind);
                var ver = BackendManager.ReadVersion(kind);
                W($"Backend {kind,-7} installed={File.Exists(exe),-5} tag={ver?.Tag ?? "-"} size={Fmt.Size(BackendManager.FolderSize(BackendManager.FolderFor(kind)))}");
            }

            var models = ModelLibrary.ScanMany(Config.EffectiveModelFolders, CancellationToken.None);
            W($"Models in {string.Join(" ; ", Config.EffectiveModelFolders)}: {models.Count}");
            foreach (var m in models.Take(12))
                W($"  {m.FileName} | {m.SizeText} | {m.Quant} | arch={m.Arch} layers={m.Layers} ctx={m.ContextLength} heads={m.Heads}/{m.HeadsKv}");

            foreach (var kind in new[] { BackendKind.Cuda12, BackendKind.Vulkan })
            {
                var exe = BackendManager.ExeFor(kind);
                if (!File.Exists(exe)) continue;
                var devices = GpuProbe.ListDevicesAsync(exe).GetAwaiter().GetResult();
                W($"Devices ({kind}): " + string.Join("; ", devices.Select(d => $"{d.Id} | {d.Name} | {d.TotalMiB} MiB | {d.FreeMiB} MiB free")));
            }

            var (cuda, driver) = GpuProbe.NvidiaInfoAsync().GetAwaiter().GetResult();
            W($"NVIDIA: CUDA={cuda} driver={driver}");
            var ram = Native.GetRam();
            W($"RAM: total={ram.TotalGb:0.0} GB avail={ram.AvailableGb:0.0} GB, logical CPUs={Environment.ProcessorCount}");

            // ---- Konvertering av safetensors ----
            var conv = Converter.Detect(refresh: true);
            W($"Converter: ready={conv.Ready} | {conv.Describe}");
            if (!string.IsNullOrEmpty(convertFolder))
            {
                if (!Directory.Exists(convertFolder))
                {
                    W($"Konvertering: mappen finns inte: {convertFolder}");
                }
                else
                {
                    var (files, bytes) = Converter.InspectSafetensors(convertFolder);
                    W($"Konvertering: {files} safetensors-filer ({Fmt.Size(bytes)}) i {convertFolder}");
                    var target = Path.GetDirectoryName(convertFolder.TrimEnd('\\', '/')) ?? convertFolder;
                    var res = Task.Run(() => Converter.ConvertAsync(convertFolder, target, "Q4_K_M",
                        new Progress<string>(line => W("  [konv] " + line)), CancellationToken.None))
                        .GetAwaiter().GetResult();
                    W($"Konvertering: ok={res.Ok} utdata={res.OutputGguf}");
                    if (!string.IsNullOrEmpty(res.Error)) W("  fel: " + res.Error.Replace("\n", " | "));
                }
            }

            // ---- Auto-inställning: visa vad LlamaDeck skulle välja för varje modell ----
            var probeKind = BackendManager.IsInstalled(BackendKind.Cuda13) ? BackendKind.Cuda13 : BackendKind.Vulkan;
            var probeDevices = GpuProbe.ListDevicesAsync(BackendManager.ExeFor(probeKind)).GetAwaiter().GetResult();
            var freeMiB = probeDevices.Count > 0 ? probeDevices.Max(d => d.FreeMiB) : 0;
            W($"--- Auto-inställning (backend {probeKind}, {freeMiB} MiB ledigt VRAM) ---");
            foreach (var m in models)
            {
                var rot = Native.DriveIncursSeekPenalty(m.Path);
                var rec = AutoTuner.Recommend(m, freeMiB, Config.VramReserveMb, 1,
                                              ram.TotalGb, ram.AvailableGb, rot);
                var mmproj = AutoTuner.FindSiblingMmproj(m.Path);
                var vision = !string.IsNullOrEmpty(mmproj) && AutoTuner.SupportsVision(m.Arch);
                W($"  {m.FileName} | MoE={m.IsMoE} layers={m.Layers} | ctx={rec.ContextSize} " +
                  $"ngl={(rec.GpuLayersAuto ? "auto" : rec.GpuLayers.ToString())} moe={rec.MoeMode} kv={rec.KvCacheType} " +
                  $"offload={rec.Offload} disk={(rot == true ? "HDD" : rot == false ? "SSD" : "?")}" +
                  (vision ? $" mmproj={Path.GetFileName(mmproj)}" : ""));
            }

            if (models.Count > 0)
            {
                var model = models[0];
                var plan = new LaunchPlan
                {
                    Backend = BackendKind.Cuda12,
                    ExePath = BackendManager.ExeFor(BackendKind.Cuda12),
                    ModelPath = model.Path,
                    Host = "127.0.0.1",
                    Port = 8099,
                    Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    Context = 4096,
                    Flash = FlashMode.Auto,
                    GpuLayersAuto = true,
                    VramReserveMb = 1024,
                    Parallel = 1,
                };
                W("Preview: " + plan.Preview);
                W($"Planner: kv={Planner.EstimateKvGb(model, 4096, "f16"):0.00} GB, perLayer={Fmt.Size((long)Planner.BytesPerLayer(model))}");

                if (!string.IsNullOrEmpty(smokeModel) && File.Exists(plan.ExePath))
                {
                    W("--- smoke test: startar server ---");
                    var runner = new ServerRunner();
                    runner.Log += (lvl, msg) => W($"[{lvl}] {msg}");
                    var tcs = new TaskCompletionSource<bool>();
                    runner.StateChanged += (state, detail) =>
                    {
                        W($"state={state} ({detail})");
                        if (state == ServerState.Running) tcs.TrySetResult(true);
                        if (state == ServerState.Error) tcs.TrySetResult(false);
                    };
                    runner.StartAsync(plan).GetAwaiter().GetResult();
                    var ok = tcs.Task.Wait(TimeSpan.FromSeconds(120));
                    W($"server ready={ok} endpoint={runner.Endpoint}");
                    if (ok)
                    {
                        var health = ServerClient.HealthAsync($"http://127.0.0.1:{plan.Port}", "", CancellationToken.None).GetAwaiter().GetResult();
                        W($"health={health}");
                        var answer = ServerClient.ChatAsync($"http://127.0.0.1:{plan.Port}", "Say hello in one short sentence.", "", CancellationToken.None).GetAwaiter().GetResult();
                        W($"chat={answer}");
                        var metrics = ServerClient.MetricsAsync($"http://127.0.0.1:{plan.Port}", "", CancellationToken.None).GetAwaiter().GetResult();
                        W($"metrics tokens={metrics?.Tokens} seconds={metrics?.Seconds:0.00}");
                    }
                    runner.StopAsync().GetAwaiter().GetResult();
                    W("--- smoke test klar ---");
                }
            }

            W("RESULT OK");
        }
        catch (Exception ex)
        {
            W("RESULT FAIL: " + ex);
        }

        try { File.WriteAllText(outPath, sb.ToString()); } catch { }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Log.Shutdown(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
