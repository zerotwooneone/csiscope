using System.IO.Ports;
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
/// Control-plane for node discovery and array assignment. Persists the
/// position → MAC geometry to <c>array_geometry.json</c>, probes COM ports for
/// the MAC on each, and derives position → port assignments by matching probed
/// MACs against the configured geometry. When every configured MAC is found on
/// a port, assignment is fully automatic — no manual detect step needed.
/// </summary>
public sealed class NodeRegistryService
{
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly int _baudRate;
    private readonly string _configDirectory;
    private readonly string _geometryPath;
    private readonly object _gate = new();
    private readonly List<string?> _geometry;   // position → MAC (persisted)
    private List<ProbedNode> _probes = new();   // port → MAC (runtime)
    private readonly ILogger<NodeRegistryService>? _logger;

    public NodeRegistryService(
        IOptions<SensingOptions> options,
        ILogger<NodeRegistryService>? logger = null)
    {
        _baudRate = options.Value.SerialBaudRate;
        _logger = logger;

        // Geometry is user data — it lives in appdata, not a checked-in project file.
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "CsiScope");
        _geometryPath = Path.Combine(_configDirectory, "array-geometry.json");
        _geometry = LoadGeometry();
    }

    /// <summary>Number of array positions (slots).</summary>
    public int SlotCount
    {
        get { lock (_gate) { return _geometry.Count; } }
    }

    /// <summary>MAC assigned to a position, or null if empty.</summary>
    public string? GetSlotMac(int position)
    {
        lock (_gate)
        {
            return position >= 0 && position < _geometry.Count ? _geometry[position] : null;
        }
    }

    /// <summary>Latest probe results (port → MAC).</summary>
    public IReadOnlyList<ProbedNode> Probes
    {
        get { lock (_gate) { return _probes.ToArray(); } }
    }

    /// <summary>COM port a probed MAC was found on, or null.</summary>
    public string? PortForMac(string mac)
    {
        lock (_gate)
        {
            return _probes.FirstOrDefault(p =>
                string.Equals(p.Mac, mac, StringComparison.OrdinalIgnoreCase))?.PortName;
        }
    }

    /// <summary>MAC found on a probed port, or null.</summary>
    public string? MacForPort(string portName)
    {
        lock (_gate)
        {
            return _probes.FirstOrDefault(p =>
                string.Equals(p.PortName, portName, StringComparison.OrdinalIgnoreCase))?.Mac;
        }
    }

    /// <summary>
    /// Assigned ports ordered by array position — the worker's session set.
    /// Derived: position → configured MAC → probed port.
    /// </summary>
    public IReadOnlyList<string> AssignedPorts
    {
        get
        {
            lock (_gate)
            {
                return _geometry
                    .Select(mac => mac is null ? null : PortForMacUnsafe(mac))
                    .Where(p => p is not null)
                    .Select(p => p!)
                    .ToArray();
            }
        }
    }

    /// <summary>True when every configured position's MAC was found on a port.</summary>
    public bool AllAssigned
    {
        get
        {
            lock (_gate)
            {
                return _geometry.All(m => m is null || PortForMacUnsafe(m) is not null)
                    && _geometry.Any(m => m is not null);
            }
        }
    }

    /// <summary>Assign a MAC to a position (null clears). Persists geometry.</summary>
    public void Assign(int position, string? mac)
    {
        lock (_gate)
        {
            if (position < 0 || position >= _geometry.Count)
            {
                return;
            }

            if (mac is not null)
            {
                // A MAC holds one position — clear any existing claim.
                for (var i = 0; i < _geometry.Count; i++)
                {
                    if (string.Equals(_geometry[i], mac, StringComparison.OrdinalIgnoreCase))
                    {
                        _geometry[i] = null;
                    }
                }
            }

            _geometry[position] = mac;
            SaveGeometry();
        }
    }

    /// <summary>All serial ports visible to the OS, naturally sorted (COM2 before COM10).</summary>
    public IReadOnlyList<string> EnumeratePorts()
        => SerialPort.GetPortNames()
            .OrderBy(NumericSuffix)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Opens a port, sends a framed get_config, and listens for any mac-bearing
    /// frame. Never throws — failures surface as <see cref="ProbedNode.Error"/>.
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

    /// <summary>Probes every enumerated port in parallel and records the results.</summary>
    public async Task<IReadOnlyList<ProbedNode>> ProbeAllAsync(CancellationToken ct = default)
    {
        var ports = EnumeratePorts();
        var results = await Task.WhenAll(ports.Select(p => ProbeAsync(p, ct)));
        lock (_gate)
        {
            _probes = results.ToList();
        }

        return results;
    }

    // ---- persistence ----

    private List<string?> LoadGeometry()
    {
        try
        {
            if (File.Exists(_geometryPath))
            {
                var doc = JsonDocument.Parse(File.ReadAllText(_geometryPath));
                if (doc.RootElement.TryGetProperty("Positions", out var arr))
                {
                    return arr.EnumerateArray()
                        .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                        .ToList();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to load array geometry from {Path}", _geometryPath);
        }

        // Default: three positions matching the L-array (origin, +X, +Y).
        return new List<string?> { null, null, null };
    }

    private void SaveGeometry()
    {
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var payload = new { Positions = _geometry };
            File.WriteAllText(_geometryPath, JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to save array geometry to {Path}", _geometryPath);
        }
    }

    // ---- probing ----

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
            using var doc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(payload));
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

    private string? PortForMacUnsafe(string mac)
        => _probes.FirstOrDefault(p =>
            string.Equals(p.Mac, mac, StringComparison.OrdinalIgnoreCase))?.PortName;

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
