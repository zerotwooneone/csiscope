using System.Collections.Concurrent;
using System.Text.Json;
using CsiHub.Features.Home.Models;

namespace CsiHub.Features.Home.Services;

/// <summary>
/// Thread-safe singleton service that persists node-specific hardware feature flags
/// to a JSON file in the user's local application data directory.
/// </summary>
public sealed class CsiNodeConfigurationService
{
    private readonly string _configDirectory;
    private readonly string _configFilePath;
    private readonly ConcurrentDictionary<string, NodeConfiguration> _configurations = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public CsiNodeConfigurationService()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        _configDirectory = Path.Combine(localAppData, "CsiHub");
        _configFilePath = Path.Combine(_configDirectory, "node-config.json");

        Load();
    }

    /// <summary>
    /// The current feature flag snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, NodeConfiguration> Configurations => _configurations;

    /// <summary>
    /// Attempts to read the saved configuration for a node by MAC address.
    /// </summary>
    public bool TryGetConfiguration(string mac, out NodeConfiguration? configuration)
    {
        return _configurations.TryGetValue(mac, out configuration);
    }

    /// <summary>
    /// Saves or updates a node's configuration and persists it to disk.
    /// </summary>
    public async Task SetConfigurationAsync(NodeConfiguration configuration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(configuration.Mac))
        {
            throw new ArgumentException("MAC address is required.", nameof(configuration));
        }

        _configurations[configuration.Mac] = configuration;

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates the persisted bandwidth for a node, preserving existing feature flags.
    /// </summary>
    public async Task SetBandwidthAsync(string mac, int bandwidth, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mac))
        {
            throw new ArgumentException("MAC address is required.", nameof(mac));
        }

        if (!_configurations.TryGetValue(mac, out var existing) || existing is null)
        {
            existing = new NodeConfiguration { Mac = mac };
        }

        existing.Bandwidth = bandwidth;

        _configurations[mac] = existing;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes a saved configuration and persists the change.
    /// </summary>
    public async Task RemoveConfigurationAsync(string mac, CancellationToken cancellationToken = default)
    {
        _configurations.TryRemove(mac, out _);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Load()
    {
        Directory.CreateDirectory(_configDirectory);

        if (!File.Exists(_configFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_configFilePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var list = JsonSerializer.Deserialize<List<NodeConfiguration>>(json, SerializerOptions);
            if (list is null)
            {
                return;
            }

            foreach (var config in list)
            {
                if (!string.IsNullOrWhiteSpace(config.Mac))
                {
                    _configurations[config.Mac] = config;
                }
            }
        }
        catch (JsonException)
        {
            // If the file is corrupt, start empty.
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(_configDirectory);

            var list = _configurations.Values.ToList();

            await using var stream = new FileStream(
                _configFilePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            await JsonSerializer
                .SerializeAsync(stream, list, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
