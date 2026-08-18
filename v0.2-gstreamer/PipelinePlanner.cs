using System.Text;

namespace StreamerV2;

public static class PipelinePlanner
{
    public static string Describe(WindowTarget target, StreamSettings settings)
    {
        var text = new StringBuilder();
        text.AppendLine("PIPELINE SPECIFICATION - not gst-launch syntax");
        text.AppendLine(target.SourceKind == VideoSourceKind.Monitor
            ? $"Target: MONITOR={target.MonitorIndex}, {target.DisplayName}"
            : $"Target: HWND=0x{target.Hwnd:X}, PID={target.ProcessId}, {target.DisplayName}");
        text.AppendLine(target.SourceKind == VideoSourceKind.Monitor
            ? $"Video: d3d11screencapturesrc(capture-api=wgc, monitor-index={target.MonitorIndex})"
            : $"Video: d3d11screencapturesrc(capture-api=wgc, window-handle={target.Hwnd})");
        text.AppendLine($"    -> d3d11convert({settings.Scale})");
        text.AppendLine($"    -> D3D11/NV12 {settings.Width}x{settings.Height}@{settings.FramesPerSecond}");
        text.AppendLine($"    -> {settings.Encoder} {settings.RateControl} {settings.VideoBitrateKbps} kbps, " +
                        $"keyint={settings.KeyframeIntervalSeconds}s, preset={settings.EncoderPreset}, bframes={settings.BFrames}");
        text.AppendLine(settings.AudioSource == AudioSourceKind.SystemExceptDiscord
            ? "Audio: wasapi2src(loopback-target-pid=<Discord root>, exclude-process-tree, low-latency)"
            : $"Audio: wasapi2src(loopback-target-pid={target.ProcessId}, include-process-tree, low-latency)");
        text.AppendLine($"    -> 48 kHz stereo, gain={settings.AudioGain:0.##}x, Opus={settings.AudioBitrateKbps} kbps");
        text.AppendLine($"Transport: whipsink explicit RTP({settings.WhipEndpoint}, bearer-token-present={!string.IsNullOrWhiteSpace(settings.BearerToken)})");
        text.AppendLine($"Network: adaptive={settings.AdaptiveNetwork}, fec={settings.ForwardErrorCorrection}, " +
                        $"rtx={settings.Retransmission}, degradation={settings.Degradation}");
        return text.ToString();
    }
}
