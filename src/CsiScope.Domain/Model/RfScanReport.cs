using System.Collections.Immutable;

namespace CsiScope.Domain.Model;

/// <summary>One top transmitter from an rf_scan frame — MAC + its packet count.</summary>
public readonly record struct RfScanTopMac(MacAddress Mac, int Packets);

/// <summary>
/// A single-channel spectrum dwell's result: total packets heard, average RSSI,
/// and the loudest transmitters. This is the survey's only signal — DIAG_RF
/// scans emit no CSI, so channel activity and candidate MACs arrive via this.
/// </summary>
public readonly record struct RfScanReport(
    MacAddress Node,
    WifiChannel Channel,
    int Packets,
    double RssiAvg,
    ImmutableArray<RfScanTopMac> TopMacs);
