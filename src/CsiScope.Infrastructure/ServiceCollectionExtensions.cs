using System.Collections.Immutable;
using System.IO.Ports;
using CsiScope.Application;
using CsiScope.Application.Ports;
using CsiScope.Domain.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsiScope.Infrastructure;

/// <summary>
/// Wires the sensing stack into DI: pure orchestrator, radio composite,
/// anomaly channel, and the host worker (singleton-forwarded so the UI can
/// inject it and poll <see cref="SensingHostWorker.LatestSnapshot"/>).
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCsiScope(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SensingOptions>(config.GetSection("CsiScope"));
        services.AddSingleton(TimeProvider.System);

        // Radio composite — one IRadioCommandPort surface over all node ports.
        services.AddSingleton<BroadcastRadioAdapter>();
        services.AddSingleton<IRadioCommandPort>(sp => sp.GetRequiredService<BroadcastRadioAdapter>());

        // Anomaly channel — concrete singleton forwarded to both port faces.
        services.AddSingleton<ChannelAnomalySink>();
        services.AddSingleton<IAnomalySink>(sp => sp.GetRequiredService<ChannelAnomalySink>());
        services.AddSingleton<IAnomalySource>(sp => sp.GetRequiredService<ChannelAnomalySink>());

        // Pure core — empty seed roster; nodes self-announce via config/hb/csi.
        services.AddSingleton(sp => new SensingOrchestrator(
            sp.GetRequiredService<IRadioCommandPort>(),
            sp.GetRequiredService<IAnomalySink>(),
            expectedNodes: ImmutableArray<MacAddress>.Empty));

        // Control plane — port enumeration, probing, array-position assignment.
        services.AddSingleton<NodeRegistryService>();

        // Worker — singleton so Dashboard.razor can inject it, then forwarded
        // to the hosted-service registry (legacy singleton-forward pattern).
        // Ports are NOT opened at startup — the UI drives StartSensing.
        services.AddSingleton(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<SensingOptions>>().Value;
            return new SensingHostWorker(
                sp.GetRequiredService<SensingOrchestrator>(),
                sp.GetRequiredService<BroadcastRadioAdapter>(),
                sp.GetRequiredService<TimeProvider>(),
                CreateSerialStreamFactory(opt.SerialBaudRate),
                tickInterval: TimeSpan.FromMilliseconds(opt.TickIntervalMs),
                reconnectDelay: TimeSpan.FromMilliseconds(opt.ReconnectDelayMs),
                logger: sp.GetService<ILogger<SensingHostWorker>>(),
                ackTimeout: TimeSpan.FromMilliseconds(opt.AckTimeoutMs),
                maxAttempts: opt.MaxAttempts);
        });
        services.AddHostedService(sp => sp.GetRequiredService<SensingHostWorker>());

        return services;
    }

    /// <summary>
    /// Opens a real COM port and returns its base stream. DTR/RTS are asserted —
    /// ESP32-S3 USB-CDC won't transmit without them. Disposing the returned
    /// stream closes the port handle (the worker relies on this for teardown).
    /// </summary>
    private static Func<string, CancellationToken, ValueTask<Stream>> CreateSerialStreamFactory(int baudRate)
        => (portName, _) =>
        {
            var port = new SerialPort(portName, baudRate)
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = true,
                ReadTimeout = -1,
                WriteTimeout = -1,
            };
            port.Open();
            return new ValueTask<Stream>(port.BaseStream);
        };
}
