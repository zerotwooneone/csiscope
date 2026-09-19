using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiScope.Infrastructure;

/// <summary>Result of probing one serial port for a CSI node.</summary>
public sealed record ProbedNode(
    string PortName,
    string? Mac,
    string? State,
    int? Baud,
    string? Version,
    string? Error);

/// <summary>
/// Control-plane for node discovery and array assignment. Enumerates COM
/// ports, probes each with get_config (the firmware announces itself via
/// config/hb frames), and tracks position → port assignments. Probing must
/// only run while the worker is stopped — the worker owns assigned ports
/// during a session.
/// </summary>
public sealed class NodeRegistryService
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2);

    private readonly int _baudRate;
    private readonly object _gate = new();
    private readonly Dictionary<int, string> _assignments = new(); // position → port
    private readonly ILogger<NodeRegistryService>? _logger;

    public NodeRegistryService(IOptions<SensingOptions> options, ILogger<NodeRegistryService>? logger = null)
    {
        _baudRate = options.Value.SerialBaudRate;
        _logger = logger;

        // Seed assignments from configured port order — position i gets the
        // i-th configured port, preserving the legacy deployment layout.
        var seed = options.Value.SerialPortNames;
        for (var i = 0; i < seed.Count; i++)
        {
            _assignments[i] = seed[i];
        }
    }

    /// <summary>Position → port assignments (0-based positions).</summary>
    public IReadOnlyDictionary<int, string> Assignments
    {
        get { lock (_gate) { return new Dictionary<int, string>(_assignments); } }
    }

    /// <summary>Assigned ports ordered by array position — the worker's session set.</summary>
    public IReadOnlyList<string> AssignedPorts
    {
        get
        {
            lock (_gate)
            {
                return _assignments.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
            }
        }
    }

    /// <summary>Assign a port to an array position (null clears the position).</summary>
    public void Assign(int position, string? portName)
    {
        lock (_gate)
        {
            if (portName is null)
            {
                _assignments.Remove(position);
                return;
            }

            // A port can only hold one position — clear any existing claim.
            foreach (var kv in _assignments.Where(kv => kv.Value == portName).ToArray())
            {
                _assignments.Remove(kv.Key);
            }

            _assignments[position] = portName;
        }
    }

    /// <summary>All serial ports visible to the OS, naturally sorted (COM2 before COM10).</summary>
    public IReadOnlyList<string> EnumeratePorts()
        => SerialPort.GetPortNames()
            .OrderBy(NumericSuffix)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Opens a port, sends get_config, and listens for a config/hb frame.
    /// Never throws — failures surface as <see cref="ProbedNode.Error"/>.
    /// </summary>
    public async Task<ProbedNode> ProbeAsync(string portName, CancellationToken ct = default)
    {
        try
        {
            return await Task.Run(() => ProbeCore(portName, ct), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbedNode(portName, null, null, null, null, ex.Message);
        }
    }

    /// <summary>Probes every enumerated port in parallel.</summary>
    public async Task<IReadOnlyList<ProbedNode>> ProbeAllAsync(CancellationToken ct = default)
    {
        var ports = EnumeratePorts();
        var results = await Task.WhenAll(ports.Select(p => ProbeAsync(p, ct)));
        return results;
    }

    private ProbedNode ProbeCore(string portName, CancellationToken ct)
    {
        using var port = new SerialPort(portName, _baudRate)
        {
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 300,
            WriteTimeout = 1000,
        };

        try
        {
            port.Open();
        }
        catch (Exception ex)
        {
            return new ProbedNode(portName, null, null, null, null, $"open failed: {ex.Message}");
        }

        var cmd = SerialFrameCodec.EncodeFrame("{\"cmd\":\"get_config\"}"u8);
        port.Write(cmd, 0, cmd.Length);

        var deadline = DateTime.UtcNow + ProbeWindow;
        var received = new List<byte>();
        var buf = new byte[1024];
        ProbedNode? found = null;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && found is null)
        {
            int n;
            try
            {
                n = port.Read(buf, 0, buf.Length);
            }
            catch (TimeoutException)
            {
                continue;
            }

            received.AddRange(buf.Take(n));

            // Drain complete binary frames; any mac-bearing payload is a node.
            SerialFrameCodec.DrainFrames(received, payload =>
            {
                found ??= TryParseAnnouncement(portName, payload);
            });
        }

        if (found is not null)
        {
            _logger?.LogInformation("Probed {Port}: node {Mac} ({State})", portName, found.Mac, found.State);
            return found;
        }

        // Distinguish dead silence from bytes-that-aren't-frames (wrong baud, noise).
        return received.Count == 0
            ? new ProbedNode(portName, null, null, null, null, "no response")
            : new ProbedNode(portName, null, null, null, null, $"{received.Count} bytes, no valid frame");
    }

    private static ProbedNode? TryParseAnnouncement(string portName, ReadOnlySpan<byte> payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
            var root = doc.RootElement;

            // Any frame carrying a node MAC is proof of life — a streaming node
            // floods csi frames and never sends config/hb, so we can't gate on type.
            if (!root.TryGetProperty("mac", out var m) || m.GetString() is not { } mac)
            {
                return null;
            }

            return new ProbedNode(
                portName,
                mac,
                root.TryGetProperty("state", out var s) ? s.GetString() : null,
                root.TryGetProperty("baud", out var b) && b.TryGetInt32(out var baud) ? baud : null,
                root.TryGetProperty("version", out var v) ? v.GetString() : null,
                null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int NumericSuffix(string name)
    {
        var i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1]))
        {
            i--;
        }

        return i < name.Length && int.TryParse(name[i..], out var n) ? n : int.MaxValue;
    }
}
