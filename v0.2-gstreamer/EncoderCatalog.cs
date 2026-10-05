using Gst;

namespace StreamerV2;

/// <summary>
/// Every video encoder the app can drive, in the order automatic mode tries
/// them. GStreamer only registers a hardware factory when the matching GPU and
/// driver exist, so "factory present" is already a hardware probe; a factory
/// that is present but fails to open (old driver, session limit) is skipped at
/// stream start by the engine's fallback.
/// </summary>
internal static class EncoderCatalog
{
    public sealed record Entry(
        EncoderKind Kind,
        string Label,
        string Codec,
        string[] Factories,
        bool IsHardware,
        // False when the encoder needs system memory, so frames are downloaded
        // from the GPU and scaled on the CPU first.
        bool AcceptsD3D11);

    // H.264 first: every browser decodes it, so automatic mode never picks
    // HEVC/AV1 (viewers on unsupported browsers would get a black player).
    public static readonly Entry[] All =
    [
        new(EncoderKind.H264Nvenc, "H.264 NVIDIA NVENC", "h264", ["nvd3d11h264enc"], true, true),
        new(EncoderKind.H264Amf, "H.264 AMD AMF", "h264", ["amfh264enc"], true, true),
        new(EncoderKind.H264QuickSync, "H.264 Intel Quick Sync", "h264", ["qsvh264enc"], true, true),
        new(EncoderKind.H264MediaFoundation, "H.264 Windows Media Foundation", "h264", ["mfh264enc"], true, true),
        new(EncoderKind.H264X264, "H.264 x264 (CPU)", "h264", ["x264enc"], false, false),
        new(EncoderKind.HevcNvenc, "H.265 / HEVC NVENC", "h265", ["nvd3d11h265enc"], true, true),
        new(EncoderKind.Av1Nvenc, "AV1 NVENC", "av1", ["nvd3d11av1enc", "nvautogpuav1enc", "nvav1enc"], true, true),
    ];

    public static Entry Get(EncoderKind kind) =>
        All.FirstOrDefault(e => e.Kind == kind)
        ?? throw new NotSupportedException($"Encoder {kind} is not supported.");

    public static string? FindFactory(Entry entry) =>
        entry.Factories.FirstOrDefault(f => ElementFactory.Find(f) is not null);

    /// <summary>Encoders to try, best first, for the requested setting.</summary>
    public static IReadOnlyList<Entry> Candidates(EncoderKind requested) =>
        requested == EncoderKind.Auto
            ? All.Where(e => e.Codec == "h264" && FindFactory(e) is not null).ToArray()
            : [Get(requested)];

    /// <summary>
    /// Low-latency configuration shared by all vendors. Properties are set by
    /// name from strings so a vendor plugin that lacks one (e.g. a QSV build
    /// without "low-latency") is configured with what it does have.
    /// </summary>
    public static void Configure(Element encoder, Entry entry, StreamSettings settings, int bitrateKbps, int fps)
    {
        var gop = Math.Max(1, settings.KeyframeIntervalSeconds * fps).ToString();
        var bFrames = settings.BFrames.ToString();
        switch (entry.Kind)
        {
            case EncoderKind.H264Nvenc or EncoderKind.HevcNvenc or EncoderKind.Av1Nvenc:
                TrySet(encoder, "rc-mode", NvencRateControl(settings.RateControl));
                // p1 + ultra-low-latency is the lightest NVENC configuration;
                // the manual preset only applies when auto quality is off.
                TrySet(encoder, "preset", settings.AutoQuality || string.IsNullOrWhiteSpace(settings.EncoderPreset)
                    ? "p1"
                    : settings.EncoderPreset.ToLowerInvariant());
                TrySet(encoder, "tune", "ultra-low-latency");
                TrySet(encoder, "bframes", bFrames);
                TrySet(encoder, "rc-lookahead", "0");
                TrySet(encoder, "gop-size", gop);
                TrySet(encoder, "zerolatency", "true");
                TrySet(encoder, "repeat-sequence-header", "true");
                break;
            case EncoderKind.H264Amf:
                TrySet(encoder, "usage", "ultra-low-latency");
                TrySet(encoder, "rate-control", settings.RateControl == RateControl.Vbr ? "vbr" : "cbr");
                TrySet(encoder, "preset", "speed");
                TrySet(encoder, "b-frames", bFrames);
                TrySet(encoder, "gop-size", gop);
                break;
            case EncoderKind.H264QuickSync:
                TrySet(encoder, "rate-control", settings.RateControl == RateControl.Vbr ? "vbr" : "cbr");
                TrySet(encoder, "target-usage", "7"); // fastest
                TrySet(encoder, "low-latency", "true");
                TrySet(encoder, "b-frames", bFrames);
                TrySet(encoder, "gop-size", gop);
                break;
            case EncoderKind.H264MediaFoundation:
                TrySet(encoder, "rc-mode", settings.RateControl == RateControl.Vbr ? "pcvbr" : "cbr");
                TrySet(encoder, "low-latency", "true");
                TrySet(encoder, "quality-vs-speed", "0");
                TrySet(encoder, "bframes", bFrames);
                TrySet(encoder, "gop-size", gop);
                break;
            case EncoderKind.H264X264:
                TrySet(encoder, "speed-preset", settings.AutoQuality ? "ultrafast" : X264Preset(settings.EncoderPreset));
                TrySet(encoder, "tune", "zerolatency");
                TrySet(encoder, "bframes", bFrames);
                TrySet(encoder, "key-int-max", gop);
                TrySet(encoder, "rc-lookahead", "0");
                TrySet(encoder, "sync-lookahead", "0");
                TrySet(encoder, "sliced-threads", "true");
                TrySet(encoder, "threads", Math.Clamp(Environment.ProcessorCount, 1, 8).ToString());
                if (settings.RateControl == RateControl.Crf && !settings.AutoQuality)
                {
                    TrySet(encoder, "pass", "quant");
                    TrySet(encoder, "quantizer", Math.Clamp(settings.Crf, 0, 50).ToString());
                }
                else
                {
                    TrySet(encoder, "pass", "cbr");
                    TrySet(encoder, "vbv-buf-capacity", "1000");
                }
                break;
        }

        SetBitrate(encoder, entry, bitrateKbps);
    }

    /// <summary>
    /// Changes bitrate on a running encoder. NVENC, AMF, QSV and x264 all
    /// reconfigure on the fly without a new keyframe or a dropped session.
    /// </summary>
    public static void SetBitrate(Element encoder, Entry entry, int bitrateKbps)
    {
        TrySet(encoder, "bitrate", Math.Max(100, bitrateKbps).ToString());
        if (entry.Kind == EncoderKind.H264Amf)
            TrySet(encoder, "max-bitrate", Math.Max(100, bitrateKbps * 3 / 2).ToString());
    }

    private static void TrySet(Element element, string property, string value)
    {
        if (!HasProperty(element, property)) return;
        // gst_util_set_object_arg parses enum nicks, flags and numbers alike;
        // a value the plugin rejects just keeps its default.
        Gst.Util.SetObjectArg(element, property, value);
    }

    private static bool HasProperty(Element element, string property)
    {
        // The binding reports a missing property with different exception
        // types depending on the call path, so any failure means "absent".
        try { _ = element[property]; return true; }
        catch { return false; }
    }

    private static string NvencRateControl(RateControl rateControl) => rateControl switch
    {
        RateControl.Cqp => "constqp",
        RateControl.Vbr => "vbr",
        _ => "cbr"
    };

    private static string X264Preset(string preset)
    {
        var known = new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium" };
        var normalized = preset.Trim().ToLowerInvariant();
        return known.Contains(normalized) ? normalized : "ultrafast";
    }
}
