using System.Globalization;
using System.Text.Json;
using System.Windows.Forms;
using StreamerV2;

if (args.Length == 0)
{
    // Top-level programs are not reliably marked STA by the generated entry
    // point. WebView2 requires COM apartment-threaded initialization, so own
    // the entire WinForms lifetime from an explicitly STA thread.
    var uiThread = new Thread(() =>
    {
        ApplicationConfiguration.Initialize();
        // GStreamer may initialize COM as MTA. Keep that away from the UI STA.
        Task.Run(GStreamerEngine.Initialize).GetAwaiter().GetResult();
        Application.Run(new MainForm());
    });
    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.Start();
    uiThread.Join();
    return 0;
}

GStreamerEngine.Initialize();

var arguments = args.ToList();
if (arguments.Contains("--list-windows", StringComparer.OrdinalIgnoreCase))
{
    foreach (var window in WindowDiscovery.List())
        Console.WriteLine($"0x{window.Hwnd.ToInt64():X}\tPID={window.ProcessId}\t{window.ProcessName}\t{window.DisplayName}");
    return 0;
}

if (arguments.Contains("--list-monitors", StringComparer.OrdinalIgnoreCase))
{
    foreach (var monitor in WindowDiscovery.ListMonitors())
        Console.WriteLine($"INDEX={monitor.MonitorIndex}\t{monitor.DisplayName}");
    return 0;
}

var duration = TimeSpan.FromSeconds(GetInt(arguments, "--duration", 30));
var settings = Presets.StableOldPc with
{
    Encoder = GetEnum(arguments, "--encoder", Presets.StableOldPc.Encoder),
    RateControl = GetEnum(arguments, "--rate-control", Presets.StableOldPc.RateControl),
    Scale = GetEnum(arguments, "--scale", Presets.StableOldPc.Scale),
    WhipEndpoint = GetString(arguments, "--endpoint", Presets.StableOldPc.WhipEndpoint),
    BearerToken = GetString(arguments, "--token", ""),
    Width = GetInt(arguments, "--width", Presets.StableOldPc.Width),
    Height = GetInt(arguments, "--height", Presets.StableOldPc.Height),
    FramesPerSecond = GetInt(arguments, "--fps", Presets.StableOldPc.FramesPerSecond),
    VideoBitrateKbps = GetInt(arguments, "--video-kbps", Presets.StableOldPc.VideoBitrateKbps),
    AudioBitrateKbps = GetInt(arguments, "--audio-kbps", Presets.StableOldPc.AudioBitrateKbps),
    AudioGain = GetDouble(arguments, "--audio-gain", Presets.StableOldPc.AudioGain),
    EncoderPreset = GetString(arguments, "--preset", GetString(arguments, "--nvenc-preset", Presets.StableOldPc.EncoderPreset)),
    KeyframeIntervalSeconds = GetInt(arguments, "--keyframe", Presets.StableOldPc.KeyframeIntervalSeconds),
    BFrames = GetInt(arguments, "--bframes", Presets.StableOldPc.BFrames),
    Crf = GetInt(arguments, "--crf", Presets.StableOldPc.Crf),
    VideoSource = GetEnum(arguments, "--video-source", Presets.StableOldPc.VideoSource),
    MonitorIndex = GetInt(arguments, "--monitor-index", Presets.StableOldPc.MonitorIndex),
    AudioSource = GetEnum(arguments, "--audio-source",
        GetEnum(arguments, "--audio-mode", Presets.StableOldPc.AudioSource))
};

var target = ResolveTarget(arguments, settings);
if (target is null)
{
    Console.Error.WriteLine("Usage: --list-windows | --list-monitors | --capture [--title TEXT | --hwnd 0x...] [--duration 30]");
    Console.Error.WriteLine("       --capture --monitor-index 0 [--audio-mode SystemExceptDiscord]");
    Console.Error.WriteLine("       --stream  [--title TEXT | --hwnd 0x...] --token STREAM_KEY [--duration 30]");
    return 2;
}

Console.WriteLine(PipelinePlanner.Describe(target, settings));
using var engine = new GStreamerEngine();
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

StreamRunResult result;
if (arguments.Contains("--capture", StringComparer.OrdinalIgnoreCase))
{
    var output = GetString(arguments, "--output", Path.Combine(AppContext.BaseDirectory, "capture-test.mkv"));
    result = engine.RunWindowCaptureTest(target, settings, duration, output, cancellation.Token);
}
else if (arguments.Contains("--stream", StringComparer.OrdinalIgnoreCase))
{
    result = engine.RunWindowStream(target, settings, duration, cancellation.Token);
}
else
{
    Console.Error.WriteLine("Escolha --capture ou --stream.");
    return 2;
}

Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return result.Completed ? 0 : 1;

static WindowTarget? ResolveTarget(List<string> arguments, StreamSettings settings)
{
    if (settings.VideoSource == VideoSourceKind.Monitor || GetOptionalString(arguments, "--monitor-index") is not null)
    {
        var index = GetInt(arguments, "--monitor-index", settings.MonitorIndex);
        return WindowDiscovery.ListMonitors().FirstOrDefault(m => m.MonitorIndex == index);
    }

    var hwndText = GetOptionalString(arguments, "--hwnd");
    if (hwndText is not null)
    {
        var hwnd = ParseNint(hwndText);
        var pid = GetInt(arguments, "--pid", 0);
        return new WindowTarget(hwnd, pid, "CLI target");
    }

    var query = GetOptionalString(arguments, "--title");
    var windows = WindowDiscovery.List();
    return windows.FirstOrDefault(w => query is null || w.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
}

static nint ParseNint(string text)
{
    var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
    return (nint)Convert.ToInt64(normalized, text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? 16 : 10);
}

static int GetInt(List<string> args, string name, int fallback)
{
    var value = GetOptionalString(args, name);
    return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

static double GetDouble(List<string> args, string name, double fallback)
{
    var value = GetOptionalString(args, name);
    return value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
}

static string GetString(List<string> args, string name, string fallback) => GetOptionalString(args, name) ?? fallback;

static T GetEnum<T>(List<string> args, string name, T fallback) where T : struct, Enum
{
    var value = GetOptionalString(args, name);
    return value is not null && Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
}

static string? GetOptionalString(List<string> args, string name)
{
    var index = args.FindIndex(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
}
