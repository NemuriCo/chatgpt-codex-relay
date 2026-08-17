namespace BlueRelay.Services.Bridges;

public interface IBrowserBridgeServer : IAsyncDisposable
{
    int Port { get; }

    bool IsRunning { get; }

    Task<BridgeOperationResult> StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
