namespace StreamerV2;

public enum EncoderKind
{
    H264Nvenc,
    H264QuickSync,
    H264Amf,
    H264MediaFoundation,
    H264X264,
    HevcNvenc,
    Av1Nvenc
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
    AudioSourceKind AudioSource = AudioSourceKind.SelectedProcess);
