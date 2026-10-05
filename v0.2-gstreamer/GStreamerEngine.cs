using System.Diagnostics;
using System.Runtime.InteropServices;
using Gst;
using GstSharpBundle;

namespace StreamerV2;

public sealed record StreamRunResult(
    bool Started,
    bool Completed,
    TimeSpan Runtime,
    string? Error,
    long BusMessages,
    string PipelineText,
    RuntimeMetrics? Metrics = null);

public sealed record RuntimeMetrics(
    double AverageProcessCpuPercent,
    double PeakProcessCpuPercent,
    long PeakWorkingSetBytes,
    long ProcessCpuTimeMilliseconds,
    int Samples);

/// <summary>Callbacks a run reports through; both may be raised from a worker thread.</summary>
public sealed record StreamObserver(Action<LiveStreamInfo>? OnInfo = null, Action<string>? OnLog = null, Action<string>? OnTrace = null)
{
    public static readonly StreamObserver Console = new(
        info => System.Console.WriteLine($"[live] {info.Encoder} {info.Width}x{info.Height}@{info.FramesPerSecond} {info.BitrateKbps} kbps {info.Adjustment}"),
        text => System.Console.WriteLine("[log] " + text),
        text => System.Console.WriteLine("[trace] " + text));
}

public sealed class GStreamerEngine : IDisposable
{
    private const string WindowGoneError = "The captured window or monitor was closed, removed or minimized; the stream stopped for safety.";
    // A session that never produced an encoded frame within this time is
    // treated as an encoder that cannot run on this PC.
    private static readonly TimeSpan EncoderStartTimeout = TimeSpan.FromSeconds(8);
    private const int MaxConsecutiveReconnects = 8;

    private Pipeline? _pipeline;
    private bool _disposed;

    public static void Initialize()
    {
        // A registry that survives restarts turns the multi-second plugin scan
        // into a ~30 ms cache load. It is keyed by install folder so separate
        // portable copies never share one; GStreamer itself rescans any plugin
        // whose size/mtime changed and writes the cache via temp file + rename.
        var registryDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StreamerV2", "gstreamer-registry");
        Directory.CreateDirectory(registryDirectory);
        var installKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16];
        Environment.SetEnvironmentVariable(
            "GST_REGISTRY",
            Path.Combine(registryDirectory, $"registry-{installKey}.bin"));
        DeleteLegacyPerProcessRegistries();
        GStreamerBundle.Initialize();
        Gst.Application.Init();
    }

    private static void DeleteLegacyPerProcessRegistries()
    {
        // Older builds left one ~2 MB registry per launch in %TEMP%.
        try
        {
            var legacy = Path.Combine(Path.GetTempPath(), "StreamerV2", "gstreamer-registry");
            if (Directory.Exists(legacy)) Directory.Delete(legacy, recursive: true);
        }
        catch
        {
            // Another old copy may still hold its file open; try again next launch.
        }
    }

    public static IReadOnlyList<EncoderOption> GetEncoderOptions()
    {
        var automatic = EncoderCatalog.Candidates(EncoderKind.Auto);
        var options = new List<EncoderOption>
        {
            new(EncoderKind.Auto, "Automatic (recommended)", automatic.Count > 0,
                automatic.Count > 0
                    ? "Tries " + string.Join(" → ", automatic.Select(e => e.Label)) + ", keeping the first that works."
                    : "No H.264 encoder is available in this runtime.")
        };
        foreach (var entry in EncoderCatalog.All)
        {
            var factory = EncoderCatalog.FindFactory(entry);
            options.Add(new EncoderOption(entry.Kind, entry.Label, factory is not null,
                factory is not null
                    ? $"Available through {factory}."
                    : entry.IsHardware ? "Not supported by this PC's GPU or driver." : $"Missing GStreamer plugin: {entry.Factories[0]}."));
        }
        return options;
    }

    public StreamRunResult RunWindowStream(WindowTarget target, StreamSettings settings, TimeSpan duration, CancellationToken cancellationToken, StreamObserver? observer = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WindowDiscovery.IsAlive(target))
            return new(false, false, TimeSpan.Zero, "The target window is not available.", 0, "");
        if (string.IsNullOrWhiteSpace(settings.WhipEndpoint))
            return new(false, false, TimeSpan.Zero, "WHIP endpoint is empty.", 0, "");
        if (string.IsNullOrWhiteSpace(settings.BearerToken))
            return new(false, false, TimeSpan.Zero, "Stream key is empty.", 0, "");
        return Run(target, settings, outputPath: null, duration, cancellationToken, observer ?? new StreamObserver());
    }

    public StreamRunResult RunWindowCaptureTest(WindowTarget target, StreamSettings settings, TimeSpan duration, string outputPath, CancellationToken cancellationToken, StreamObserver? observer = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WindowDiscovery.IsAlive(target))
            return new(false, false, TimeSpan.Zero, "The target window is not available.", 0, "");
        return Run(target, settings, Path.GetFullPath(outputPath), duration, cancellationToken, observer ?? new StreamObserver());
    }

    /// <summary>
    /// Runs sessions until the duration ends or the user stops: falls through
    /// the encoder candidates while none has produced a frame yet, and
    /// reconnects with backoff when an established stream drops.
    /// </summary>
    private StreamRunResult Run(WindowTarget target, StreamSettings settings, string? outputPath, TimeSpan duration, CancellationToken cancellationToken, StreamObserver observer)
    {
        var stopwatch = Stopwatch.StartNew();
        var deadline = System.DateTime.UtcNow + duration;
        var metrics = new MetricsRecorder();
        var candidates = EncoderCatalog.Candidates(settings.Encoder);
        if (candidates.Count == 0)
            return new(false, false, TimeSpan.Zero, "No usable H.264 encoder was found on this PC.", 0, "");

        var (sourceWidth, sourceHeight) = WindowDiscovery.GetSourceSize(target);
        var levels = settings.AutoQuality
            ? AdaptiveController.BuildLadder(settings.Mode, sourceWidth, sourceHeight)
            : [new QualityLevel(settings.Width, settings.Height, settings.FramesPerSecond, settings.VideoBitrateKbps)];

        long busMessages = 0;
        string? lastError = null;
        string description = "";
        var everStarted = false;
        var candidateIndex = 0;
        var reconnects = 0;
        var retriedBelow4K = false;
        AdaptiveController? controller = null;

        while (!cancellationToken.IsCancellationRequested && System.DateTime.UtcNow < deadline && candidateIndex < candidates.Count)
        {
            var entry = candidates[candidateIndex];
            var factory = EncoderCatalog.FindFactory(entry);
            if (factory is null)
            {
                lastError = $"{entry.Label} is not available on this PC.";
                candidateIndex++;
                continue;
            }

            // A new encoder starts from its own safe level; a reconnect keeps
            // whatever the controller had learned about this PC and network.
            controller ??= new AdaptiveController(levels, AdaptiveController.StartIndex(levels, entry.IsHardware), settings.AutoQuality);

            var session = new Session(target, settings, entry, factory, outputPath, controller, observer);
            description = session.Description;
            observer.OnLog?.Invoke($"Starting {entry.Label} at {controller.Current.Width}x{controller.Current.Height}@{controller.Current.FramesPerSecond}, {controller.BitrateKbps} kbps.");
            var outcome = session.Run(this, deadline, cancellationToken, metrics);
            busMessages += outcome.BusMessages;
            everStarted |= outcome.ProducedFrames;
            lastError = outcome.Error;

            if (outcome.Error is null || outcome.WindowGone || cancellationToken.IsCancellationRequested)
                break;

            if (!outcome.ProducedFrames)
            {
                // Older GPUs cap encoder resolution; retry once at 1080p before
                // giving up on a hardware encoder that would otherwise work.
                if (!retriedBelow4K && controller.Current.Height > 1080 && levels.Any(l => l.Height <= 1080))
                {
                    retriedBelow4K = true;
                    observer.OnLog?.Invoke($"{entry.Label} could not start above 1080p; retrying at 1080p.");
                    // Drop the higher steps too, so auto quality never climbs back into them.
                    levels = levels.Where(l => l.Height <= 1080).ToArray();
                    controller = new AdaptiveController(levels, 0, settings.AutoQuality);
                    continue;
                }
                retriedBelow4K = false;
                observer.OnLog?.Invoke($"{entry.Label} could not start: {outcome.Error}");
                candidateIndex++;
                controller = null;
                if (candidateIndex < candidates.Count)
                    observer.OnLog?.Invoke($"Trying the next encoder: {candidates[candidateIndex].Label}.");
                continue;
            }

            if (outputPath is not null) break; // local tests do not reconnect

            reconnects = outcome.HealthyFor > TimeSpan.FromSeconds(60) ? 1 : reconnects + 1;
            if (reconnects > MaxConsecutiveReconnects) break;
            var delay = TimeSpan.FromSeconds(Math.Min(15, 2 * reconnects));
            observer.OnLog?.Invoke($"Stream dropped ({outcome.Error}). Reconnecting in {delay.TotalSeconds:0} s…");
            if (cancellationToken.WaitHandle.WaitOne(delay)) break;
        }

        stopwatch.Stop();
        var completed = everStarted && (lastError is null || cancellationToken.IsCancellationRequested);
        return new(everStarted, completed, stopwatch.Elapsed, completed ? null : lastError, busMessages, description, metrics.Build());
    }

    private sealed record SessionOutcome(bool ProducedFrames, TimeSpan HealthyFor, string? Error, bool WindowGone, long BusMessages);

    /// <summary>One pipeline instance with one encoder.</summary>
    private sealed class Session(
        WindowTarget target,
        StreamSettings settings,
        EncoderCatalog.Entry entry,
        string factory,
        string? outputPath,
        AdaptiveController controller,
        StreamObserver observer)
    {
        private Element? _encoder;
        private Element? _videoCaps;
        private bool _systemMemory;
        private long _encodedFrames;
        // Held in a field: the native side keeps calling it for every frame.
        private PadProbeCallback? _countFrames;

        public string Description => $"{(target.SourceKind == VideoSourceKind.Monitor ? $"monitor {target.MonitorIndex}" : $"window 0x{target.Hwnd:X}")} -> {factory} -> {(outputPath is null ? "WHIP " + settings.WhipEndpoint : outputPath)}";

        public SessionOutcome Run(GStreamerEngine engine, System.DateTime deadline, CancellationToken cancellationToken, MetricsRecorder metrics)
        {
            long busMessages = 0;
            Stopwatch? healthy = null;
            var started = Stopwatch.StartNew();
            var cpu = new SystemCpuSampler();
            var network = new NetworkStatsReader(observer);
            var lastTick = Stopwatch.GetTimestamp();
            long framesAtLastTick = 0;

            try
            {
                engine._pipeline = Build(controller.Current, controller.BitrateKbps);
                if (engine._pipeline.SetState(State.Playing) == StateChangeReturn.Failure)
                    return new(false, TimeSpan.Zero, "GStreamer refused to start the pipeline.", false, 0);
                Report(null);

                var bus = engine._pipeline.Bus ?? throw new InvalidOperationException("Pipeline has no bus.");
                while (System.DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
                {
                    if (!WindowDiscovery.IsAlive(target))
                        return new(healthy is not null, healthy?.Elapsed ?? TimeSpan.Zero, WindowGoneError, true, busMessages);

                    var frames = Interlocked.Read(ref _encodedFrames);
                    if (healthy is null && frames > 0) healthy = Stopwatch.StartNew();
                    if (healthy is null && started.Elapsed > EncoderStartTimeout)
                        return new(false, TimeSpan.Zero, "the encoder produced no video", false, busMessages);

                    var now = Stopwatch.GetTimestamp();
                    var interval = (now - lastTick) / (double)Stopwatch.Frequency;
                    if (interval >= 1 && healthy is not null)
                    {
                        metrics.Sample();
                        var (loss, rtt) = outputPath is null ? network.Read(engine._pipeline) : (null, null);
                        var sample = new AdaptiveSample(interval, frames - framesAtLastTick, cpu.Sample(), loss, rtt);
                        observer.OnTrace?.Invoke($"encoded {sample.FramesEncoded / interval:0.0} fps, system CPU {sample.SystemCpuPercent:0}%, loss {(loss is null ? "n/a" : loss.Value.ToString("P1"))}, RTT {(rtt is null ? "n/a" : rtt.Value.ToString("0") + " ms")}");
                        var decision = controller.Tick(sample);
                        Apply(decision);
                        lastTick = now;
                        framesAtLastTick = frames;
                    }
                    else if (healthy is null)
                    {
                        lastTick = now;
                        framesAtLastTick = frames;
                    }

                    using var message = bus.TimedPopFiltered(100_000_000, MessageType.Error | MessageType.Eos);
                    if (message is null) continue;
                    busMessages++;
                    if (message.Type == MessageType.Eos)
                        return new(healthy is not null, healthy?.Elapsed ?? TimeSpan.Zero, null, false, busMessages);

                    string error;
                    try
                    {
                        message.ParseError(out var gstError, out var debug);
                        error = $"{gstError.Message} {debug}".Trim();
                    }
                    catch
                    {
                        error = message.ToString() ?? "unknown GStreamer error";
                    }
                    var gone = !WindowDiscovery.IsAlive(target);
                    return new(healthy is not null, healthy?.Elapsed ?? TimeSpan.Zero, gone ? WindowGoneError : error, gone, busMessages);
                }
                return new(healthy is not null, healthy?.Elapsed ?? TimeSpan.Zero, null, false, busMessages);
            }
            catch (Exception ex)
            {
                return new(healthy is not null, healthy?.Elapsed ?? TimeSpan.Zero, ex.GetBaseException().Message, false, busMessages);
            }
            finally
            {
                metrics.Sample();
                engine.StopPipeline();
            }
        }

        private void Apply(AdaptiveDecision decision)
        {
            if (decision.NewLevel is { } level && _videoCaps is not null)
            {
                // Changing the caps renegotiates scaler and encoder in place;
                // the WHIP session and the viewers' connection stay up.
                _videoCaps["caps"] = VideoCaps(level);
            }
            if (decision.NewBitrateKbps is { } kbps && _encoder is not null)
                EncoderCatalog.SetBitrate(_encoder, entry, kbps);
            if (decision.NewLevel is not null || decision.NewBitrateKbps is not null)
            {
                if (decision.Reason is not null)
                    observer.OnLog?.Invoke($"Auto quality: {(decision.NewLevel is not null ? $"{controller.Current.Width}x{controller.Current.Height}@{controller.Current.FramesPerSecond}, " : "")}{controller.BitrateKbps} kbps — {decision.Reason}.");
                Report(decision.Reason);
            }
        }

        private void Report(string? reason)
        {
            var level = controller.Current;
            observer.OnInfo?.Invoke(new LiveStreamInfo(entry.Label, level.Width, level.Height, level.FramesPerSecond, controller.BitrateKbps, reason));
        }

        private Caps VideoCaps(QualityLevel level) => Caps.FromString(
            $"video/x-raw{(_systemMemory ? "" : "(memory:D3D11Memory)")},format=NV12,width={level.Width},height={level.Height},framerate={level.FramesPerSecond}/1");

        private Pipeline Build(QualityLevel level, int bitrateKbps)
        {
            var pipeline = new Pipeline("streamer-v02");
            var screen = Make("d3d11screencapturesrc", "screen", pipeline);
            Set(screen, "capture-api", 1); // Windows Graphics Capture
            if (target.SourceKind == VideoSourceKind.Monitor)
            {
                Set(screen, "monitor-index", target.MonitorIndex);
            }
            else
            {
                Set(screen, "window-handle", (ulong)target.Hwnd.ToInt64());
                Set(screen, "window-capture-mode", 1); // client area
            }
            Set(screen, "show-cursor", target.SourceKind == VideoSourceKind.Monitor);

            var convert = Make("d3d11convert", "gpu-convert", pipeline);
            var video = new List<Element> { screen, convert };
            // Lanczos/Bicubic exist only as CPU filters; everything else keeps
            // frames on the GPU from capture to encoder with no copies.
            _systemMemory = !entry.AcceptsD3D11 ||
                            (!settings.AutoQuality && settings.Scale is ScaleMethod.Bicubic or ScaleMethod.Lanczos);
            if (_systemMemory)
            {
                var download = Make("d3d11download", "gpu-download", pipeline);
                var cpuConvert = Make("videoconvert", "cpu-convert", pipeline);
                var scale = Make("videoscale", "cpu-scale", pipeline);
                Set(scale, "method", settings.AutoQuality ? 1 : VideoScaleMethod(settings.Scale));
                Set(scale, "n-threads", (uint)Math.Clamp(Environment.ProcessorCount, 1, 4));
                Set(cpuConvert, "n-threads", (uint)Math.Clamp(Environment.ProcessorCount, 1, 4));
                video.AddRange([download, cpuConvert, scale]);
            }
            else
            {
                Set(convert, "method", settings.Scale == ScaleMethod.None && !settings.AutoQuality ? 0 : 1);
            }
            _videoCaps = Make("capsfilter", "video-caps", pipeline);
            _videoCaps["caps"] = VideoCaps(level);
            video.Add(_videoCaps);

            var queue = Make("queue", "video-queue", pipeline);
            Set(queue, "max-size-buffers", (uint)2);
            Set(queue, "max-size-bytes", (uint)0);
            Set(queue, "max-size-time", (ulong)0);
            Set(queue, "leaky", 2); // drop old frames instead of building latency
            video.Add(queue);

            _encoder = Make(factory, "encoder", pipeline);
            EncoderCatalog.Configure(_encoder, entry, settings, bitrateKbps, level.FramesPerSecond);
            _countFrames = (_, _) =>
            {
                Interlocked.Increment(ref _encodedFrames);
                return PadProbeReturn.Ok;
            };
            _encoder.GetStaticPad("src").AddProbe(PadProbeType.Buffer, _countFrames);
            video.Add(_encoder);

            var parse = Make(entry.Codec switch { "h265" => "h265parse", "av1" => "av1parse", _ => "h264parse" }, "parse", pipeline);
            if (entry.Codec != "av1") Set(parse, "config-interval", -1);
            video.Add(parse);

            var audio = BuildAudio(pipeline);
            if (outputPath is null)
            {
                var whip = Make("whipsink", "whip", pipeline);
                Set(whip, "whip-endpoint", settings.WhipEndpoint);
                Set(whip, "auth-token", settings.BearerToken);
                Set(whip, "use-link-headers", true);

                var pay = Make(entry.Codec switch { "h265" => "rtph265pay", "av1" => "rtpav1pay", _ => "rtph264pay" }, "video-pay", pipeline);
                if (entry.Codec != "av1") Set(pay, "config-interval", -1);
                Set(pay, "pt", entry.Codec switch { "h265" => 98u, "av1" => 99u, _ => 96u });
                var outQueue = PacketQueue("encoded-video-queue", pipeline);
                video.AddRange([pay, outQueue, whip]);

                var payAudio = Make("rtpopuspay", "opus-pay", pipeline);
                Set(payAudio, "pt", 97u);
                audio.AddRange([payAudio, PacketQueue("audio-queue", pipeline), whip]);
            }
            else
            {
                var mux = Make("matroskamux", "mux", pipeline);
                var sink = Make("filesink", "file", pipeline);
                Set(sink, "location", outputPath);
                video.AddRange([mux, sink]);
                // A file never stalls like a network, so keep every audio
                // buffer while the muxer waits for the first video frame.
                var audioQueue = Make("queue", "audio-queue", pipeline);
                Set(audioQueue, "max-size-time", 2_000_000_000ul);
                audio.AddRange([audioQueue, mux]);
            }

            Link(video);
            Link(audio);
            return pipeline;
        }

        private List<Element> BuildAudio(Pipeline pipeline)
        {
            var source = Make("wasapi2src", "audio-source", pipeline);
            Set(source, "loopback", true);
            Set(source, "low-latency", true);
            // Audio follows the video source by design: a window captures only
            // its process tree; a monitor captures the system mix minus Discord.
            if (target.SourceKind == VideoSourceKind.Monitor)
            {
                var discordPid = WindowDiscovery.FindDiscordRootPid()
                    ?? throw new InvalidOperationException("Discord was not found. Start Discord first, or capture an application window instead.");
                Set(source, "loopback-mode", 2); // exclude-process-tree
                Set(source, "loopback-target-pid", (uint)discordPid);
            }
            else
            {
                if (target.ProcessId <= 0)
                    throw new InvalidOperationException("Selected app audio requires an application window.");
                Set(source, "loopback-mode", 1); // include-process-tree
                Set(source, "loopback-target-pid", (uint)target.ProcessId);
            }

            var convert = Make("audioconvert", "audio-convert", pipeline);
            var resample = Make("audioresample", "audio-resample", pipeline);
            var caps = Make("capsfilter", "audio-caps", pipeline);
            caps["caps"] = Caps.FromString("audio/x-raw,format=S16LE,rate=48000,channels=2");
            var volume = Make("volume", "audio-gain", pipeline);
            Set(volume, "volume", settings.AudioGain);
            var opus = Make("opusenc", "opus", pipeline);
            Set(opus, "bitrate", settings.AudioBitrateKbps * 1000);
            Set(opus, "frame-size", 20);
            return [source, convert, resample, caps, volume, opus];
        }

        /// <summary>
        /// Queue in front of the network sink. One video frame is many RTP
        /// packets (a keyframe can be dozens), so a count-limited leaky queue
        /// drops pieces of frames and viewers see corruption until the next
        /// keyframe. Bound it by time instead: it only drops when the network
        /// has been stalled for longer than a viewer would tolerate anyway.
        /// </summary>
        private static Element PacketQueue(string name, Pipeline pipeline)
        {
            var queue = Make("queue", name, pipeline);
            Set(queue, "max-size-buffers", 0u);
            Set(queue, "max-size-bytes", 0u);
            Set(queue, "max-size-time", 300_000_000ul);
            Set(queue, "leaky", 2);
            return queue;
        }
    }

    /// <summary>
    /// Reads packet loss and RTT from the RTCP receiver reports webrtcbin
    /// collects inside whipsink. Worst stream wins: losing audio is as bad as
    /// losing video.
    /// </summary>
    private sealed class NetworkStatsReader(StreamObserver observer)
    {
        private readonly Dictionary<uint, (ulong Sent, long Lost)> _previous = [];
        private Element? _webrtc;
        private bool _unavailable;

        public (double? Loss, double? RttMs) Read(Pipeline pipeline)
        {
            if (_unavailable) return (null, null);
            try
            {
                _webrtc ??= FindWebRtcBin(pipeline);
                if (_webrtc is null) return (null, null);

                // The managed signal wrapper rejects a null or ghost pad, so
                // emit directly: a NULL pad returns stats for every stream.
                using var promise = new Promise();
                g_signal_emit_by_name(_webrtc.Handle, "get-stats", nint.Zero, promise.Handle, nint.Zero);
                if (promise.Wait() != PromiseResult.Replied) return (null, null);
                var reply = promise.RetrieveReply();
                if (reply is null) return (null, null);

                // Cumulative counters per SSRC: packets we sent (outbound-rtp)
                // and packets the server reports lost (remote-inbound-rtp).
                var sent = new Dictionary<uint, ulong>();
                var lost = new Dictionary<uint, long>();
                double? rtt = null;
                for (uint i = 0; i < (uint)reply.NFields(); i++)
                {
                    if (reply.GetValue(reply.NthFieldName(i)).Val is not Structure stat || !stat.GetUint("ssrc", out var ssrc))
                        continue;
                    if (stat.Name.StartsWith("outbound-rtp", StringComparison.Ordinal) && stat.GetUint64("packets-sent", out var packets))
                        sent[ssrc] = packets;
                    if (!stat.Name.StartsWith("remote-inbound-rtp", StringComparison.Ordinal)) continue;
                    if (stat.GetInt64("packets-lost", out var packetsLost))
                        lost[ssrc] = packetsLost;
                    // Broadcast Box (Pion) omits the RTCP timing fields, so
                    // RTT is often 0; only trust it when the server fills it.
                    if (stat.GetDouble("round-trip-time", out var seconds) && seconds > 0)
                        rtt = Math.Max(rtt ?? 0, seconds * 1000);
                }

                // The receiver's own "fraction-lost" is unreliable here (it
                // freezes on startup losses), so measure loss over the packets
                // sent since the last report: worst stream wins.
                double? loss = null;
                foreach (var (ssrc, packetsLost) in lost)
                {
                    if (!sent.TryGetValue(ssrc, out var packetsSent)) continue;
                    if (_previous.TryGetValue(ssrc, out var before) && packetsSent > before.Sent)
                    {
                        var window = packetsSent - before.Sent;
                        if (window < 60 && packetsLost == before.Lost) continue; // too few packets to judge yet
                        var fraction = Math.Clamp((packetsLost - before.Lost) / (double)window, 0, 1);
                        loss = Math.Max(loss ?? 0, fraction);
                    }
                    _previous[ssrc] = (packetsSent, packetsLost);
                }
                return (loss, rtt);
            }
            catch (Exception ex)
            {
                // Stats are an optimisation; never let them stop a stream.
                observer.OnTrace?.Invoke("WebRTC stats unavailable: " + ex.GetBaseException().Message);
                _unavailable = true;
                return (null, null);
            }
        }

        // Variadic in C, but on x64 Windows pointer-sized varargs use the
        // regular calling convention; the trailing zero is a harmless extra.
        [DllImport("gobject-2.0-0.dll", CharSet = CharSet.Ansi)]
        private static extern void g_signal_emit_by_name(nint instance, string signal, nint pad, nint promise, nint terminator);

        private static Element? FindWebRtcBin(Pipeline pipeline)
        {
            var iterator = pipeline.IterateAllByElementFactoryName("webrtcbin");
            var value = new GLib.Value();
            return iterator.Next(ref value) == IteratorResult.Ok ? value.Val as Element : null;
        }
    }

    /// <summary>Whole-machine CPU load, so a game eating the CPU counts too.</summary>
    private sealed class SystemCpuSampler
    {
        private ulong _idle, _total;

        public double Sample()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            var total = kernel + user; // kernel time includes idle time
            var (deltaIdle, deltaTotal) = (idle - _idle, total - _total);
            var first = _total == 0;
            (_idle, _total) = (idle, total);
            return first || deltaTotal == 0 ? 0 : Math.Clamp(100.0 * (deltaTotal - deltaIdle) / deltaTotal, 0, 100);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    }

    private sealed class MetricsRecorder
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly List<(double Cpu, long WorkingSet)> _samples = [];
        private long _lastWall = Stopwatch.GetTimestamp();
        private TimeSpan _lastCpu = Process.GetCurrentProcess().TotalProcessorTime;

        public void Sample()
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _lastWall) / (double)Stopwatch.Frequency;
            if (elapsed < 0.5) return;
            _process.Refresh();
            var cpu = _process.TotalProcessorTime;
            _samples.Add((Math.Max(0, (cpu - _lastCpu).TotalSeconds / (elapsed * Environment.ProcessorCount) * 100), _process.WorkingSet64));
            (_lastWall, _lastCpu) = (now, cpu);
        }

        public RuntimeMetrics? Build() => _samples.Count == 0
            ? null
            : new(_samples.Average(s => s.Cpu), _samples.Max(s => s.Cpu), _samples.Max(s => s.WorkingSet),
                (long)_process.TotalProcessorTime.TotalMilliseconds, _samples.Count);
    }

    private static Element Make(string factory, string name, Pipeline pipeline)
    {
        var element = ElementFactory.Make(factory, name)
            ?? throw new InvalidOperationException($"Missing GStreamer plugin: {factory}");
        if (!pipeline.Add(element))
            throw new InvalidOperationException($"Could not add {factory} to the pipeline.");
        return element;
    }

    private static void Set(Element element, string property, object value)
    {
        try { element[property] = value; }
        catch (Exception ex) { throw new InvalidOperationException($"Failed to set {element.Name}.{property}: {ex.Message}", ex); }
    }

    private static void Link(IReadOnlyList<Element> elements)
    {
        for (var i = 0; i + 1 < elements.Count; i++)
            if (!elements[i].Link(elements[i + 1]))
                throw new InvalidOperationException($"Could not link {elements[i].Name} -> {elements[i + 1].Name}.");
    }

    private static int VideoScaleMethod(ScaleMethod method) => method switch
    {
        ScaleMethod.None => 0,
        ScaleMethod.Bicubic => 8,
        ScaleMethod.Lanczos => 3,
        _ => 1
    };

    private void StopPipeline()
    {
        if (_pipeline is null) return;
        try
        {
            _pipeline.SetState(State.Null);
            _pipeline.Dispose();
        }
        finally
        {
            _pipeline = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPipeline();
    }
}
