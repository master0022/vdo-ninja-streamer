namespace StreamerV2;

/// <summary>One step of the quality ladder.</summary>
public sealed record QualityLevel(int Width, int Height, int FramesPerSecond, int BitrateKbps)
{
    public override string ToString() => $"{Width}x{Height}@{FramesPerSecond} {BitrateKbps} kbps";
}

/// <summary>Per-second measurements taken from the running pipeline.</summary>
public readonly record struct AdaptiveSample(
    double IntervalSeconds,
    long FramesEncoded,
    double SystemCpuPercent,
    // From the viewer-side RTCP receiver reports; null until the first report.
    double? PacketLoss,
    double? RoundTripMs);

public readonly record struct AdaptiveDecision(QualityLevel? NewLevel, int? NewBitrateKbps, string? Reason);

/// <summary>
/// Picks resolution/FPS (compute pressure) and bitrate (network pressure)
/// independently. Steps down fast when a bottleneck is sustained, steps up
/// slowly when everything has been calm, and remembers levels that already
/// failed so it does not oscillate around a ceiling the PC cannot hold.
/// </summary>
public sealed class AdaptiveController
{
    // Thresholds are deliberately conservative: a few dropped frames during a
    // scene change must not cause a resolution switch the viewers will notice.
    private const double EncodedFpsFloor = 0.85;
    private const double CpuSaturatedPercent = 92;
    private const double CpuCalmPercent = 70;
    private const int PressureSecondsToStepDown = 3;
    private const double MinSecondsBetweenLevelChanges = 6;
    private const double CalmSecondsToStepUp = 25;
    private const double FailedLevelMemorySeconds = 300;
    // Broadcast Box reports a steady few percent of "loss" tied to keyframes
    // even on a clean link, so congestion is loss rising above this
    // connection's own baseline (or plainly high loss), sustained.
    private const double LossAboveBaselineCongested = 0.03;
    private const double LossAlwaysCongested = 0.15;
    private const double LossAboveBaselineClean = 0.02;
    private const int CongestedSamplesToAct = 2;
    // One burst this large is already a visible freeze for viewers; do not wait for a second sample.
    private const double LossSpikeActsImmediately = 0.08;
    private const double RttCongestionMarginMs = 250;
    private const double MinNetworkFactor = 0.35;

    private readonly IReadOnlyList<QualityLevel> _levels;
    private readonly bool _enabled;
    private int _index;
    private int _ceilingIndex;
    private double _ceilingExpiresAt = double.MaxValue;
    private double _now;
    private double _lastLevelChange = double.NegativeInfinity;
    private double _lastStepUp = double.NegativeInfinity;
    private double _lastNetworkCut = double.NegativeInfinity;
    private double _computePressure;
    private double _calmSeconds;
    private double _cleanNetworkSeconds;
    private double _networkFactor = 1;
    // Bitrate share (of the level's target) that caused loss last time. Probing
    // back to full speed within seconds just hits the same wall again, so the
    // ceiling sits a bit under the failing point and relaxes slowly.
    private double _factorCeiling = 1;
    private double _lastCeilingRelax;
    private double _baseRttMs = double.MaxValue;
    private double _baseLoss = double.MaxValue;
    private int _congestedSamples;
    private int _bitrateKbps;

    public AdaptiveController(IReadOnlyList<QualityLevel> levels, int startIndex, bool enabled)
    {
        if (levels.Count == 0) throw new ArgumentException("At least one level is required.", nameof(levels));
        _levels = levels;
        _enabled = enabled;
        _index = Math.Clamp(startIndex, 0, levels.Count - 1);
        _bitrateKbps = Current.BitrateKbps;
    }

    public QualityLevel Current => _levels[_index];
    public int BitrateKbps => _bitrateKbps;

    public AdaptiveDecision Tick(AdaptiveSample sample)
    {
        _now += sample.IntervalSeconds;
        if (!_enabled || sample.IntervalSeconds <= 0) return default;
        if (_now >= _ceilingExpiresAt)
        {
            _ceilingIndex = 0;
            _ceilingExpiresAt = double.MaxValue;
        }

        var encodedFps = sample.FramesEncoded / sample.IntervalSeconds;
        var encoderBehind = encodedFps < Current.FramesPerSecond * EncodedFpsFloor;
        var cpuSaturated = sample.SystemCpuPercent >= CpuSaturatedPercent;
        if (encoderBehind || cpuSaturated)
        {
            _computePressure += sample.IntervalSeconds;
            _calmSeconds = 0;
        }
        else
        {
            _computePressure = Math.Max(0, _computePressure - sample.IntervalSeconds);
            _calmSeconds = sample.SystemCpuPercent < CpuCalmPercent ? _calmSeconds + sample.IntervalSeconds : 0;
        }

        var networkReason = UpdateNetwork(sample);
        var canChangeLevel = _now - _lastLevelChange >= MinSecondsBetweenLevelChanges;

        if (_computePressure >= PressureSecondsToStepDown && canChangeLevel && _index < _levels.Count - 1)
        {
            // Stepping up and immediately struggling means the previous level
            // is this PC's real ceiling for now; stop retrying it for a while.
            if (_now - _lastStepUp < 20)
            {
                _ceilingIndex = _index + 1;
                _ceilingExpiresAt = _now + FailedLevelMemorySeconds;
            }
            var reason = encoderBehind
                ? $"encoder could not keep up ({encodedFps:0} of {Current.FramesPerSecond} fps)"
                : $"CPU is saturated ({sample.SystemCpuPercent:0}%)";
            return ChangeLevel(_index + 1, reason);
        }

        // Too few bits for this resolution looks worse than a smaller, clean
        // picture, so a sustained network cut also lowers the resolution.
        if (_networkFactor <= 0.5 && canChangeLevel && _index < _levels.Count - 1)
        {
            _networkFactor = Math.Min(1, _networkFactor * 1.6);
            return ChangeLevel(_index + 1, "upload is congested");
        }

        if (_index > _ceilingIndex && canChangeLevel && _calmSeconds >= CalmSecondsToStepUp && _networkFactor >= 0.95 && _factorCeiling >= 0.95)
        {
            _lastStepUp = _now;
            _calmSeconds = 0;
            return ChangeLevel(_index - 1, "PC and network have headroom");
        }

        var bitrate = TargetBitrate();
        if (bitrate != _bitrateKbps)
        {
            _bitrateKbps = bitrate;
            return new AdaptiveDecision(null, bitrate, networkReason);
        }
        return default;
    }

    private string? UpdateNetwork(AdaptiveSample sample)
    {
        if (sample.RoundTripMs is { } rtt && rtt > 0) _baseRttMs = Math.Min(_baseRttMs, rtt);
        var rttCongested = sample.RoundTripMs is { } r && r > 0 && _baseRttMs < double.MaxValue && r > _baseRttMs + RttCongestionMarginMs;

        var excessLoss = 0.0;
        if (sample.PacketLoss is { } loss)
        {
            // The baseline follows the lowest loss seen and creeps up slowly,
            // so a link whose floor is 4% is judged against 4%, not 0%.
            _baseLoss = _baseLoss == double.MaxValue ? loss : Math.Min(loss, _baseLoss + 0.002 * sample.IntervalSeconds);
            excessLoss = loss >= LossAlwaysCongested ? loss : loss - _baseLoss;
        }

        var congested = excessLoss >= LossAboveBaselineCongested || rttCongested;
        _congestedSamples = excessLoss >= LossSpikeActsImmediately ? CongestedSamplesToAct : congested ? _congestedSamples + 1 : 0;
        if (_congestedSamples >= CongestedSamplesToAct && _now - _lastNetworkCut >= 3)
        {
            _factorCeiling = Math.Max(MinNetworkFactor, _networkFactor * 0.85);
            _lastCeilingRelax = _now;
            _networkFactor = Math.Max(MinNetworkFactor, _networkFactor * 0.75);
            _lastNetworkCut = _now;
            _cleanNetworkSeconds = 0;
            _calmSeconds = 0;
            return rttCongested ? $"latency rose to {sample.RoundTripMs:0} ms" : $"packet loss {sample.PacketLoss:P0}";
        }

        if (excessLoss < LossAboveBaselineClean && !rttCongested)
        {
            _cleanNetworkSeconds += sample.IntervalSeconds;
            if (_factorCeiling < 1 && _now - _lastCeilingRelax >= 60 && _now - _lastNetworkCut >= 60)
            {
                _factorCeiling = Math.Min(1, _factorCeiling + 0.05);
                _lastCeilingRelax = _now;
            }
            if (_cleanNetworkSeconds >= 8 && _networkFactor < _factorCeiling)
            {
                _networkFactor = Math.Min(_factorCeiling, _networkFactor + 0.04);
                _cleanNetworkSeconds = 3;
                return "network recovered";
            }
        }
        else
        {
            _cleanNetworkSeconds = 0;
        }
        return null;
    }

    private AdaptiveDecision ChangeLevel(int index, string reason)
    {
        _index = Math.Clamp(index, 0, _levels.Count - 1);
        _lastLevelChange = _now;
        _computePressure = 0;
        _bitrateKbps = TargetBitrate();
        return new AdaptiveDecision(Current, _bitrateKbps, reason);
    }

    private int TargetBitrate() =>
        Math.Max(250, (int)Math.Round(Current.BitrateKbps * _networkFactor / 50.0) * 50);

    /// <summary>
    /// Builds the ladder for a mode. Sizes keep the source aspect ratio, never
    /// upscale, and stay even (required by NV12/H.264).
    /// </summary>
    public static IReadOnlyList<QualityLevel> BuildLadder(StreamMode mode, int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0) (sourceWidth, sourceHeight) = (1920, 1080);
        var (maxWidth, maxHeight, maxKbps) = mode == StreamMode.Sharp ? (3840, 2160, 9000) : (1280, 720, 2500);

        var steps = new (int Height, int Fps)[]
        {
            (2160, 30), (1440, 30), (1080, 30), (900, 30), (720, 30), (540, 30), (432, 24), (360, 20)
        };
        var levels = new List<QualityLevel>();
        foreach (var (boxHeight, fps) in steps)
        {
            var boxWidth = boxHeight * 16 / 9;
            var scale = Math.Min(1.0, Math.Min(
                Math.Min(boxWidth, maxWidth) / (double)sourceWidth,
                Math.Min(boxHeight, maxHeight) / (double)sourceHeight));
            var width = Even(sourceWidth * scale);
            var height = Even(sourceHeight * scale);
            if (levels.Count > 0 && levels[^1].Width == width && levels[^1].Height == height && levels[^1].FramesPerSecond == fps)
                continue;
            // ~0.08 bits per pixel per frame suits screen content and games at
            // low latency; capped per mode so a 4K screen does not ask for 20 Mbps.
            var kbps = (int)Math.Clamp(width * (double)height * fps * 0.08 / 1000, 350, maxKbps);
            levels.Add(new QualityLevel(width, height, fps, kbps / 50 * 50));
        }
        return levels;
    }

    /// <summary>
    /// Where to start before any measurement exists. CPU encoding at high
    /// resolutions is what made older PCs stutter, so it starts at 720p or less
    /// and only climbs if the controller later sees headroom.
    /// </summary>
    public static int StartIndex(IReadOnlyList<QualityLevel> levels, bool hardwareEncoder)
    {
        if (hardwareEncoder) return 0;
        var maxHeight = Environment.ProcessorCount <= 4 ? 540 : 720;
        var index = levels.ToList().FindIndex(l => l.Height <= maxHeight);
        return index < 0 ? levels.Count - 1 : index;
    }

    private static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
}
