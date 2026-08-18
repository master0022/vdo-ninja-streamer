using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace StreamerV2;

internal sealed class MainForm : Form
{
    private readonly AtomicSettingsStore _settingsStore = new(Path.Combine(AppContext.BaseDirectory, "settings-v02.json"));
    private readonly GStreamerEngine _engine = new();
    private readonly WebView2 _webView = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private IReadOnlyList<WindowTarget> _windows = Array.Empty<WindowTarget>();
    private IReadOnlyList<WindowTarget> _monitors = Array.Empty<WindowTarget>();
    private StreamSettings _settings;
    private CoreWebView2? _core;
    private CancellationTokenSource? _runCancellation;
    private Task<StreamRunResult>? _runTask;
    private nint _selectedHwnd;
    private int _selectedMonitorIndex = -1;
    private bool _closing;

    public MainForm()
    {
        Text = "Streamer v0.2";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 720);
        ClientSize = new Size(1180, 860);
        Icon = LoadApplicationIcon();
        BackColor = Color.FromArgb(12, 15, 22);

        _settings = _settingsStore.LoadOrDefault(Presets.StableOldPc);
        _webView.Dock = DockStyle.Fill;
        _webView.DefaultBackgroundColor = BackColor;
        Controls.Add(_webView);
        Shown += OnShownAsync;
        FormClosing += OnFormClosing;
    }

    private async void OnShownAsync(object? sender, EventArgs e)
    {
        try
        {
            // Keep the profile beside this portable copy so different installs
            // cannot lock each other's browser data or runtime versions.
            var userData = Path.Combine(AppContext.BaseDirectory, "WebView2Data");
            Directory.CreateDirectory(userData);
            var fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2");
            var browserExecutable = Path.Combine(fixedRuntime, "msedgewebview2.exe");
            if (!File.Exists(browserExecutable))
                throw new InvalidOperationException("The bundled WebView2 runtime is missing from the WebView2 folder next to StreamerV2.exe.");

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: fixedRuntime,
                userDataFolder: userData);
            await _webView.EnsureCoreWebView2Async(environment);
            _core = _webView.CoreWebView2;
            _core.Settings.AreDefaultContextMenusEnabled = false;
            _core.Settings.AreDevToolsEnabled = false;
            _core.Settings.IsStatusBarEnabled = false;
            _core.Settings.IsZoomControlEnabled = false;
            _core.WebMessageReceived += OnWebMessageReceived;
            _webView.NavigateToString(WebUi.Html);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                "The bundled WebView2 runtime could not open the interface.\n\n" + ex.Message,
                "Streamer v0.2", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : null;
            switch (type)
            {
                case "ready":
                    RefreshWindows();
                    SendState();
                    Log($"Loaded portable settings from {_settingsStore.PathForDisplay}.");
                    break;
                case "refresh":
                    RefreshWindows();
                    SendState();
                    break;
                case "save":
                    SaveFromUi(root.GetProperty("settings"));
                    break;
                case "save-key":
                    SaveStreamKey(root);
                    break;
                case "start":
                    StartStream(root);
                    break;
                case "capture":
                    StartCapture(root);
                    break;
                case "stop":
                    StopStream();
                    break;
                case "copy":
                    CopyToClipboard(root.GetProperty("text").GetString() ?? string.Empty);
                    break;
                case "open":
                    OpenExternal(root.GetProperty("url").GetString() ?? string.Empty);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus("ERROR", "error");
            Log("Error: " + ex.GetBaseException().Message);
        }
    }

    private void RefreshWindows()
    {
        var previous = _selectedHwnd;
        var previousMonitor = _selectedMonitorIndex;
        _windows = WindowDiscovery.List(Handle);
        _monitors = WindowDiscovery.ListMonitors();
        _selectedHwnd = _windows.FirstOrDefault(w => w.Hwnd == previous)?.Hwnd
            ?? _windows.FirstOrDefault()?.Hwnd
            ?? 0;
        _selectedMonitorIndex = _monitors.FirstOrDefault(m => m.MonitorIndex == previousMonitor)?.MonitorIndex
            ?? _monitors.FirstOrDefault()?.MonitorIndex
            ?? -1;
        Log($"Found {_windows.Count} visible application windows and {_monitors.Count} monitors.");
    }

    private void SaveFromUi(JsonElement uiSettings)
    {
        try
        {
            _settings = ReadSettings(uiSettings, requireToken: false);
            _settingsStore.Save(_settings);
            SetStatus("SAVED", "success");
            Log("Configuration saved locally.");
            SendState();
        }
        catch (Exception ex)
        {
            SetStatus("INVALID CONFIG", "error");
            Log("Error: " + ex.Message);
        }
    }

    private void SaveStreamKey(JsonElement root)
    {
        try
        {
            var token = String(root, "streamKey", _settings.BearerToken).Trim();
            if (token.Length == 0) throw new InvalidOperationException("Stream key cannot be empty.");
            _settings = _settings with { BearerToken = token, SettingsVersion = 2 };
            _settingsStore.Save(_settings);
            SetStatus("KEY SAVED", "success");
            Log("Stream key saved locally.");
            SendState();
        }
        catch (Exception ex)
        {
            SetStatus("INVALID KEY", "error");
            Log("Error: " + ex.Message);
        }
    }

    private void StartStream(JsonElement root)
    {
        if (_runTask is { IsCompleted: false }) return;

        StreamSettings settings;
        try
        {
            settings = ReadSettings(root.GetProperty("settings"), requireToken: true);
            ValidateSourceSettings(settings);
        }
        catch (Exception ex)
        {
            SetStatus("INVALID CONFIG", "error");
            Log("Error: " + ex.Message);
            return;
        }

        var target = ResolveTarget(root, settings);
        if (target is null)
        {
            SetStatus("SELECT A VIDEO SOURCE", "error");
            Log("Choose a visible window or monitor before starting.");
            return;
        }

        _settings = settings;
        _settingsStore.Save(_settings);
        _selectedHwnd = target.Hwnd;
        _selectedMonitorIndex = target.MonitorIndex;
        _runCancellation = new CancellationTokenSource();
        _runTask = Task.Run(() => _engine.RunWindowStream(target, _settings, TimeSpan.FromDays(7), _runCancellation.Token));
        SetStatus("STARTING", "starting");
        Log($"Starting {TargetDescription(target)}.");
        _ = ObserveRunAsync(_runTask, "stream");
        SendState();
    }

    private void StartCapture(JsonElement root)
    {
        if (_runTask is { IsCompleted: false }) return;

        StreamSettings settings;
        try
        {
            settings = ReadSettings(root.GetProperty("settings"), requireToken: false);
            ValidateSourceSettings(settings);
        }
        catch (Exception ex)
        {
            SetStatus("INVALID CONFIG", "error");
            Log("Error: " + ex.Message);
            return;
        }

        var target = ResolveTarget(root, settings);
        if (target is null)
        {
            SetStatus("SELECT A VIDEO SOURCE", "error");
            Log("Choose a visible window or monitor before the local capture test.");
            return;
        }

        _settings = settings;
        var directory = Path.Combine(Path.GetTempPath(), "StreamerV2", "captures");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.mkv");
        _selectedHwnd = target.Hwnd;
        _selectedMonitorIndex = target.MonitorIndex;
        _runCancellation = new CancellationTokenSource();
        _runTask = Task.Run(() => _engine.RunWindowCaptureTest(target, _settings, TimeSpan.FromSeconds(10), output, _runCancellation.Token));
        SetStatus("CAPTURING", "starting");
        Log($"Running a 10-second local capture test. Output: {output}");
        _ = ObserveRunAsync(_runTask, "capture");
        SendState();
    }

    private async Task ObserveRunAsync(Task<StreamRunResult> task, string mode)
    {
        try
        {
            var result = await task;
            Log($"{mode}: started={result.Started}, completed={result.Completed}, runtime={result.Runtime.TotalSeconds:0.0}s, bus messages={result.BusMessages}.");
            if (result.Metrics is { } metrics)
                Log($"Metrics: CPU avg {metrics.AverageProcessCpuPercent:0.0}% · peak {metrics.PeakProcessCpuPercent:0.0}% · RAM peak {metrics.PeakWorkingSetBytes / 1024d / 1024d:0} MB.");
            if (result.Error is not null) Log("GStreamer: " + result.Error);
            SetStatus(result.Completed ? "IDLE" : "STOPPED WITH ERROR", result.Completed ? "idle" : "error");
        }
        catch (Exception ex)
        {
            SetStatus("ERROR", "error");
            Log("Fatal error: " + ex.GetBaseException().Message);
        }
        finally
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
            SendState();
        }
    }

    private void StopStream()
    {
        if (_runTask is not { IsCompleted: false }) return;
        SetStatus("STOPPING", "starting");
        Log("Stop requested. Waiting for GStreamer and WHIP to close.");
        _runCancellation?.Cancel();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _closing = true;
        _runCancellation?.Cancel();
        if (_runTask is { IsCompleted: false } task)
        {
            try { task.GetAwaiter().GetResult(); }
            catch { /* engine is disposed immediately below */ }
        }
        _engine.Dispose();
        _webView.Dispose();
    }

    private WindowTarget? ResolveTarget(JsonElement root, StreamSettings settings)
    {
        if (settings.VideoSource == VideoSourceKind.Monitor)
        {
            var monitorIndex = settings.MonitorIndex;
            if (root.TryGetProperty("monitorIndex", out var monitorValue) && monitorValue.TryGetInt32(out var requestedIndex))
                monitorIndex = requestedIndex;
            return _monitors.FirstOrDefault(m => m.MonitorIndex == monitorIndex);
        }

        if (!root.TryGetProperty("hwnd", out var value)) return _windows.FirstOrDefault(w => w.Hwnd == _selectedHwnd);
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)) return _windows.FirstOrDefault(w => w.Hwnd == _selectedHwnd);
        var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!long.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out var hwnd)) return null;
        return _windows.FirstOrDefault(w => w.Hwnd == (nint)hwnd);
    }

    private static void ValidateSourceSettings(StreamSettings settings)
    {
        if (settings.VideoSource == VideoSourceKind.Monitor && settings.AudioSource == AudioSourceKind.SelectedProcess)
            throw new InvalidOperationException("Monitor capture cannot follow a selected app's audio. Choose System audio (except Discord) for a full-monitor capture.");
    }

    private static string TargetDescription(WindowTarget target) =>
        target.SourceKind == VideoSourceKind.Monitor
            ? target.DisplayName
            : $"{target.DisplayName} · {target.ProcessName} · PID {target.ProcessId}";

    private StreamSettings ReadSettings(JsonElement value, bool requireToken)
    {
        var endpoint = String(value, "endpoint", _settings.WhipEndpoint).Trim();
        var token = String(value, "streamKey", _settings.BearerToken).Trim();
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _)) throw new InvalidOperationException("WHIP endpoint is invalid.");
        if (requireToken && token.Length == 0) throw new InvalidOperationException("Enter a stream key before starting.");
        var videoSource = EnumValue(value, "videoSource", _settings.VideoSource);
        var audioSource = videoSource == VideoSourceKind.Monitor
            ? AudioSourceKind.SystemExceptDiscord
            : AudioSourceKind.SelectedProcess;

        return new StreamSettings(
            EnumValue(value, "encoder", _settings.Encoder),
            Clamp(value, "width", _settings.Width, 160, 4096),
            Clamp(value, "height", _settings.Height, 64, 4096),
            Clamp(value, "fps", _settings.FramesPerSecond, 1, 120),
            Clamp(value, "videoKbps", _settings.VideoBitrateKbps, 100, 100000),
            Clamp(value, "audioKbps", _settings.AudioBitrateKbps, 32, 512),
            Math.Clamp(Double(value, "audioGain", _settings.AudioGain), 0.1, 4),
            EnumValue(value, "scale", _settings.Scale),
            EnumValue(value, "rateControl", _settings.RateControl),
            Clamp(value, "keyframeSeconds", _settings.KeyframeIntervalSeconds, 1, 10),
            String(value, "encoderPreset", _settings.EncoderPreset).Trim(),
            Clamp(value, "bFrames", _settings.BFrames, 0, 4),
            _settings.AdaptiveNetwork,
            _settings.ForwardErrorCorrection,
            _settings.Retransmission,
            _settings.Degradation,
            endpoint,
            token,
            Clamp(value, "crf", _settings.Crf, 0, 51),
            videoSource,
            Clamp(value, "monitorIndex", _settings.MonitorIndex, -1, 32),
            audioSource,
            2);
    }

    private void SendState()
    {
        if (_core is null || _closing) return;
        var state = new
        {
            type = "state",
            running = _runTask is { IsCompleted: false },
            status = _runTask is { IsCompleted: false } ? "LIVE" : "IDLE",
            encoders = GStreamerEngine.GetEncoderOptions().Select(o => new
            {
                value = o.Value.ToString(),
                label = o.Label,
                available = o.Available,
                reason = o.Reason
            }).ToArray(),
            selectedHwnd = _selectedHwnd == 0 ? null : _selectedHwnd.ToInt64().ToString("X"),
            selectedMonitorIndex = _selectedMonitorIndex < 0 ? (int?)null : _selectedMonitorIndex,
            windows = _windows.Select(w => new { hwnd = w.Hwnd.ToInt64().ToString("X"), pid = w.ProcessId, title = w.DisplayName, process = w.ProcessName }).ToArray(),
            monitors = _monitors.Select(m => new { index = m.MonitorIndex, title = m.DisplayName }).ToArray(),
            settings = new
            {
                endpoint = _settings.WhipEndpoint,
                streamKey = _settings.BearerToken,
                encoder = _settings.Encoder.ToString(),
                rateControl = _settings.RateControl.ToString(),
                scale = _settings.Scale.ToString(),
                width = _settings.Width,
                height = _settings.Height,
                fps = _settings.FramesPerSecond,
                videoKbps = _settings.VideoBitrateKbps,
                audioKbps = _settings.AudioBitrateKbps,
                audioGain = _settings.AudioGain,
                keyframeSeconds = _settings.KeyframeIntervalSeconds,
                bFrames = _settings.BFrames,
                encoderPreset = _settings.EncoderPreset,
                crf = _settings.Crf,
                videoSource = _settings.VideoSource.ToString(),
                monitorIndex = _settings.MonitorIndex,
                audioSource = _settings.AudioSource.ToString()
            }
        };
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(state, _json));
    }

    private void SetStatus(string text, string tone) => Send(new { type = "status", text, tone });

    private void Log(string text) => Send(new { type = "log", text });

    private void Send(object payload)
    {
        if (_core is null || _closing) return;
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(payload, _json));
    }

    private static void CopyToClipboard(string text)
    {
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private static Icon LoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    private static void OpenExternal(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private static string String(JsonElement value, string name, string fallback) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? fallback : fallback;

    private static int Clamp(JsonElement value, string name, int fallback, int minimum, int maximum) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var parsed) ? Math.Clamp(parsed, minimum, maximum) : Math.Clamp(fallback, minimum, maximum);

    private static double Double(JsonElement value, string name, double fallback) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var parsed) ? parsed : fallback;

    private static T EnumValue<T>(JsonElement value, string name, T fallback) where T : struct, Enum
    {
        var text = String(value, name, fallback.ToString());
        return Enum.TryParse<T>(text, true, out var parsed) ? parsed : fallback;
    }
}
