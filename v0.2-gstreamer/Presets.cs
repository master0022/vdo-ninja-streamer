namespace StreamerV2;

public static class Presets
{
    private const string Endpoint = "https://b.siobud.com/api/whip";

    public static readonly StreamSettings StableOldPc = new(
        EncoderKind.H264Nvenc, 1280, 720, 30, 2_000, 192, 2.0,
        ScaleMethod.Bilinear, RateControl.Cbr, 1, "low-latency", 0,
        true, true, true, NetworkDegradation.PreserveFramerate, Endpoint,
        "", 23, VideoSourceKind.Window, -1, AudioSourceKind.SelectedProcess)
    {
        Encoder = EncoderKind.Auto,
        SettingsVersion = CurrentSettingsVersion
    };

    public const int CurrentSettingsVersion = 3;

    /// <summary>
    /// v3 moved everyone to automatic quality: earlier builds asked people to
    /// tune encoder/bitrate by hand and almost nobody picked values that fit
    /// their PC. The stream key, endpoint, source and audio choices are kept.
    /// </summary>
    public static StreamSettings Migrate(StreamSettings loaded) =>
        loaded.SettingsVersion >= CurrentSettingsVersion
            ? loaded
            : loaded with
            {
                Encoder = EncoderKind.Auto,
                AutoQuality = true,
                Mode = StreamMode.Performance720,
                SettingsVersion = CurrentSettingsVersion
            };

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
