using BlueRelay.Models;

namespace BlueRelay.Persistence;

public interface IStateStore
{
    string FilePath { get; }

    Task<StateLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ApplicationState state, CancellationToken cancellationToken = default);
}
