using BlueRelay.Models;

namespace BlueRelay.Persistence;

public sealed record StateLoadResult(ApplicationState State, string? Warning);
