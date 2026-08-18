using System.Diagnostics;
using System.Globalization;
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

public sealed class GStreamerEngine : IDisposable
{
    private sealed record VideoCodecInfo(
        string Name,
        string EncoderFactory,
        string ParserFactory,
        string PayloaderFactory,
        uint PayloadType,
        bool RequiresSystemMemory,
        bool IsHardware);

    private Pipeline? _pipeline;
    private bool _disposed;

    public static void Initialize()
    {
        // Do not let two portable copies race on GStreamer's shared registry
        // cache. A private per-process registry also avoids stale plugin
        // metadata after the ZIP is moved to another machine.
        var registryDirectory = Path.Combine(Path.GetTempPath(), "StreamerV2", "gstreamer-registry");
        Directory.CreateDirectory(registryDirectory);
        Environment.SetEnvironmentVariable(
            "GST_REGISTRY",
            Path.Combine(registryDirectory, $"registry-{Environment.ProcessId}.bin"));
        GStreamerBundle.Initialize();
        Gst.Application.Init();
    }

    public static IReadOnlyList<EncoderOption> GetEncoderOptions()
    {
        var av1Factory = FindAv1EncoderFactory();
        return new List<EncoderOption>
        {
            EncoderOptionFor(EncoderKind.H264Nvenc, "H.264 NVENC", "nvd3d11h264enc"),
            EncoderOptionFor(EncoderKind.HevcNvenc, "H.265 / HEVC NVENC", "nvd3d11h265enc"),
            new EncoderOption(
                EncoderKind.Av1Nvenc,
                "AV1 NVENC",
                av1Factory is not null,
                av1Factory is null
                    ? "Not included in this bundled GStreamer runtime (hardware AV1 stays disabled)."
                    : $"Available through {av1Factory}."),
            EncoderOptionFor(EncoderKind.H264X264, "H.264 x264 (CPU)", "x264enc")
        };
    }

    private static EncoderOption EncoderOptionFor(EncoderKind value, string label, string factory)
    {
        var available = HasFactory(factory);
        return new EncoderOption(
            value,
            label,
            available,
            available ? $"Available through {factory}." : $"Missing GStreamer plugin: {factory}.");
    }

    private static bool HasFactory(string factory) => ElementFactory.Find(factory) is not null;

    private static string? FindAv1EncoderFactory() =>
        new[] { "nvd3d11av1enc", "nvautogpuav1enc", "nvav1enc" }.FirstOrDefault(HasFactory);

    public StreamRunResult RunWindowStream(WindowTarget target, StreamSettings settings, TimeSpan duration, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WindowDiscovery.IsAlive(target))
            return new(false, false, TimeSpan.Zero, "A janela alvo não está viva.", 0, "");
        if (string.IsNullOrWhiteSpace(settings.WhipEndpoint))
            return new(false, false, TimeSpan.Zero, "WHIP endpoint vazio.", 0, "");
        if (string.IsNullOrWhiteSpace(settings.BearerToken))
            return new(false, false, TimeSpan.Zero, "Bearer token/stream key vazio.", 0, "");

        try
        {
            var description = PipelineTextBuilder.BuildWindowWhip(target, settings);
            return RunPipeline(target, description, duration, cancellationToken, BuildWhipPipeline);
        }
        catch (Exception ex)
        {
            return new(false, false, TimeSpan.Zero, ex.GetBaseException().Message, 0, "");
        }
    }

    public StreamRunResult RunWindowCaptureTest(WindowTarget target, StreamSettings settings, TimeSpan duration, string outputPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!WindowDiscovery.IsAlive(target))
            return new(false, false, TimeSpan.Zero, "A janela alvo não está viva.", 0, "");

        try
        {
            var description = PipelineTextBuilder.BuildLocalCapture(target, settings, outputPath);
            return RunPipeline(target, description, duration, cancellationToken, BuildCapturePipeline);
        }
        catch (Exception ex)
        {
            return new(false, false, TimeSpan.Zero, ex.GetBaseException().Message, 0, "");
        }
    }

    private StreamRunResult RunPipeline(WindowTarget target, string description, TimeSpan duration, CancellationToken cancellationToken, Func<Pipeline> buildPipeline)
    {
        var stopwatch = Stopwatch.StartNew();
        long busMessages = 0;
        string? error = null;
        var started = false;
        var process = Process.GetCurrentProcess();
        var metricSamples = new List<(double Cpu, long WorkingSet)>();
        var lastMetricWall = Stopwatch.GetTimestamp();
        var lastMetricCpu = process.TotalProcessorTime;

        try
        {
            // GstSharp's Parse.Launch binding currently causes an ABI-level access violation
            // on this Windows bundle. Building elements individually also makes ownership explicit.
            _pipeline = buildPipeline();
            var state = _pipeline.SetState(State.Playing);
            if (state == StateChangeReturn.Failure)
                throw new InvalidOperationException("GStreamer recusou o estado PLAYING.");
            started = true;

            var bus = _pipeline.Bus ?? throw new InvalidOperationException("Pipeline sem bus.");
            var deadline = System.DateTime.UtcNow + duration;
            while (System.DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                SampleProcessMetrics(process, metricSamples, ref lastMetricWall, ref lastMetricCpu);
                if (!WindowDiscovery.IsAlive(target))
                {
                    error = "A janela ou monitor capturado foi fechado, removido ou minimizado; stream encerrado por segurança.";
                    break;
                }

                var message = bus.TimedPopFiltered(100_000_000, MessageType.Error | MessageType.Eos | MessageType.Warning | MessageType.Qos);
                if (message is null)
                    continue;

                busMessages++;
                if (message.Type == MessageType.Error)
                {
                    try
                    {
                        message.ParseError(out var gstError, out var debug);
                        error = $"{gstError.Message} {debug}".Trim();
                        if (!WindowDiscovery.IsAlive(target))
                            error = "A janela ou monitor capturado foi fechado, removido ou minimizado; stream encerrado por segurança.";
                    }
                    catch
                    {
                        error = message.ToString();
                    }
                    message.Dispose();
                    break;
                }

                if (message.Type == MessageType.Eos)
                {
                    message.Dispose();
                    break;
                }

                message.Dispose();
            }
        }
        catch (Exception ex)
        {
            error = ex.GetBaseException().Message;
        }
        finally
        {
            SampleProcessMetrics(process, metricSamples, ref lastMetricWall, ref lastMetricCpu);
            StopPipeline();
            stopwatch.Stop();
        }

        var metrics = metricSamples.Count == 0
            ? null
            : new RuntimeMetrics(
                metricSamples.Average(x => x.Cpu),
                metricSamples.Max(x => x.Cpu),
                metricSamples.Max(x => x.WorkingSet),
                (long)process.TotalProcessorTime.TotalMilliseconds,
                metricSamples.Count);
        return new(started, started && error is null, stopwatch.Elapsed, error, busMessages, description, metrics);
    }

    private static void SampleProcessMetrics(Process process, List<(double Cpu, long WorkingSet)> samples, ref long lastWall, ref TimeSpan lastCpu)
    {
        var nowWall = Stopwatch.GetTimestamp();
        var elapsed = (nowWall - lastWall) / (double)Stopwatch.Frequency;
        if (elapsed < 0.5) return;
        process.Refresh();
        var nowCpu = process.TotalProcessorTime;
        var cpuPercent = Math.Max(0, (nowCpu - lastCpu).TotalSeconds / (elapsed * Math.Max(1, Environment.ProcessorCount)) * 100.0);
        samples.Add((cpuPercent, process.WorkingSet64));
        lastWall = nowWall;
        lastCpu = nowCpu;
    }

    private static Pipeline BuildWhipPipeline()
    {
        var target = PipelineTextBuilder.LastTarget ?? throw new InvalidOperationException("Target ausente.");
        var settings = PipelineTextBuilder.LastSettings ?? throw new InvalidOperationException("Configuração ausente.");
        var codec = GetVideoCodec(settings);
        var pipeline = new Pipeline("streamer-v02-whip");

        var whip = Make("whipsink", "whip", pipeline);
        Set(whip, "whip-endpoint", settings.WhipEndpoint);
        Set(whip, "auth-token", settings.BearerToken);
        Set(whip, "use-link-headers", true);

        var videoElements = BuildVideoSource(target, settings, pipeline, codec.RequiresSystemMemory);
        var encoder = BuildEncoder(settings, pipeline, "encoder", codec);
        var parse = Make(codec.ParserFactory, $"{codec.Name}-parse", pipeline);
        if (codec.Name is "h264" or "h265")
            Set(parse, "config-interval", -1);
        var pay = Make(codec.PayloaderFactory, $"{codec.Name}-pay", pipeline);
        if (codec.Name is "h264" or "h265")
            Set(pay, "config-interval", -1);
        Set(pay, "pt", codec.PayloadType);
        var outputQueue = Make("queue", "encoded-video-queue", pipeline);
        Set(outputQueue, "max-size-buffers", (uint)2);
        Set(outputQueue, "leaky", 2);
        Link(videoElements.Concat(new[] { encoder, parse, pay, outputQueue, whip }).ToArray());

        var audio = Make("wasapi2src", "process-audio", pipeline);
        Set(audio, "loopback", true);
        SetAudioLoopback(audio, target, settings);
        Set(audio, "low-latency", true);
        var audioConvert = Make("audioconvert", "audio-convert", pipeline);
        var audioResample = Make("audioresample", "audio-resample", pipeline);
        var audioCaps = Make("capsfilter", "audio-caps", pipeline);
        Set(audioCaps, "caps", Caps.FromString("audio/x-raw,format=S16LE,rate=48000,channels=2"));
        var volume = Make("volume", "audio-gain", pipeline);
        Set(volume, "volume", settings.AudioGain);
        var opus = Make("opusenc", "opus", pipeline);
        Set(opus, "bitrate", settings.AudioBitrateKbps * 1000);
        Set(opus, "frame-size", 20);
        var payAudio = Make("rtpopuspay", "opus-pay", pipeline);
        Set(payAudio, "pt", (uint)97);
        var audioQueue = Make("queue", "audio-queue", pipeline);
        Set(audioQueue, "max-size-buffers", (uint)4);
        Set(audioQueue, "leaky", 2);
        Link(audio, audioConvert, audioResample, audioCaps, volume, opus, payAudio, audioQueue, whip);
        return pipeline;
    }

    private static Pipeline BuildCapturePipeline()
    {
        var target = PipelineTextBuilder.LastTarget ?? throw new InvalidOperationException("Target ausente.");
        var settings = PipelineTextBuilder.LastSettings ?? throw new InvalidOperationException("Configuração ausente.");
        var outputPath = PipelineTextBuilder.LastOutputPath ?? throw new InvalidOperationException("Arquivo de teste ausente.");
        var codec = GetVideoCodec(settings);
        var pipeline = new Pipeline("streamer-v02-capture-test");

        var videoElements = BuildVideoSource(target, settings, pipeline, true);
        var encoder = BuildEncoder(settings, pipeline, "capture-encoder", codec);
        var parse = Make(codec.ParserFactory, "test-parse", pipeline);
        var mux = Make("matroskamux", "test-mux", pipeline);
        var sink = Make("filesink", "test-file", pipeline);
        Set(sink, "location", outputPath);
        Link(videoElements.Concat(new[] { encoder, parse, mux, sink }).ToArray());

        var audio = Make("wasapi2src", "process-audio", pipeline);
        Set(audio, "loopback", true);
        SetAudioLoopback(audio, target, settings);
        Set(audio, "low-latency", true);
        var audioConvert = Make("audioconvert", "audio-convert", pipeline);
        var audioResample = Make("audioresample", "audio-resample", pipeline);
        var audioCaps = Make("capsfilter", "audio-caps", pipeline);
        Set(audioCaps, "caps", Caps.FromString("audio/x-raw,format=S16LE,rate=48000,channels=2"));
        var volume = Make("volume", "audio-gain", pipeline);
        Set(volume, "volume", settings.AudioGain);
        var opus = Make("opusenc", "opus", pipeline);
        Set(opus, "bitrate", settings.AudioBitrateKbps * 1000);
        Set(opus, "frame-size", 20);
        var audioQueue = Make("queue", "audio-queue", pipeline);
        Set(audioQueue, "max-size-buffers", (uint)4);
        Set(audioQueue, "leaky", 2);
        Link(audio, audioConvert, audioResample, audioCaps, volume, opus, audioQueue, mux);
        return pipeline;
    }

    private static List<Element> BuildVideoSource(WindowTarget target, StreamSettings settings, Pipeline pipeline, bool requiresSystemMemory)
    {
        var screen = Make("d3d11screencapturesrc", "screen", pipeline);
        Set(screen, "capture-api", 1);
        if (target.SourceKind == VideoSourceKind.Monitor)
        {
            Set(screen, "monitor-index", target.MonitorIndex);
        }
        else
        {
            Set(screen, "window-handle", (ulong)target.Hwnd.ToInt64());
            Set(screen, "window-capture-mode", 1);
        }
        Set(screen, "show-cursor", false);
        var convert = Make("d3d11convert", "gpu-convert", pipeline);
        var elements = new List<Element> { screen, convert };

        var cpuScale = requiresSystemMemory || settings.Scale is ScaleMethod.Bicubic or ScaleMethod.Lanczos;
        if (cpuScale)
        {
            var download = Make("d3d11download", "gpu-download", pipeline);
            var cpuConvert = Make("videoconvert", "cpu-convert", pipeline);
            var scale = Make("videoscale", "cpu-scale", pipeline);
            Set(scale, "method", VideoScaleMethod(settings.Scale));
            Set(scale, "n-threads", (uint)Math.Clamp(Environment.ProcessorCount, 1, 4));
            var caps = Make("capsfilter", "system-video-caps", pipeline);
            Set(caps, "caps", Caps.FromString($"video/x-raw,format=NV12,width={settings.Width},height={settings.Height},framerate={settings.FramesPerSecond}/1"));
            elements.AddRange([download, cpuConvert, scale, caps]);
        }
        else
        {
            Set(convert, "method", GpuScaleMethod(settings.Scale));
            var caps = Make("capsfilter", "gpu-video-caps", pipeline);
            Set(caps, "caps", Caps.FromString($"video/x-raw(memory:D3D11Memory),format=NV12,width={settings.Width},height={settings.Height},framerate={settings.FramesPerSecond}/1"));
            elements.Add(caps);
        }

        var queue = Make("queue", "video-queue", pipeline);
        Set(queue, "max-size-buffers", (uint)2);
        Set(queue, "leaky", 2);
        elements.Add(queue);
        return elements;
    }

    private static void SetAudioLoopback(Element audio, WindowTarget target, StreamSettings settings)
    {
        if (settings.AudioSource == AudioSourceKind.SystemExceptDiscord)
        {
            var discordPid = WindowDiscovery.FindDiscordRootPid()
                ?? throw new InvalidOperationException("Discord was not found. Start Discord first or choose Selected app audio.");
            Set(audio, "loopback-mode", 2); // exclude-process-tree
            Set(audio, "loopback-target-pid", (uint)discordPid);
            return;
        }

        if (target.SourceKind != VideoSourceKind.Window || target.ProcessId <= 0)
            throw new InvalidOperationException("Selected app audio requires an application window. Choose System audio (except Discord) for a full-monitor capture.");

        Set(audio, "loopback-mode", 1); // include-process-tree
        Set(audio, "loopback-target-pid", (uint)target.ProcessId);
    }

    private static Element BuildEncoder(StreamSettings settings, Pipeline pipeline, string name, VideoCodecInfo codec)
    {
        var gop = Math.Max(1, settings.KeyframeIntervalSeconds * settings.FramesPerSecond);
        if (settings.Encoder is EncoderKind.H264Nvenc or EncoderKind.HevcNvenc)
        {
            if (settings.RateControl == RateControl.Crf)
                throw new NotSupportedException("CRF não existe no NVENC; use CBR, VBR ou CQP.");
            var encoder = Make(codec.EncoderFactory, name, pipeline);
            SetNvencRateControl(encoder, settings.RateControl);
            Set(encoder, "bitrate", (uint)settings.VideoBitrateKbps);
            Set(encoder, "preset", NvencPresetValue(settings.EncoderPreset));
            Set(encoder, "tune", 3);
            SetNvencBFrames(encoder, settings.BFrames);
            Set(encoder, "rc-lookahead", (uint)0);
            Set(encoder, "gop-size", gop);
            SetNvencZeroLatency(encoder);
            Set(encoder, "repeat-sequence-header", true);
            return encoder;
        }

        if (settings.Encoder == EncoderKind.Av1Nvenc)
        {
            if (settings.RateControl == RateControl.Crf)
                throw new NotSupportedException("CRF não existe no AV1 NVENC; use CBR, VBR ou CQP.");
            var encoder = Make(codec.EncoderFactory, name, pipeline);
            SetNvencRateControl(encoder, settings.RateControl);
            Set(encoder, "bitrate", (uint)settings.VideoBitrateKbps);
            Set(encoder, "preset", NvencPresetValue(settings.EncoderPreset));
            Set(encoder, "tune", 3);
            SetNvencBFrames(encoder, settings.BFrames);
            Set(encoder, "rc-lookahead", (uint)0);
            Set(encoder, "gop-size", gop);
            Set(encoder, "zerolatency", true);
            return encoder;
        }

        if (settings.Encoder == EncoderKind.H264X264)
        {
            var encoder = Make("x264enc", name, pipeline);
            Set(encoder, "speed-preset", X264PresetValue(settings.EncoderPreset));
            Set(encoder, "tune", 4);
            Set(encoder, "bframes", (uint)settings.BFrames);
            Set(encoder, "key-int-max", gop);
            Set(encoder, "rc-lookahead", 0);
            Set(encoder, "sync-lookahead", 0);
            Set(encoder, "sliced-threads", true);
            Set(encoder, "threads", (uint)Math.Clamp(Environment.ProcessorCount, 1, 8));
            Set(encoder, "option-string", $"vbv-maxrate={settings.VideoBitrateKbps}:vbv-bufsize={Math.Max(settings.VideoBitrateKbps * 2, 100)}");
            if (settings.RateControl == RateControl.Crf)
            {
                Set(encoder, "pass", 5);
                Set(encoder, "quantizer", (uint)Math.Clamp(settings.Crf, 0, 50));
            }
            else
            {
                Set(encoder, "pass", 0);
                Set(encoder, "bitrate", (uint)settings.VideoBitrateKbps);
            }
            return encoder;
        }

        throw new NotSupportedException($"Encoder {settings.Encoder} não está disponível neste bundle. Escolha um encoder habilitado na lista.");
    }

    private static VideoCodecInfo GetVideoCodec(StreamSettings settings)
    {
        var codec = settings.Encoder switch
        {
            EncoderKind.H264Nvenc => new VideoCodecInfo("h264", "nvd3d11h264enc", "h264parse", "rtph264pay", 96, false, true),
            EncoderKind.H264X264 => new VideoCodecInfo("h264", "x264enc", "h264parse", "rtph264pay", 96, true, false),
            EncoderKind.HevcNvenc => new VideoCodecInfo("h265", "nvd3d11h265enc", "h265parse", "rtph265pay", 98, false, true),
            EncoderKind.Av1Nvenc => Av1Codec(),
            _ => throw new NotSupportedException($"Encoder {settings.Encoder} não está disponível neste bundle. Escolha um encoder habilitado na lista.")
        };

        if (!HasFactory(codec.EncoderFactory))
            throw new NotSupportedException($"O encoder {settings.Encoder} exige o plugin GStreamer {codec.EncoderFactory}, que não está disponível nesta release.");
        return codec;
    }

    private static VideoCodecInfo Av1Codec()
    {
        var factory = FindAv1EncoderFactory()
            ?? throw new NotSupportedException("AV1 NVENC exige GStreamer 1.26+ com nvd3d11av1enc/nvautogpuav1enc/nvav1enc; esta instalação não expõe um encoder AV1 NVIDIA por hardware.");
        // The CUDA-only factory accepts system memory, while the D3D11 and
        // auto-GPU factories can stay on the capture device without a download.
        return new VideoCodecInfo("av1", factory, "av1parse", "rtpav1pay", 99, factory == "nvav1enc", true);
    }

    private static Element Make(string factory, string name, Pipeline pipeline)
    {
        var element = ElementFactory.Make(factory, name)
            ?? throw new InvalidOperationException($"Plugin GStreamer ausente: {factory}");
        if (!pipeline.Add(element))
            throw new InvalidOperationException($"Não consegui adicionar {factory} ao pipeline.");
        return element;
    }

    private static void Set(Element element, string property, object value)
    {
        try { element[property] = value; }
        catch (Exception ex) { throw new InvalidOperationException($"Falha configurando {element.Name}.{property}: {ex.Message}", ex); }
    }

    private static void SetNvencRateControl(Element element, RateControl rateControl)
    {
        // GStreamer 1.26+ exposes the common NVENC property as rc-mode with
        // GstNvEncoderRCMode values. Older bundles used rate-control and a
        // different enum numbering. Keep both mappings correct so a locally
        // built app does not silently turn CQP/VBR into the wrong mode.
        if (HasProperty(element, "rc-mode"))
        {
            Set(element, "rc-mode", rateControl switch
            {
                RateControl.Cqp => 1, // constqp
                RateControl.Vbr => 3, // vbr
                _ => 2                // cbr
            });
            return;
        }

        if (HasProperty(element, "rate-control"))
        {
            Set(element, "rate-control", rateControl switch
            {
                RateControl.Cqp => 0,
                RateControl.Vbr => 1,
                _ => 2
            });
            return;
        }

        throw new NotSupportedException($"O encoder {element.Name} não expõe uma propriedade de rate control NVENC conhecida.");
    }

    private static void SetNvencBFrames(Element element, int bFrames)
    {
        if (HasProperty(element, "bframes"))
        {
            Set(element, "bframes", (uint)bFrames);
            return;
        }

        Set(element, "b-frames", (uint)bFrames);
    }

    private static void SetNvencZeroLatency(Element element)
    {
        if (HasProperty(element, "zerolatency"))
        {
            Set(element, "zerolatency", true);
            return;
        }

        Set(element, "zero-reorder-delay", true);
    }

    private static bool HasProperty(Element element, string property)
    {
        try
        {
            element.GetProperty(property);
            return true;
        }
        catch (PropertyNotFoundException)
        {
            return false;
        }
    }

    private static void Link(params Element[] elements)
    {
        for (var i = 0; i + 1 < elements.Length; i++)
            if (!elements[i].Link(elements[i + 1]))
                throw new InvalidOperationException($"Falha ligando {elements[i].Name} -> {elements[i + 1].Name}.");
    }

    private static int NvencPresetValue(string preset) => preset.ToLowerInvariant() switch
    {
        "p1" => 8, "p2" => 9, "p3" => 10, "p4" => 11, "p5" => 12, "p6" => 13, "p7" => 14,
        "low-latency" => 3, "low-latency-hq" => 4, "low-latency-hp" => 5, _ => 8
    };

    private static int X264PresetValue(string preset) => preset.ToLowerInvariant() switch
    {
        "ultrafast" => 1, "superfast" => 2, "veryfast" => 3, "faster" => 4,
        "fast" => 5, "medium" => 6, "slow" => 7, "slower" => 8, "veryslow" => 9,
        "placebo" => 10, _ => 1
    };

    private static int VideoScaleMethod(ScaleMethod method) => method switch
    {
        ScaleMethod.None => 0,
        ScaleMethod.Bilinear => 1,
        ScaleMethod.Bicubic => 8,
        ScaleMethod.Lanczos => 3,
        _ => 1
    };

    private static int GpuScaleMethod(ScaleMethod method) => method switch
    {
        ScaleMethod.None => 0,
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

internal static class PipelineTextBuilder
{
    public static WindowTarget? LastTarget { get; private set; }
    public static StreamSettings? LastSettings { get; private set; }
    public static string? LastOutputPath { get; private set; }

    public static string BuildWindowWhip(WindowTarget target, StreamSettings settings)
    {
        LastTarget = target;
        LastSettings = settings;
        LastOutputPath = null;
        var endpoint = Quote(settings.WhipEndpoint);
        var token = Quote(settings.BearerToken);
        var fps = settings.FramesPerSecond;
        var codec = CodecText(settings);
        var videoCaps = codec.Name switch
        {
            "h264" => "video/x-h264,profile=constrained-baseline,stream-format=byte-stream,alignment=au",
            "h265" => "video/x-h265,stream-format=byte-stream,alignment=au",
            _ => "video/x-av1,stream-format=obu-stream,alignment=tu"
        };

        var source = target.SourceKind == VideoSourceKind.Monitor
            ? $"d3d11screencapturesrc name=screen capture-api=wgc monitor-index={target.MonitorIndex} show-cursor=false"
            : $"d3d11screencapturesrc name=screen capture-api=wgc window-handle={target.Hwnd.ToInt64()} window-capture-mode=client show-cursor=false";
        var audio = AudioText(settings, target);
        return $"whipsink whip-endpoint={endpoint} auth-token={token} use-link-headers=true " +
               $"{source} ! d3d11convert ! video/x-raw(memory:D3D11Memory),format=NV12,width={settings.Width},height={settings.Height},framerate={fps}/1 ! queue max-size-buffers=2 leaky=downstream ! {codec.Encoder} ! {codec.Parser} ! {codec.Payloader} pt={codec.PayloadType} ! {videoCaps} ! queue max-size-buffers=2 leaky=downstream ! whip. " +
               $"{audio} ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! volume volume={settings.AudioGain.ToString(CultureInfo.InvariantCulture)} ! opusenc bitrate={settings.AudioBitrateKbps * 1000} frame-size=20 ! audio/x-opus,rate=48000,channels=2 ! queue max-size-buffers=4 leaky=downstream ! whip.";
    }

    public static string BuildLocalCapture(WindowTarget target, StreamSettings settings, string outputPath)
    {
        LastTarget = target;
        LastSettings = settings;
        LastOutputPath = Path.GetFullPath(outputPath);
        var codec = CodecText(settings);
        var source = target.SourceKind == VideoSourceKind.Monitor
            ? $"d3d11screencapturesrc capture-api=wgc monitor-index={target.MonitorIndex} show-cursor=false"
            : $"d3d11screencapturesrc capture-api=wgc window-handle={target.Hwnd.ToInt64()} window-capture-mode=client show-cursor=false";
        var audio = AudioText(settings, target);
        return $"{source} ! d3d11convert ! d3d11download ! videoconvert ! videoscale method={settings.Scale.ToString().ToLowerInvariant()} ! video/x-raw,format=NV12,width={settings.Width},height={settings.Height},framerate={settings.FramesPerSecond}/1 ! queue max-size-buffers=2 leaky=downstream ! {codec.Encoder} ! {codec.Parser} ! matroskamux ! filesink location=\"{LastOutputPath}\" " +
               $"{audio} ! audioconvert ! audioresample ! audio/x-raw,format=S16LE,rate=48000,channels=2 ! volume volume={settings.AudioGain.ToString(CultureInfo.InvariantCulture)} ! opusenc bitrate={settings.AudioBitrateKbps * 1000} frame-size=20 ! matroskamux.";
    }

    private static string AudioText(StreamSettings settings, WindowTarget target)
    {
        if (settings.AudioSource == AudioSourceKind.SystemExceptDiscord)
        {
            var discordPid = WindowDiscovery.FindDiscordRootPid()
                ?? throw new InvalidOperationException("Discord was not found. Start Discord first or choose Selected app audio.");
            return $"wasapi2src loopback=true loopback-mode=exclude-process-tree loopback-target-pid={discordPid} low-latency=true";
        }

        if (target.SourceKind != VideoSourceKind.Window || target.ProcessId <= 0)
            throw new InvalidOperationException("Selected app audio requires an application window. Choose System audio (except Discord) for a full-monitor capture.");
        return $"wasapi2src loopback=true loopback-mode=include-process-tree loopback-target-pid={target.ProcessId} low-latency=true";
    }

    private sealed record CodecTextInfo(string Name, string Encoder, string Parser, string Payloader, uint PayloadType);

    private static CodecTextInfo CodecText(StreamSettings settings)
    {
        var preset = string.IsNullOrWhiteSpace(settings.EncoderPreset) ? "p1" : settings.EncoderPreset;
        var gop = Math.Max(1, settings.KeyframeIntervalSeconds * settings.FramesPerSecond);
        return settings.Encoder switch
        {
            EncoderKind.H264X264 => new("h264", $"x264enc speed-preset={preset} tune=zerolatency bframes={settings.BFrames} key-int-max={gop} rc-lookahead=0 sync-lookahead=0 sliced-threads=true", "h264parse config-interval=-1", "rtph264pay config-interval=-1", 96),
            EncoderKind.H264Nvenc => new("h264", $"nvd3d11h264enc rc-mode={NvencRateControlText(settings.RateControl)} bitrate={settings.VideoBitrateKbps} preset={preset} tune=ultra-low-latency bframes={settings.BFrames} rc-lookahead=0 gop-size={gop} zerolatency=true repeat-sequence-header=true", "h264parse config-interval=-1", "rtph264pay config-interval=-1", 96),
            EncoderKind.HevcNvenc => new("h265", $"nvd3d11h265enc rc-mode={NvencRateControlText(settings.RateControl)} bitrate={settings.VideoBitrateKbps} preset={preset} tune=ultra-low-latency bframes={settings.BFrames} rc-lookahead=0 gop-size={gop} zerolatency=true repeat-sequence-header=true", "h265parse config-interval=-1", "rtph265pay config-interval=-1", 98),
            EncoderKind.Av1Nvenc => new("av1", $"nvd3d11av1enc rc-mode={NvencRateControlText(settings.RateControl)} bitrate={settings.VideoBitrateKbps} preset={preset} tune=ultra-low-latency bframes={settings.BFrames} rc-lookahead=0 gop-size={gop} zerolatency=true", "av1parse", "rtpav1pay", 99),
            _ => throw new NotSupportedException($"Encoder {settings.Encoder} não está disponível neste bundle.")
        };
    }

    private static string NvencRateControlText(RateControl rateControl) => rateControl switch
    {
        RateControl.Cqp => "constqp",
        RateControl.Vbr => "vbr",
        _ => "cbr"
    };

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
