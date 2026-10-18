namespace StreamerV2;

/// <summary>Per-second network measurements from the running pipeline.</summary>
public readonly record struct NetworkSample(
    double IntervalSeconds,
    // From the viewer-side RTCP receiver reports; null until the first report.
    double? PacketLoss,
    double? RoundTripMs);

public readonly record struct BitrateDecision(int? NewBitrateKbps, string? Reason);

/// <summary>
/// The only thing adjusted while streaming: the video bitrate, and only in
/// response to the network. Resolution, FPS, scaling and encoder always stay
/// what the user chose. The bitrate never goes above the user's value; it
/// drops quickly on sustained packet loss and climbs back slowly once the
/// link is clean, remembering where it failed so it does not keep hitting
/// the same wall.
/// </summary>
public sealed class BitrateController
{
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

    private readonly int _targetKbps;
    private readonly bool _enabled;
    private double _now;
    private double _lastNetworkCut = double.NegativeInfinity;
    private double _cleanNetworkSeconds;
    private double _networkFactor = 1;
    // Bitrate share (of the user's target) that caused loss last time. Probing
    // back to full speed within seconds just hits the same wall again, so the
    // ceiling sits a bit under the failing point and relaxes slowly.
    private double _factorCeiling = 1;
    private double _lastCeilingRelax;
    private double _baseRttMs = double.MaxValue;
    private double _baseLoss = double.MaxValue;
    private int _congestedSamples;
    private int _bitrateKbps;

    public BitrateController(int targetKbps, bool enabled)
    {
        _targetKbps = Math.Max(100, targetKbps);
        _enabled = enabled;
        _bitrateKbps = _targetKbps;
    }

    public int BitrateKbps => _bitrateKbps;

    public BitrateDecision Tick(NetworkSample sample)
    {
        _now += sample.IntervalSeconds;
        if (!_enabled || sample.IntervalSeconds <= 0) return default;

        var reason = UpdateNetwork(sample);
        var bitrate = TargetBitrate();
        if (bitrate == _bitrateKbps) return default;
        _bitrateKbps = bitrate;
        return new BitrateDecision(bitrate, reason);
    }

    private string? UpdateNetwork(NetworkSample sample)
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

    private int TargetBitrate() =>
        Math.Min(_targetKbps, Math.Max(250, (int)Math.Round(_targetKbps * _networkFactor / 50.0) * 50));
}
