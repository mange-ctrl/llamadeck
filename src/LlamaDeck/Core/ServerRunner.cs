using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaDeck.Core;

public enum ServerState { Stopped, Starting, Running, Stopping, Error }

/// <summary>Startar, övervakar och stoppar llama-server som en barnprocess.</summary>
public sealed class ServerRunner : IDisposable
{
    public event Action<LogLevel, string>? Log;
    public event Action<ServerState, string>? StateChanged;
    public event Action<int>? Exited;

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? Endpoint { get; private set; }
    public string ApiKey { get; private set; } = "";
    public LaunchPlan? Plan { get; private set; }
    public int? LastExitCode { get; private set; }

    private Process? _proc;
    private Native.Job? _job;
    private bool _stopping;
    private readonly object _sync = new();
    private CancellationTokenSource? _readyCts;

    private static readonly Regex ListenRx = new(@"listening on (http://[^\s]+)", RegexOptions.Compiled);
    private static readonly Regex LevelRx = new(@"^\s*[\d.]+\s+([IWEF])\s", RegexOptions.Compiled);

    public bool IsRunning => State is ServerState.Starting or ServerState.Running;

    public Process? Process
    {
        get
        {
            lock (_sync) return _proc;
        }
    }

    public async Task<bool> StartAsync(LaunchPlan plan)
    {
        await StopAsync().ConfigureAwait(false);
        Plan = plan;
        Endpoint = null;
        LastExitCode = null;

        if (!File.Exists(plan.ExePath))
        {
            Log?.Invoke(LogLevel.Error, $"Hittar inte llama-server.exe för {AppConfig.BackendName(plan.Backend)}.");
            SetState(ServerState.Error, "Backend saknas");
            return false;
        }
        if (!File.Exists(plan.ModelPath))
        {
            Log?.Invoke(LogLevel.Error, "Modellfilen finns inte längre. Välj en annan modell.");
            SetState(ServerState.Error, "Modell saknas");
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = plan.ExePath,
            WorkingDirectory = Path.GetDirectoryName(plan.ExePath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in plan.BuildArgs()) psi.ArgumentList.Add(arg);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) HandleLine(e.Data); };
        proc.Exited += Proc_Exited;

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            Log?.Invoke(LogLevel.Error, $"Kunde inte starta servern: {ex.Message}");
            SetState(ServerState.Error, "Start misslyckades");
            proc.Dispose();
            return false;
        }

        lock (_sync) { _proc = proc; _stopping = false; }

        _job = Native.Job.Create(plan.RamLimitBytes);
        if (_job is { Handle: not 0 } && !_job.Assign(proc))
        {
            Log?.Invoke(LogLevel.Warn, "Kunde inte koppla RAM-vakten till serverprocessen (fortsätter ändå).");
        }
        else if (_job?.Error != null)
        {
            Log?.Invoke(LogLevel.Warn, $"RAM-vakt: {_job.Error}");
        }
        else if (plan.RamLimitBytes is > 0)
        {
            Log?.Invoke(LogLevel.Info,
                $"RAM-vakt aktiv: servern får högst {Fmt.Gb(plan.RamLimitBytes.Value / 1073741824d)} (resten är reserverat för Windows).");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        ApiKey = plan.ApiKey ?? "";
        SetState(ServerState.Starting, "Laddar modell …");
        Log?.Invoke(LogLevel.Info, $"Startar {AppConfig.BackendName(plan.Backend)} på port {plan.Port} …");

        _readyCts = new CancellationTokenSource();
        _ = WaitForReadyAsync(plan.Port, _readyCts.Token);
        return true;
    }

    /// <summary>Väntar tills servern svarar 200 på /health, dvs. modellen är laddad.</summary>
    private async Task WaitForReadyAsync(int port, CancellationToken ct)
    {
        var url = $"http://127.0.0.1:{port}";
        var sw = Stopwatch.StartNew();
        bool listening = false;

        while (!ct.IsCancellationRequested && sw.Elapsed < TimeSpan.FromMinutes(30))
        {
            Process? p;
            lock (_sync) p = _proc;
            if (p == null) return;
            try
            {
                if (p.HasExited) return;
            }
            catch
            {
                return;
            }

            if (!listening)
            {
                try
                {
                    using var tcp = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(2));
                    await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
                    listening = true;
                    Endpoint = url;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // lyssnar inte än
                }
            }

            if (listening && await ServerClient.HealthAsync(url, ApiKey, ct, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
            {
                if (State is ServerState.Starting)
                    SetState(ServerState.Running, "Modellen är laddad och redo");
                return;
            }

            try { await Task.Delay(400, ct).ConfigureAwait(false); } catch { return; }
        }
    }

    private void HandleLine(string line)
    {
        var level = LogLevel.Server;
        var m = LevelRx.Match(line);
        if (m.Success)
        {
            level = m.Groups[1].Value switch
            {
                "W" => LogLevel.Warn,
                "E" or "F" => LogLevel.Error,
                _ => LogLevel.Server,
            };
        }

        Log?.Invoke(level, line);

        var listen = ListenRx.Match(line);
        if (listen.Success)
        {
            Endpoint = listen.Groups[1].Value;
            return;
        }

        if (line.Contains("model loaded", StringComparison.OrdinalIgnoreCase))
            SetState(State == ServerState.Running ? State : ServerState.Starting, "Modell laddad – startar HTTP …");
        else if (line.Contains("loading model", StringComparison.OrdinalIgnoreCase))
            SetState(ServerState.Starting, "Laddar modell …");
        else if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            Log?.Invoke(LogLevel.Error, "Minnet räckte inte. Sänk kontextstorleken, antal GPU-lager eller stäng andra program.");
    }

    private void Proc_Exited(object? sender, EventArgs e)
    {
        int code = -1;
        try { code = _proc?.ExitCode ?? -1; } catch { }
        LastExitCode = code;

        bool wasStopping;
        lock (_sync) wasStopping = _stopping;
        _job?.Dispose();
        _job = null;

        if (wasStopping) return;

        if (code == 0)
        {
            Log?.Invoke(LogLevel.Info, "Servern avslutades.");
            SetState(ServerState.Stopped, "Stoppad");
        }
        else
        {
            Log?.Invoke(LogLevel.Error, $"Servern avslutades oväntat (exitkod {code}).");
            SetState(ServerState.Error, $"Avslutades oväntat (kod {code})");
        }

        Exited?.Invoke(code);
    }

    public async Task StopAsync()
    {
        Process? p;
        lock (_sync) { p = _proc; _stopping = true; }

        _readyCts?.Cancel();

        if (p == null)
        {
            SetState(ServerState.Stopped, "Stoppad");
            return;
        }

        SetState(ServerState.Stopping, "Stoppar …");
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignorera
        }

        try
        {
            using var cts = new CancellationTokenSource(5000);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ignorera
        }

        _job?.Dispose();
        _job = null;
        try { p.Dispose(); } catch { }

        lock (_sync)
        {
            if (ReferenceEquals(_proc, p)) _proc = null;
            _stopping = false;
        }

        Endpoint = null;
        SetState(ServerState.Stopped, "Stoppad");
    }

    private void SetState(ServerState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    /// <summary>Dödar processen direkt utan att vänta (används när fönstret stängs).</summary>
    public void KillNow()
    {
        Process? p;
        lock (_sync)
        {
            p = _proc;
            _stopping = true;
        }

        try { _readyCts?.Cancel(); } catch { }
        try
        {
            if (p is { HasExited: false }) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignorera
        }

        _job?.Dispose();
        _job = null;
        try { p?.Dispose(); } catch { }
        lock (_sync) _proc = null;
        Endpoint = null;
        State = ServerState.Stopped;
    }

    public void Dispose()
    {
        KillNow();
        _readyCts?.Dispose();
    }
}

/// <summary>Små HTTP-anrop mot den lokala servern (hälsa, statistik, testchatt).</summary>
public static class ServerClient
{
    public static async Task<bool> HealthAsync(string baseUrl, string apiKey, CancellationToken ct, TimeSpan? timeout = null)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(4));
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, baseUrl.TrimEnd('/') + "/health");
            Attach(req, apiKey);
            using var resp = await UpdateService.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Sätter Authorization-headern när en API-nyckel är konfigurerad.</summary>
    private static void Attach(System.Net.Http.HttpRequestMessage req, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public static async Task<(long Tokens, double Seconds)?> MetricsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, baseUrl.TrimEnd('/') + "/metrics");
            Attach(req, apiKey);
            var text = await (await UpdateService.Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                .Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            long tokens = 0;
            double seconds = 0;
            foreach (var line in text.Split('\n'))
            {
                if (line.StartsWith("llamacpp:tokens_predicted_total ", StringComparison.Ordinal))
                    long.TryParse(line["llamacpp:tokens_predicted_total ".Length..].Trim(), out tokens);
                else if (line.StartsWith("llamacpp:tokens_predicted_seconds_total ", StringComparison.Ordinal))
                    double.TryParse(line["llamacpp:tokens_predicted_seconds_total ".Length..].Trim(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out seconds);
            }
            return (tokens, seconds);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> ChatAsync(string baseUrl, string prompt, string apiKey, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));

            var payload = new
            {
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens = 768,
                temperature = 0.7,
                stream = false,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var resp = await UpdateService.Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}";

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var message = choice.GetProperty("message");

            var content = message.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String
                ? c.GetString() ?? ""
                : "";
            var reasoning = message.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == System.Text.Json.JsonValueKind.String
                ? rc.GetString() ?? ""
                : "";
            var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;

            if (!string.IsNullOrWhiteSpace(content)) return content;

            if (!string.IsNullOrWhiteSpace(reasoning))
            {
                var trim = reasoning.Trim();
                if (trim.Length > 300) trim = trim[..300] + "…";
                return "Modellen resonerade men hann inte ge ett synligt svar (Max tokens tog slut). " +
                       $"Höj Max tokens – finish_reason = {finish}.\n\n{trim}";
            }

            return $"Tomt svar från servern (finish_reason = {finish}).";
        }
        catch (Exception ex)
        {
            return $"Fel: {ex.Message}";
        }
    }
}

