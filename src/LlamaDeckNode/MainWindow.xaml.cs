using System.Windows;
using System.Windows.Media;

namespace LlamaDeckNode;

public partial class MainWindow : Window
{
    private readonly NodeService _service = new();
    private readonly string[] _args;
    private bool _autoStart = true;

    public MainWindow(string[] args)
    {
        InitializeComponent();
        _args = args;

        var rpcPort = ArgInt("--port", 50052);
        var infoPort = ArgInt("--info-port", 50053);
        _autoStart = !args.Contains("--no-autostart");

        _service.RpcPort = rpcPort;
        _service.InfoPort = infoPort;
        _service.BackendDir = ArgValue("--backend");
        _service.Log += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        _service.StateChanged += running => Dispatcher.BeginInvoke(() => UpdateState(running));

        MachineText.Text = $"{Environment.MachineName} · {SystemInfo.OsName}";

        var ram = SystemInfo.RamGb;
        RamText.Text = ram > 0 ? $"{ram:0.0} GB" : "okänt";
        CpuText.Text = $"{Environment.ProcessorCount} logiska kärnor";

        var gpus = SystemInfo.Gpus();
        GpuText.Text = gpus.Count == 0
            ? "Ingen NVIDIA-GPU hittad (CPU-backend körs ändå)"
            : string.Join(", ", gpus.Select(g => $"{g.Name} · {g.VramMb} MB"));

        BackendText.Text = string.IsNullOrWhiteSpace(_service.BackendDir)
            ? "Inbyggd CPU-backend (packas upp vid första start)"
            : _service.BackendDir;

        AddressText.Text = $"{SystemInfo.LocalAddress()}:{rpcPort}";
        AutoToggle.IsChecked = _autoStart;

        Loaded += OnLoaded;
        Closing += (_, _) => _service.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppendLog("LlamaDeck Node redo.");
        if (_args.Contains("--headless")) Hide();

        if (_autoStart)
            StartNode();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => StartNode();
    private void Stop_Click(object sender, RoutedEventArgs e) => _service.Stop();

    private void StartNode()
    {
        try
        {
            StartBtn.IsEnabled = false;
            _service.Start();
        }
        catch (Exception ex)
        {
            AppendLog("Fel: " + ex.Message);
            StartBtn.IsEnabled = true;
            UpdateState(false);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(AddressText.Text);
            AppendLog("Adressen kopierad: " + AddressText.Text);
        }
        catch
        {
            AppendLog("Kunde inte kopiera till urklipp.");
        }
    }

    private void UpdateState(bool running)
    {
        StatusText.Text = running ? "Kör" : "Stoppad";
        StatusText.Foreground = (Brush)FindResource(running ? "GreenBrush" : "MutedBrush");
        StatusDot.Fill = (Brush)FindResource(running ? "GreenBrush" : "MutedBrush");
        StartBtn.IsEnabled = !running;
        StopBtn.IsEnabled = running;
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogBox.AppendText($"{stamp}  {line}{Environment.NewLine}");
        if (LogBox.LineCount > 400)
        {
            var text = LogBox.Text;
            var keep = text.Length / 2;
            LogBox.Text = text[keep..];
        }
        LogBox.ScrollToEnd();
    }

    private int ArgInt(string name, int fallback)
    {
        var value = ArgValue(name);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private string? ArgValue(string name)
    {
        var index = Array.IndexOf(_args, name);
        if (index >= 0 && index + 1 < _args.Length) return _args[index + 1];
        return null;
    }
}
