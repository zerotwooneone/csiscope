using System.Collections.Concurrent;
using CsiHub.Ingestion.Pipelines;

namespace CsiHub.Ingestion.IntegrationTests.Fakes;

/// <summary>
/// DI-replaceable <see cref="ISerialPortFactory"/> that hands out loopback
/// <see cref="FakeSerialPort"/> instances for integration tests.
/// </summary>
public sealed class FakeSerialPortFactory : ISerialPortFactory, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, FakeSerialPort> _ports = new();

    public FakeSerialPort GetOrCreate(string portName)
        => _ports.GetOrAdd(portName, _ => new FakeSerialPort(portName));

    public ISerialPort Create(string portName, int baudRate)
        => GetOrCreate(portName);

    public async ValueTask DisposeAsync()
    {
        foreach (var port in _ports.Values)
        {
            try
            {
                await port.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup during test teardown.
            }
        }

        _ports.Clear();
    }
}
