namespace StreamerV2;

public static class Presets
{
    private const string Endpoint = "https://b.siobud.com/api/whip";

    public static readonly StreamSettings StableOldPc = new(
        EncoderKind.H264Nvenc, 1280, 720, 30, 2_000, 192, 2.0,
        ScaleMethod.Bilinear, RateControl.Cbr, 1, "low-latency", 0,
        true, true, true, NetworkDegradation.PreserveFramerate, Endpoint,
        "", 23, VideoSourceKind.Window, -1, AudioSourceKind.SelectedProcess);

    public static readonly StreamSettings Motion60 = StableOldPc with
    {
        FramesPerSecond = 60,
        VideoBitrateKbps = 3_500
    };

    public static readonly StreamSettings FourKDownscale = Motion60 with
    {
        Scale = ScaleMethod.Lanczos,
        VideoBitrateKbps = 5_000
    };
}
