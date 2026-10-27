using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;

namespace StreamerV2;

/// <summary>
/// Headless control mode for driving the streamer from another app (the Discord
/// BBStream plugin): `StreamerV2.exe --serve`.
///
/// One JSON object per line on stdin, one per line on stdout.
///   in : {"cmd":"status"} | {"cmd":"monitors"}
///        {"cmd":"start","hwnd":"0x1234"} or {"cmd":"start","monitorIndex":0}
///          optional: "width","height","fps","kbps" override the saved settings
///          optional: "excludePid" = the Discord process whose audio a monitor capture leaves out
///        {"cmd":"stop"}
///   out: {"event":"ready"}, {"event":"status",...}, {"event":"monitors",...}
///        {"event":"starting"}, {"event":"live",...}, {"event":"log","text":...}
///        {"event":"ended","completed":bool,"error":string?}, {"event":"error","text":...}
///
/// Settings and the stream key come from settings-v02.json next to the exe, the
/// same file the normal UI saves. Closing stdin (the parent exiting) stops the
/// stream and exits, so nothing is left streaming after Discord closes.
/// </summary>
public static class ServeMode
{
    private static readonly object WriteLock = new();
    private static readonly AtomicSettingsStore Store = new(Path.Combine(AppContext.BaseDirectory, "settings-v02.json"));

    private static CancellationTokenSource? _streamCancel;
    private static Task? _streamTask;

    public static int Run()
    {
        // GStreamer loads while the parent is still deciding what to share.
        GStreamerEngine.Initialize();
        Emit(new JsonObject { ["event"] = "ready" });

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { message = null; }
            if (message is null)
            {
                Emit(new JsonObject { ["event"] = "error", ["text"] = "Invalid command." });
                continue;
            }

            switch ((string?)message["cmd"])
            {
                case "status": EmitStatus(); break;
                case "monitors": EmitMonitors(); break;
                case "start": Start(message); break;
                case "stop": Stop(); break;
                default: Emit(new JsonObject { ["event"] = "error", ["text"] = "Unknown command." }); break;
            }
        }

        Stop();
        return 0;
    }

    private static StreamSettings LoadSettings() => Presets.Migrate(Store.LoadOrDefault(Presets.Defaults));

    private static void EmitStatus()
    {
        var settings = LoadSettings();
        Emit(new JsonObject
        {
            ["event"] = "status",
            ["streaming"] = _streamTask is { IsCompleted: false },
            ["hasKey"] = !string.IsNullOrWhiteSpace(settings.BearerToken),
            ["streamKey"] = settings.BearerToken,
            ["endpoint"] = settings.WhipEndpoint,
            ["width"] = settings.Width,
            ["height"] = settings.Height,
            ["fps"] = settings.FramesPerSecond,
            ["kbps"] = settings.VideoBitrateKbps
        });
    }

    private static void EmitMonitors()
    {
        var list = new JsonArray();
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            list.Add(new JsonObject
            {
                ["index"] = i,
                ["x"] = b.X,
                ["y"] = b.Y,
                ["width"] = b.Width,
                ["height"] = b.Height,
                ["primary"] = screens[i].Primary
            });
        }
        Emit(new JsonObject { ["event"] = "monitors", ["monitors"] = list });
    }

    private static void Start(JsonObject message)
    {
        Stop();
        WindowDiscovery.DiscordPidOverride = (int?)message["excludePid"];

        var settings = LoadSettings();
        if (string.IsNullOrWhiteSpace(settings.BearerToken))
        {
            Emit(new JsonObject { ["event"] = "ended", ["completed"] = false, ["error"] = "No stream key yet. Open StreamerV2 once and set it." });
            return;
        }

        WindowTarget? target;
        if (message["monitorIndex"] is JsonValue monitorValue)
        {
            var index = monitorValue.GetValue<int>();
            target = WindowDiscovery.ListMonitors().FirstOrDefault(m => m.MonitorIndex == index);
            settings = settings with { VideoSource = VideoSourceKind.Monitor, MonitorIndex = index, AudioSource = AudioSourceKind.SystemExceptDiscord };
        }
        else
        {
            target = ParseHwnd((string?)message["hwnd"]) is { } hwnd ? WindowDiscovery.FromHwnd(hwnd) : null;
            settings = settings with { VideoSource = VideoSourceKind.Window, AudioSource = AudioSourceKind.SelectedProcess };
        }

        if (target is null)
        {
            Emit(new JsonObject { ["event"] = "ended", ["completed"] = false, ["error"] = "That window or screen is no longer available." });
            return;
        }

        settings = settings with
        {
            Width = (int?)message["width"] ?? settings.Width,
            Height = (int?)message["height"] ?? settings.Height,
            FramesPerSecond = (int?)message["fps"] ?? settings.FramesPerSecond,
            VideoBitrateKbps = (int?)message["kbps"] ?? settings.VideoBitrateKbps
        };

        var cancel = _streamCancel = new CancellationTokenSource();
        Emit(new JsonObject { ["event"] = "starting", ["source"] = target.DisplayName });

        var observer = new StreamObserver(
            info => Emit(new JsonObject
            {
                ["event"] = "live",
                ["encoder"] = info.Encoder,
                ["width"] = info.Width,
                ["height"] = info.Height,
                ["fps"] = info.FramesPerSecond,
                ["kbps"] = info.BitrateKbps
            }),
            text => Emit(new JsonObject { ["event"] = "log", ["text"] = text }));

        _streamTask = Task.Run(() =>
        {
            StreamRunResult result;
            try
            {
                using var engine = new GStreamerEngine();
                // Runs until stopped; the engine treats this as "no time limit".
                result = engine.RunWindowStream(target, settings, TimeSpan.FromDays(365), cancel.Token, observer);
            }
            catch (Exception ex)
            {
                Emit(new JsonObject { ["event"] = "ended", ["completed"] = false, ["error"] = ex.Message });
                return;
            }
            Emit(new JsonObject { ["event"] = "ended", ["completed"] = result.Completed, ["error"] = result.Error });
        });
    }

    private static void Stop()
    {
        _streamCancel?.Cancel();
        // The engine tears the pipeline down to NULL (sending the WHIP DELETE) before returning.
        _streamTask?.Wait(TimeSpan.FromSeconds(10));
        _streamCancel = null;
        _streamTask = null;
    }

    private static nint? ParseHwnd(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? text[2..] : text, hex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? (nint)value
            : null;
    }

    private static void Emit(JsonObject message)
    {
        var json = message.ToJsonString();
        lock (WriteLock)
        {
            Console.Out.WriteLine(json);
            Console.Out.Flush();
        }
    }
}
