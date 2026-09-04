using CsiHub.Ingestion.Channels;
using CsiHub.Ingestion.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CsiHub.Ingestion;

/// <summary>
/// DI registration helpers for the CsiHub.Ingestion layer.
/// </summary>
public static class CsiIngestionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the singleton <see cref="CsiIngestionChannel"/> and
    /// <see cref="CsiNodePortManager"/> and the background service that drives them.
    /// </summary>
    public static IServiceCollection AddCsiIngestion(this IServiceCollection services)
    {
        services.AddOptions<CsiIngestionOptions>();
        services.AddOptions<CsiAoaOptions>();
        services.AddSingleton<CsiIngestionChannel>();
        services.AddSingleton<ISerialPortFactory, SerialPortAdapterFactory>();
        services.AddSingleton<CsiNodePortManager>();
        services.AddHostedService<CsiIngestionBackgroundService>();

        services.AddSingleton<CsiDspBackgroundService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<CsiDspBackgroundService>());

        return services;
    }

    /// <summary>
    /// Registers ingestion services and binds options from an <see cref="IConfiguration"/> section.
    /// </summary>
    public static IServiceCollection AddCsiIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        AddCsiIngestion(services);
        services.Configure<CsiIngestionOptions>(configuration);

        return services;
    }

    /// <summary>
    /// Registers ingestion services and applies a code-based configuration action.
    /// </summary>
    public static IServiceCollection AddCsiIngestion(this IServiceCollection services, Action<CsiIngestionOptions> configureOptions)
    {
        AddCsiIngestion(services);
        services.Configure(configureOptions);

        return services;
    }
}
