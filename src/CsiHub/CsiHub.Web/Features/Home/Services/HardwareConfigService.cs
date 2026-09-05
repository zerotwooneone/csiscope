using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CsiHub.Core;
using CsiHub.Ingestion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Persists the user-friendly origin/X-arm/Y-arm geometry assignments to
/// <c>array_geometry.json</c> and keeps a computed <see cref="CsiAoaOptions"/>
/// section in the same file so the DSP service can hot-reload it.
/// </summary>
public sealed class HardwareConfigService
{
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<CsiAoaOptions> _aoaOptions;

    public HardwareConfigService(
        IHostEnvironment environment,
        IConfiguration configuration,
        IOptionsMonitor<CsiAoaOptions> aoaOptions)
    {
        _environment = environment;
        _configuration = configuration;
        _aoaOptions = aoaOptions;
    }

    private string FilePath => Path.Combine(_environment.ContentRootPath, "array_geometry.json");

    /// <summary>
    /// Loads the current geometry assignments. If the file does not exist,
    /// attempts to infer assignments from the current <see cref="CsiAoaOptions"/>.
    /// </summary>
    public Task<ArrayGeometryOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return Task.FromResult(InferFromAoaOptions(_aoaOptions.CurrentValue));
        }

        return LoadFromFileAsync(cancellationToken);
    }

    /// <summary>
    /// Saves geometry assignments to disk, computes the derived sensor positions,
    /// and writes a <see cref="CsiAoaOptions"/> section the DSP service can hot-reload.
    /// </summary>
    public async Task SaveAsync(ArrayGeometryOptions geometry, CancellationToken cancellationToken = default)
    {
        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);

        file ??= new ConfigFile();
        file.ArrayGeometry = geometry;
        file.CsiAoaOptions = MergeWithExistingAoaOptions(geometry, file.CsiAoaOptions ?? _aoaOptions.CurrentValue);

        var json = JsonSerializer.Serialize(file, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = null,
        });

        await File.WriteAllTextAsync(FilePath, json, cancellationToken).ConfigureAwait(false);

        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
        }
    }

    private async Task<ArrayGeometryOptions> LoadFromFileAsync(CancellationToken cancellationToken)
    {
        var file = await ReadFileAsync(cancellationToken).ConfigureAwait(false);
        if (file?.ArrayGeometry is not null)
        {
            return file.ArrayGeometry;
        }

        return InferFromAoaOptions(file?.CsiAoaOptions ?? _aoaOptions.CurrentValue);
    }

    private async Task<ConfigFile?> ReadFileAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ConfigFile>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = null,
        });
    }

    private static CsiAoaOptions MergeWithExistingAoaOptions(ArrayGeometryOptions geometry, CsiAoaOptions? existing)
    {
        var merged = existing is null
            ? new CsiAoaOptions()
            : new CsiAoaOptions
            {
                SensorPositions = new(existing.SensorPositions, StringComparer.OrdinalIgnoreCase),
                CarrierFrequencyHz = existing.CarrierFrequencyHz,
                SpeedOfLight = existing.SpeedOfLight,
                SourceCount = existing.SourceCount,
                StepDegrees = existing.StepDegrees,
                SubcarrierIndex = existing.SubcarrierIndex,
                SampleMaxAge = existing.SampleMaxAge,
            };

        merged.SensorPositions = BuildSensorPositions(geometry);

        if (merged.CarrierFrequencyHz == 0.0)
        {
            merged.CarrierFrequencyHz = new CsiAoaOptions().CarrierFrequencyHz;
        }

        if (merged.SpeedOfLight == 0.0)
        {
            merged.SpeedOfLight = new CsiAoaOptions().SpeedOfLight;
        }

        return merged;
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

    private static ArrayGeometryOptions InferFromAoaOptions(CsiAoaOptions aoa)
    {
        var geometry = new ArrayGeometryOptions();

        foreach (var (mac, position) in aoa.SensorPositions)
        {
            if (Math.Abs(position.X) < 1e-9 && Math.Abs(position.Y) < 1e-9)
            {
                geometry.OriginMac = mac;
            }
            else if (Math.Abs(position.Y) < 1e-9 && position.X > 0)
            {
                geometry.XArmMac = mac;
                geometry.XArmSpacingMeters = position.X;
            }
            else if (Math.Abs(position.X) < 1e-9 && position.Y > 0)
            {
                geometry.YArmMac = mac;
                geometry.YArmSpacingMeters = position.Y;
            }
        }

        return geometry;
    }

    private sealed class ConfigFile
    {
        public ArrayGeometryOptions? ArrayGeometry { get; set; }
        public CsiAoaOptions? CsiAoaOptions { get; set; }
    }
}
