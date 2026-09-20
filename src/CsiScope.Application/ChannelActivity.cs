using CsiScope.Domain.Model;

namespace CsiScope.Application;

/// <summary>
/// Per-channel survey record — the orchestrator's internal environment map.
/// Tracks frame count, top transmitter, and window bounds so PPS reflects
/// the current dwell rather than a lifetime average.
/// </summary>
internal sealed class ChannelActivity
{
    private readonly Dictionary<MacAddress, int> _macCounts = new();
    private int _topCount;

    public ChannelActivity(WifiChannel channel, DateTimeOffset windowStartedAt)
    {
        Channel = channel;
        WindowStartedAt = windowStartedAt;
    }

    public WifiChannel Channel { get; }

    public int Frames { get; private set; }

    public DateTimeOffset WindowStartedAt { get; private set; }

    public DateTimeOffset LastFrameAt { get; private set; } = DateTimeOffset.MinValue;

    public MacAddress TopMac { get; private set; }

    /// <summary>O(1) per frame — dictionary hit, no allocation on the hot path.</summary>
    public void Record(MacAddress source, DateTimeOffset at)
    {
        Frames++;
        LastFrameAt = at;

        int count = _macCounts.TryGetValue(source, out var existing) ? existing + 1 : 1;
        _macCounts[source] = count;
        if (count > _topCount)
        {
            _topCount = count;
            TopMac = source;
        }
    }

    /// <summary>
    /// Fold a spectrum-scan dwell into the activity record — the scan reports a
    /// packet total (not per-frame events), so Frames jumps by the dwell count
    /// and the loudest transmitter becomes the channel's top MAC.
    /// </summary>
    public void RecordScan(int packets, MacAddress topMac, DateTimeOffset at)
    {
        Frames += packets;
        LastFrameAt = at;

        if (topMac == default)
        {
            return;
        }

        int count = _macCounts.TryGetValue(topMac, out var existing) ? existing + packets : packets;
        _macCounts[topMac] = count;
        if (count > _topCount)
        {
            _topCount = count;
            TopMac = topMac;
        }
    }

    public double Pps(DateTimeOffset now)
    {
        double seconds = (now - WindowStartedAt).TotalSeconds;
        return seconds > 0 ? Frames / seconds : 0.0;
    }

    /// <summary>Restart the measurement window — called when a hop lands on this channel.</summary>
    public void ResetWindow(DateTimeOffset now)
    {
        Frames = 0;
        WindowStartedAt = now;
        _macCounts.Clear();
        _topCount = 0;
    }
}
