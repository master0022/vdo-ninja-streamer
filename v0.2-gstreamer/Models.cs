namespace StreamerV2;

public enum EncoderKind
{
    H264Nvenc,
    H264QuickSync,
    H264Amf,
    H264MediaFoundation,
    H264X264,
    HevcNvenc,
    Av1Nvenc,
    // Appended so values persisted as numbers by older builds keep their meaning.
    Auto
}

public enum StreamMode
{
    // Fixed 1280x720 ceiling: the light choice for older PCs and games.
    Performance720,
    // Up to the source's native resolution (max 4K) for sharp text/screens.
    Sharp
}

public sealed record EncoderOption(
    EncoderKind Value,
    string Label,
    bool Available,
    string Reason);

public enum ScaleMethod { None, Bilinear, Bicubic, Lanczos }
public enum RateControl { Cbr, Vbr, Cqp, Crf }
public enum NetworkDegradation { PreserveFramerate, Balanced, PreserveResolution }
public enum VideoSourceKind { Window, Monitor }
public enum AudioSourceKind { SelectedProcess, SystemExceptDiscord }

public sealed record WindowTarget(
    nint Hwnd,
    int ProcessId,
    string DisplayName,
    string ProcessName = "",
    string ClassName = "",
    VideoSourceKind SourceKind = VideoSourceKind.Window,
    int MonitorIndex = -1);

public sealed record StreamSettings(
    EncoderKind Encoder,
    int Width,
    int Height,
    int FramesPerSecond,
    int VideoBitrateKbps,
    int AudioBitrateKbps,
    double AudioGain,
    ScaleMethod Scale,
    RateControl RateControl,
    int KeyframeIntervalSeconds,
    string EncoderPreset,
    int BFrames,
    bool AdaptiveNetwork,
    bool ForwardErrorCorrection,
    bool Retransmission,
    NetworkDegradation Degradation,
    string WhipEndpoint,
    string BearerToken = "",
    int Crf = 23,
    VideoSourceKind VideoSource = VideoSourceKind.Window,
    int MonitorIndex = -1,
    AudioSourceKind AudioSource = AudioSourceKind.SelectedProcess,
    int SettingsVersion = 2,
    StreamMode Mode = StreamMode.Performance720,
    // When true the app picks encoder, resolution, FPS and bitrate itself and
    // keeps adjusting them; the manual fields only apply when this is false.
    bool AutoQuality = true);

/// <summary>What the stream is actually sending right now.</summary>
public sealed record LiveStreamInfo(
    string Encoder,
    int Width,
    int Height,
    int FramesPerSecond,
    int BitrateKbps,
    string? Adjustment);
