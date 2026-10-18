namespace StreamerV2;

/// <summary>
/// A named set of video values. Choosing one in the UI copies every field
/// into the editable settings; the stream then uses exactly those values.
/// </summary>
public sealed record VideoPreset(
    string Name,
    int Width,
    int Height,
    int FramesPerSecond,
    int VideoBitrateKbps,
    ScaleMethod Scale,
    RateControl RateControl,
    int KeyframeIntervalSeconds,
    string EncoderPreset,
    int BFrames);

public static class Presets
{
    private const string Endpoint = "https://b.siobud.com/api/whip";

    public static readonly StreamSettings Defaults = new(
        EncoderKind.Auto, 1280, 720, 30, 2_500, 192, 2.0,
        ScaleMethod.Bilinear, RateControl.Cbr, 1, "p1", 0,
        true, true, true, NetworkDegradation.PreserveFramerate, Endpoint,
        "", 23, VideoSourceKind.Window, -1, AudioSourceKind.SelectedProcess)
    {
        SettingsVersion = CurrentSettingsVersion
    };

    public const int CurrentSettingsVersion = 3;

    // Encoder presets use NVENC names; x264 falls back to ultrafast for
    // names it does not know, and AMF/QSV/Media Foundation ignore them.
    public static readonly VideoPreset[] Video =
    [
        new("720p30 · light / older PCs", 1280, 720, 30, 2_500, ScaleMethod.Bilinear, RateControl.Cbr, 1, "p1", 0),
        new("720p60 · games", 1280, 720, 60, 4_000, ScaleMethod.Bilinear, RateControl.Cbr, 1, "p1", 0),
        new("1080p30 · screen and text", 1920, 1080, 30, 4_500, ScaleMethod.Bilinear, RateControl.Cbr, 1, "p4", 0),
        new("1080p60 · 4K source, Lanczos", 1920, 1080, 60, 8_000, ScaleMethod.Lanczos, RateControl.Cbr, 1, "p4", 0),
        new("4K30 · native", 3840, 2160, 30, 12_000, ScaleMethod.None, RateControl.Cbr, 1, "p4", 0),
    ];

    /// <summary>
    /// v3 reset the encoder to automatic selection: earlier builds asked people
    /// to pick one by hand and many picked one their PC did not have.
    /// </summary>
    public static StreamSettings Migrate(StreamSettings loaded) =>
        loaded.SettingsVersion >= CurrentSettingsVersion
            ? loaded
            : loaded with
            {
                Encoder = EncoderKind.Auto,
                SettingsVersion = CurrentSettingsVersion
            };
}
