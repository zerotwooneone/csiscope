using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsiHub.Core;
using CsiHub.Ingestion;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Persists the user's origin/X-arm/Y-arm array assignment to
/// %LocalAppData%/CsiHub/array-geometry.json and derives the sensor-position map
/// the DSP service uses for AoA. Geometry is user data, so it lives in appdata —
/// not in a checked-in project config file.
/// </summary>
public sealed class HardwareConfigService : IHostedService
{
    // Reads both the new camelCase appdata file and the legacy PascalCase project file.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private readonly CsiDspBackgroundService _dsp;
    private readonly ILogger<HardwareConfigService> _logger;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private ArrayGeometryOptions _current = new();
    private bool _loaded;

    public HardwareConfigService(
        CsiDspBackgroundService dsp,
        ILogger<HardwareConfigService> logger)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        _configDirectory = Path.Combine(localAppData, "CsiHub");
        _configFilePath = Path.Combine(_configDirectory, "array-geometry.json");
        _dsp = dsp;
        _logger = logger;
    }

    /// <summary>
    /// Loads the saved geometry and pushes the derived sensor positions to the DSP service.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        PushSensorPositions();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Returns the current array assignment (empty until the user saves one).
    /// </summary>
    public async Task<ArrayGeometryOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return _current;
    }

    /// <summary>
    /// Persists the assignment to appdata and pushes the derived sensor positions
    /// to the DSP service.
    /// </summary>
    public async Task SaveAsync(ArrayGeometryOptions geometry, CancellationToken cancellationToken = default)
    {
        _current = geometry;
        _loaded = true;
        await WriteAsync(geometry, cancellationToken).ConfigureAwait(false);
        PushSensorPositions();
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded)
            {
                return;
            }

            _current = await ReadAsync(cancellationToken).ConfigureAwait(false) ?? new ArrayGeometryOptions();
            _loaded = true;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private Task<ArrayGeometryOptions?> ReadAsync(CancellationToken cancellationToken)
        => TryReadGeometryAsync(_configFilePath, cancellationToken);

    private void PushSensorPositions()
    {
        _dsp.SetSensorPositions(BuildSensorPositions(_current));
    }

    private static Dictionary<string, AoaEstimator.SensorPosition> BuildSensorPositions(ArrayGeometryOptions geometry)
    {
        var positions = new Dictionary<string, AoaEstimator.SensorPosition>(StringComparer.Ordinal);

        var originMac = MacAddressFormatter.ToCanonical(geometry.OriginMac);
        if (!string.IsNullOrEmpty(originMac))
        {
            positions[originMac] = new AoaEstimator.SensorPosition(0.0, 0.0);
        }

        var xArmMac = MacAddressFormatter.ToCanonical(geometry.XArmMac);
        if (!string.IsNullOrEmpty(xArmMac))
        {
            positions[xArmMac] = new AoaEstimator.SensorPosition(geometry.XArmSpacingMeters, 0.0);
        }

        var yArmMac = MacAddressFormatter.ToCanonical(geometry.YArmMac);
        if (!string.IsNullOrEmpty(yArmMac))
        {
            positions[yArmMac] = new AoaEstimator.SensorPosition(0.0, geometry.YArmSpacingMeters);
        }

        return positions;
    }

    private async Task<ArrayGeometryOptions?> TryReadGeometryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ArrayGeometryOptions>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse array geometry file {Path}; starting with empty geometry", path);
            return null;
        }
    }

    private async Task WriteAsync(ArrayGeometryOptions geometry, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_configDirectory);
            var json = JsonSerializer.Serialize(geometry, WriteOptions);
            await File.WriteAllTextAsync(_configFilePath, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }
}
