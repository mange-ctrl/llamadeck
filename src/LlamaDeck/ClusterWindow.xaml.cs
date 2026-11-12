using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using LlamaDeck.Core;

namespace LlamaDeck;

public sealed class NodeRow : INotifyPropertyChanged
{
    public string Address { get; init; } = "";

    private string _status = "—";
    private string _info = "";
    private Brush _statusBrush = Brushes.Gray;

    public string Status
    {
        get => _status;
        set { _status = value; Raise(nameof(Status)); }
    }

    public string Info
    {
        get => _info;
        set { _info = value; Raise(nameof(Info)); }
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        set { _statusBrush = value; Raise(nameof(StatusBrush)); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class ClusterWindow : Window
{
    private readonly AppConfig _cfg;
    private readonly ObservableCollection<NodeRow> _rows = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2.5) };

    public ClusterWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;

        NodesList.ItemsSource = _rows;
        foreach (var node in cfg.RpcNodes)
            _rows.Add(new NodeRow { Address = node });

        WeightsBox.Text = cfg.RpcWeights;
        UpdateSummary();

        Loaded += async (_, _) =>
        {
            if (_rows.Count > 0) await TestAllAsync();
        };
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var address = Normalize(AddressBox.Text);
        if (address == null)
        {
            StatusHint.Text = "Skriv en adress, till exempel 192.168.1.42:50052.";
            return;
        }
        if (_rows.Any(r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase)))
        {
            StatusHint.Text = address + " finns redan i listan.";
            return;
        }

        var row = new NodeRow { Address = address };
        _rows.Add(row);
        AddressBox.Clear();
        UpdateSummary();
        _ = TestAsync(row);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (NodesList.SelectedItem is not NodeRow row) return;
        _rows.Remove(row);
        UpdateSummary();
    }

    private async void TestAll_Click(object sender, RoutedEventArgs e) => await TestAllAsync();

    private async Task TestAllAsync()
    {
        foreach (var row in _rows.ToList())
            await TestAsync(row);
        UpdateSummary();
    }

    private async Task TestAsync(NodeRow row)
    {
        row.Status = "testar …";
        row.StatusBrush = Brush("MutedBrush");

        var (host, port) = Split(row.Address);
        var ok = await CanConnectAsync(host, port, 1500);
        row.Status = ok ? "svarar" : "nås inte";
        row.StatusBrush = Brush(ok ? "GreenBrush" : "RedBrush");

        if (ok) await FetchInfoAsync(row, host);
    }

    private static async Task<bool> CanConnectAsync(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            var done = await Task.WhenAny(task, Task.Delay(timeoutMs));
            return done == task && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task FetchInfoAsync(NodeRow row, string host)
    {
        try
        {
            var json = await Http.GetStringAsync($"http://{host}:50053/");
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var name = root.TryGetProperty("name", out var n) ? n.GetString() : host;
            var ram = root.TryGetProperty("ramGb", out var r) ? r.GetDouble() : 0;
            var cpus = root.TryGetProperty("cpus", out var c) ? c.GetInt32() : 0;

            var gpuText = "";
            if (root.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var gpu in gpus.EnumerateArray())
                {
                    var gname = gpu.TryGetProperty("name", out var gn) ? gn.GetString() : "GPU";
                    var vram = gpu.TryGetProperty("vramMb", out var vm) ? vm.GetInt32() : 0;
                    parts.Add($"{gname} {vram} MB");
                }
                gpuText = parts.Count > 0 ? " · " + string.Join(", ", parts) : "";
            }

            row.Info = $"{name} · {ram:0.0} GB RAM · {cpus} trådar{gpuText}";
        }
        catch
        {
            row.Info = "";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _cfg.RpcNodes = _rows.Select(r => r.Address).ToList();
        _cfg.RpcWeights = WeightsBox.Text.Trim();
        _cfg.Save();
        DialogResult = true;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateSummary()
    {
        SummaryText.Text = _rows.Count == 0
            ? "Inga noder – servern kör bara på den här datorn."
            : $"{_rows.Count} nod(er) i klustret.";
    }

    private static string? Normalize(string input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return null;
        text = text.Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                   .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                   .TrimEnd('/');
        if (!text.Contains(':')) text += ":50052";
        var parts = text.Split(':');
        if (parts.Length != 2) return null;
        if (parts[0].Length == 0) return null;
        if (!int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return null;
        return text;
    }

    private static (string Host, int Port) Split(string address)
    {
        var index = address.LastIndexOf(':');
        if (index < 0) return (address, 50052);
        return (address[..index], int.TryParse(address[(index + 1)..], out var port) ? port : 50052);
    }

    private static Brush Brush(string key)
    {
        try { return (Brush)Application.Current.FindResource(key); }
        catch { return Brushes.Gray; }
    }
}
